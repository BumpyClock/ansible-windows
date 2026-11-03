using System.Diagnostics;
using System.Numerics;
using Ansible.Core;
using Microsoft.UI;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Ansible;

public sealed partial class MainWindow : Window
{
    private const string SessionNoticeKey = "session";
    private const string MicrophonePermissionNoticeKey = "microphone-permission";
    private const string MeetingSuggestionNoticeKey = "meeting-suggestion";
    private const string TrayErrorNoticeKey = "tray-error";
    private const string ShutdownErrorNoticeKey = "shutdown-error";
    private readonly DictationSession _session;
    private readonly DictationPage _dictation;
    private readonly WelcomePage _welcome;
    private readonly HomeDictationHistory _history;
    private readonly MainPage _settings;
    private readonly ModelManagementPage _models;
    private readonly CustomWordsPage _customWords;
    private readonly MeetingsPage _meetings;
    private readonly MeetingStore _meetingStore;
    private readonly MeetingCleanupService _meetingCleanup;
    private readonly LocalDiagnosticLog _meetingDiagnostics;
    private readonly AppPaths _paths;
    private readonly List<(MeetingSession session, MeetingWindow window, Guid? savedId)> _meetingWindows = [];
    private readonly UiSessionObserver _observer;
    private readonly AppNotices _notices = new();
    private GlobalDictationHotkey? _hotkey;
    private SystemTrayIcon? _tray;
    private DictationShortcut _configuredHotkey = DictationShortcut.Default;
    private readonly ShortcutDictation _shortcut;
    private FloatingDictationWindow? _floating;
    private bool _initialized;
    private bool _compactNavigation;
    private bool _meetingRecoveryComplete;
    private bool _closed;
    private ThemeSettings? _themeSettings;
    private bool _allowClose;
    private bool _closing;
    private bool _popupFailed;
    private bool _creatingFloating;
    // Caret of the shortcut destination, read once at press time. Null for in-app dictation or an unknown caret.
    private CaretBounds? _overlayAnchor;
    private bool? _capturePrivacy;
    private readonly MeetingSuggestions _suggestions;
    private readonly MicrophonePermissionPreflight _microphonePermission;
    private CancellationTokenSource _startupCancellation = new();
    private Task? _startupTask;
    private Task? _meetingRecoveryTask;
    private Task? _permissionRetryTask;
    private Task? _microphoneSettingsTask;
    private bool _launchReady;
    private bool _permissionCheckComplete;
    private MicrophonePermissionStatus? _dismissedMicrophonePermissionStatus;
    // The microphone status the posted notice describes; null while no microphone notice is posted.
    private PageCanvas? _canvas;
    // The current page's primary action mirrored in the title bar, and the property callbacks that keep the copy current.
    private Button? _primaryAction;
    private readonly List<(DependencyObject source, DependencyProperty property, long token)> _primaryActionCallbacks = [];

    internal MainWindow(DictationSession session, AppPaths paths, HttpClient modelDownloads, MicrophonePermissionPreflight microphonePermission,
        LocalDiagnosticLog diagnostics)
    {
        InitializeComponent();
        Notices.Attach(_notices);
        _session = session;
        _microphonePermission = microphonePermission;
        _paths = paths;
        _meetingStore = new MeetingStore(Path.Combine(Path.GetDirectoryName(paths.Settings)!, "meetings"));
        _meetingDiagnostics = diagnostics;
        _meetingCleanup = new(diagnostics);
        _meetings = new MeetingsPage(session, _meetingStore, OpenMeetingAsync, () => WinRT.Interop.WindowNative.GetWindowHandle(this), diagnostics, _notices);
        _history = new HomeDictationHistory(session, Path.Combine(Path.GetDirectoryName(paths.Settings)!, "dictation-history.json"), diagnostics);
        _shortcut = new ShortcutDictation(session,
            new WelcomeShortcutTargetInspector(DispatcherQueue, CurrentWelcome,
                new WindowsShortcutTargetInspector(WinRT.Interop.WindowNative.GetWindowHandle(this))),
            diagnostics, (operationId, status) => _history.RecordDelivery(operationId, status), anchor =>
            {
                _overlayAnchor = anchor;
                if (FloatingPreviewPresentation.ShouldShowWaveform(_session.State)) { _floating?.ShowPreview(_overlayAnchor); }
            });
        _suggestions = new MeetingSuggestions(new Ansible.Core.Windows.TeamsMeetingDetector(),
            () => _session.State.Settings.AutoDetectMeetings, () => MeetingSuggestionsBlocked,
            candidate => _meetingWindows.Any(meeting => meeting.session.IsActive && meeting.window.SelectedMeetingIdentity == candidate),
            (title, message) => _session.Notify(NoticeKind.Warning, title, message, NoticeArea.Meetings));
        _suggestions.Changed += RenderMeetingSuggestion;
        _welcome = new WelcomePage(session, CompleteWelcome, () => Navigate("models"), () => _shortcut.IsBusy);
        _dictation = new DictationPage(session, paths, () => WinRT.Interop.WindowNative.GetWindowHandle(this), _history, _notices);
        _settings = new MainPage(session, paths, () => WinRT.Interop.WindowNative.GetWindowHandle(this),
            ChangeHotkey, ChangeDictationMode, CaptureShortcut, diagnostics, _notices);
        _settings.ShortcutLessonRequested += OpenShortcutLesson;
        _models = new ModelManagementPage(session, paths, () => WinRT.Interop.WindowNative.GetWindowHandle(this), modelDownloads,
            () => { Navigate("dictation"); return _dictation.TranscribeFileAsync(); }, diagnostics, _notices);
        _customWords = new CustomWordsPage(session, () => Navigate("models"), _notices);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        _themeSettings = ThemeSettings.CreateForWindowId(AppWindow.Id);
        _themeSettings.Changed += ContrastThemeChanged;
        RefreshTitleBarColors();
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var size = new Windows.Graphics.SizeInt32(Math.Min(1700, area.Width - 60), Math.Min(1100, area.Height - 60));
        AppWindow.Resize(size);
        AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
        MainContent.Loaded += Initialize;
        Activated += async (_, args) =>
        {
            if (_closed || _closing || args.WindowActivationState == WindowActivationState.Deactivated) return;
            if (_permissionCheckComplete && _permissionRetryTask is not { IsCompleted: false })
            {
                _microphonePermission.CheckAccess();
                RenderMicrophonePermission();
            }
            if (ReferenceEquals(MainContent.Content, _meetings)) await _meetings.RefreshAsync();
        };
        AppWindow.Closing += CloseRequested;
        Closed += WindowClosed;
        AddTitleBarAnimations();
        Navigate("dictation");
        _observer = new UiSessionObserver(session, DispatcherQueue, SessionChanged);
        try { EnsureTrayIcon(); }
        catch (Exception error) { TrayFailed(error); }
    }

    private async void Initialize(object sender, RoutedEventArgs args)
    {
        if (_initialized) { return; }
        _initialized = true;
        _startupTask = InitializeAsync(_startupCancellation.Token);
        _meetingRecoveryTask = RecoverMeetingsAsync(_startupCancellation.Token);
        await _startupTask;
    }

    private async Task InitializeAsync(CancellationToken token)
    {
        var startupGeneration = _meetingDiagnostics.CurrentGeneration;
        var startupStarted = startupGeneration is null ? 0 : Stopwatch.GetTimestamp();
        try
        {
            // Let the initial Window.Activate finish before Windows can display consent.
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            var permissionStarted = startupGeneration is null ? 0 : Stopwatch.GetTimestamp();
            Exception? permissionFailure = null;
            try { await _microphonePermission.InitializeAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                permissionFailure = error;
                // The preflight retains Unavailable and blocks capture. File/model workflows still initialize.
                Debug.WriteLine($"Ansible: microphone permission preflight failed, HRESULT 0x{error.HResult:X8}.");
            }
            finally
            {
                if (startupGeneration is { } permissionGeneration)
                    _meetingDiagnostics.Write(new ApplicationDiagnostic(DateTimeOffset.UtcNow, ApplicationDiagnosticStage.MicrophonePermission,
                        token.IsCancellationRequested ? DiagnosticOutcome.Cancelled :
                        permissionFailure is null ? DiagnosticOutcome.Completed : DiagnosticOutcome.Failed,
                        Stopwatch.GetElapsedTime(permissionStarted).TotalMilliseconds, permissionFailure?.HResult,
                        _microphonePermission.Status), permissionGeneration);
            }
            token.ThrowIfCancellationRequested();
            _permissionCheckComplete = true;
            RenderMicrophonePermission();
            await _history.InitializeAsync();
            await _session.InitializeAsync();
            var diagnosticsGeneration = _meetingDiagnostics.CurrentGeneration;
            token.ThrowIfCancellationRequested();
            if (_closed || _closing)
            {
                LogApplication(ApplicationDiagnosticStage.Startup, DiagnosticOutcome.Cancelled, startupGeneration, startupStarted);
                return;
            }
            _launchReady = true;
            var hotkeyStarted = diagnosticsGeneration is not null ? Stopwatch.GetTimestamp() : 0;
            try
            {
                _configuredHotkey = _session.State.Settings.Shortcut;
                _hotkey = new GlobalDictationHotkey(WinRT.Interop.WindowNative.GetWindowHandle(this),
                    _configuredHotkey, HotkeyChanged);
                LogApplication(ApplicationDiagnosticStage.Hotkey, DiagnosticOutcome.Completed, diagnosticsGeneration, hotkeyStarted);
            }
            catch (Exception error)
            {
                LogApplication(ApplicationDiagnosticStage.Hotkey, DiagnosticOutcome.Failed, diagnosticsGeneration, hotkeyStarted, error);
                _session.Notify(NoticeKind.Error, "Shortcut unavailable", error.Message, NoticeArea.Settings | NoticeArea.Dictation);
            }
            if (!_session.State.Settings.WelcomeCompleted) Navigate("welcome");
            else if (_session.State.Phase == DictationPhase.Disconnected && _session.State.Models.Count == 0) Navigate("models");
            _suggestions.Start();
            LogApplication(ApplicationDiagnosticStage.Startup, DiagnosticOutcome.Completed, startupGeneration, startupStarted);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            LogApplication(ApplicationDiagnosticStage.Startup, DiagnosticOutcome.Cancelled, startupGeneration, startupStarted);
        }
        catch (Exception error)
        {
            LogApplication(ApplicationDiagnosticStage.Startup, DiagnosticOutcome.Failed, startupGeneration, startupStarted, error);
            _session.ReportUiError(error, NoticeArea.Application);
        }
    }

    private void LogApplication(ApplicationDiagnosticStage stage, DiagnosticOutcome outcome,
        long? generation, long startedAt, Exception? error = null)
    {
        if (generation is { } value)
            _meetingDiagnostics.Write(new ApplicationDiagnostic(DateTimeOffset.UtcNow, stage, outcome,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, error?.HResult), value);
    }

    private async Task RecoverMeetingsAsync(CancellationToken token)
    {
        SessionNotice? notice = null;
        try
        {
            var failures = await Task.Run(() => _meetingStore.MarkInterruptedSessionsAsync(token), token);
            token.ThrowIfCancellationRequested();
            _meetingRecoveryComplete = true;
            if (failures.Count > 0)
                notice = new(NoticeKind.Warning, "Some saved transcripts unavailable",
                    "Open Meetings to export and remove unreadable files. New meetings are still available.", NoticeArea.Meetings);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception error) { notice = new(NoticeKind.Error, "Meeting recovery failed", error.Message, NoticeArea.Meetings); }
        // Initialization publishes its own notice. Report recovery problems afterward without delaying dictation.
        if (notice is not null && _startupTask is not null) await _startupTask;
        if (token.IsCancellationRequested || _closed || _closing) return;
        if (notice is not null) _session.Notify(notice.Kind, notice.Title, notice.Message, notice.Area);
        if (ReferenceEquals(MainContent.Content, _meetings)) await _meetings.RefreshAsync();
    }

    private void RenderMicrophonePermission()
    {
        if (_closing || _closed) return;
        if (!_permissionCheckComplete) return;
        var status = _microphonePermission.Status;
        var allowed = status == MicrophonePermissionStatus.Allowed;
        if (allowed || (_dismissedMicrophonePermissionStatus is { } dismissed && dismissed != status))
            _dismissedMicrophonePermissionStatus = null;
        if (allowed || _dismissedMicrophonePermissionStatus == status)
        {
            _notices.Set(MicrophonePermissionNoticeKey, null);
            return;
        }
        var message = status switch
        {
            MicrophonePermissionStatus.DeniedByUser => "Enable microphone access for Ansible and desktop apps in Windows Settings, then check again.",
            MicrophonePermissionStatus.DeniedBySystem => "Windows is blocking microphone access. Check Windows Settings or contact your administrator.",
            MicrophonePermissionStatus.NotDeclared => "This package does not declare microphone access. Install an Ansible package with microphone capability.",
            MicrophonePermissionStatus.PromptRequired => "Microphone access is required. Check again to request access.",
            _ => "Microphone access could not be checked. Review Windows privacy settings, then check again."
        };
        _notices.Set(MicrophonePermissionNoticeKey, new NoticeContent(NoticeSeverity.Warning, "Microphone access required", message,
            [new("Open settings", OpenMicrophoneSettings), new("Check again", CheckMicrophonePermission)]),
            dismissed: () =>
            {
                // Dismissal holds for this status only; a different failure, or opening Settings, shows it again.
                if (_microphonePermission.Status != MicrophonePermissionStatus.Allowed)
                    _dismissedMicrophonePermissionStatus = _microphonePermission.Status;
            });
    }

    // A check already in progress ignores the request; the notice keeps its actions rather than reflowing.
    private async void CheckMicrophonePermission()
    {
        if (_closing || _closed || !_permissionCheckComplete || _permissionRetryTask is { IsCompleted: false }) return;
        _permissionRetryTask = RetryMicrophonePermissionAsync();
        await _permissionRetryTask;
    }

    private async Task RetryMicrophonePermissionAsync()
    {
        _dismissedMicrophonePermissionStatus = null;
        try { await _microphonePermission.InitializeAsync(_startupCancellation.Token); }
        catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested) { }
        catch (Exception error) { if (!_closing) _session.ReportUiError(error, NoticeArea.Application); }
        finally { RenderMicrophonePermission(); }
    }

    private async void OpenMicrophoneSettings()
    {
        if (_closing || _closed || _microphoneSettingsTask is { IsCompleted: false }) return;
        _microphoneSettingsTask = OpenMicrophoneSettingsAsync();
        await _microphoneSettingsTask;
    }

    private async Task OpenMicrophoneSettingsAsync()
    {
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone")) && !_closing)
                _session.Notify(NoticeKind.Warning, "Windows Settings unavailable", "Open Windows Settings > Privacy & security > Microphone.", NoticeArea.Application);
        }
        catch (Exception error) { if (!_closing) _session.ReportUiError(error, NoticeArea.Application); }
    }

    private void NavigateClicked(object sender, RoutedEventArgs args) => Navigate((string)((Button)sender).Tag);

    private Button[] NavigationButtons => [DictationNav, MeetingsNav, ModelsNav, CustomWordsNav, SettingsNav];

    // The collapsed title and action fade and rise into the title bar, and fade out. Skipped when Windows
    // animations are off.
    private void AddTitleBarAnimations()
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var resources = Application.Current.Resources;
        var duration = TimeSpan.FromMilliseconds((double)resources["MotionNormalMilliseconds"]);
        var compositor = ElementCompositionPreview.GetElementVisual(ShellRoot).Compositor;
        foreach (UIElement element in new UIElement[] { TitleBarPageTitle, TitleBarPrimaryAction })
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var fadeIn = compositor.CreateScalarKeyFrameAnimation();
            fadeIn.Target = "Opacity";
            fadeIn.InsertKeyFrame(0, 0);
            fadeIn.InsertKeyFrame(1, 1);
            fadeIn.Duration = duration;
            var rise = compositor.CreateVector3KeyFrameAnimation();
            rise.Target = "Translation";
            rise.InsertKeyFrame(0, new Vector3(0, 4, 0));
            rise.InsertKeyFrame(1, Vector3.Zero);
            rise.Duration = duration;
            var show = compositor.CreateAnimationGroup();
            show.Add(fadeIn);
            show.Add(rise);
            ElementCompositionPreview.SetImplicitShowAnimation(element, show);
            var fadeOut = compositor.CreateScalarKeyFrameAnimation();
            fadeOut.Target = "Opacity";
            fadeOut.InsertKeyFrame(1, 0);
            fadeOut.Duration = duration;
            ElementCompositionPreview.SetImplicitHideAnimation(element, fadeOut);
        }
    }

    private void ShellSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var compact = args.NewSize.Width < (double)Application.Current.Resources["CompactNavigationThreshold"];
        if (_compactNavigation == compact) return;
        _compactNavigation = compact;
        var resources = Application.Current.Resources;
        SidebarColumn.Width = TitleBarBrandColumn.Width = (GridLength)resources[compact ? "CompactSidebarColumnWidth" : "SidebarColumnWidth"];
        Sidebar.Padding = (Thickness)resources[compact ? "CompactSidebarPadding" : "SidebarPadding"];
        AppWordmark.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Brand.Margin = compact ? new Thickness(0) : (Thickness)resources["SidebarBrandMargin"];
        Brand.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        TitleBarPageTitle.Margin = (Thickness)resources[compact ? "CompactTitleBarPageTitleMargin" : "TitleBarPageTitleMargin"];
        foreach (var button in NavigationButtons)
        {
            // A local value outlasts Navigate's style swaps, so the icon stays centred in the 72 px column.
            button.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            if (button.Content is StackPanel content)
            {
                content.Spacing = compact ? 0 : (double)Application.Current.Resources["NavLabelGap"];
                foreach (var label in content.Children.OfType<TextBlock>())
                    label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            }
            ToolTipService.SetToolTip(button, compact ? AutomationProperties.GetName(button) : null);
        }
    }

    private void Navigate(string page)
    {
        if (page == "settings" && _permissionCheckComplete && _microphonePermission.Status != MicrophonePermissionStatus.Allowed)
        {
            _dismissedMicrophonePermissionStatus = null;
            RenderMicrophonePermission();
        }
        if (page == "dictation" && _launchReady && !_session.State.Settings.WelcomeCompleted) page = "welcome";
        Page target = page switch { "welcome" => _welcome, "settings" => _settings, "dictation" => _dictation, "models" => _models,
            "custom-words" => _customWords, "meetings" => _meetings, _ => _dictation };
        // Reselecting the current page keeps its state and scroll position.
        if (ReferenceEquals(MainContent.Content, target)) return;
        MainContent.Content = target;
        foreach (var (button, indicator) in new[]
        {
            (DictationNav, DictationIndicator), (ModelsNav, ModelsIndicator),
            (MeetingsNav, MeetingsIndicator),
            (CustomWordsNav, CustomWordsIndicator),
            (SettingsNav, SettingsIndicator),
        })
        {
            var selected = (string)button.Tag == (page == "welcome" ? "dictation" : page);
            button.Style = (Style)Application.Current.Resources[
                selected ? "SelectedNavigationButtonStyle" : "NavigationButtonStyle"];
            indicator.Style = (Style)Application.Current.Resources[
                selected ? "SelectedNavigationIconStyle" : "NavigationIconStyle"];
            AutomationProperties.SetHelpText(button, selected ? "Current page" : "");
            AutomationProperties.SetItemStatus(button, selected ? "Current page" : "");
        }
        var canvas = PageCanvas.Find(target);
        if (!ReferenceEquals(_canvas, canvas))
        {
            if (_canvas is not null) _canvas.HeadingScrolledOutChanged -= HeadingScrolledOutChanged;
            _canvas = canvas;
            if (canvas is not null) canvas.HeadingScrolledOutChanged += HeadingScrolledOutChanged;
        }
        Notices.ShowPage(page, canvas);
        // The title bar names the page as the sidebar does; the page heading may be a greeting instead.
        TitleBarPageTitle.Text = NavigationButtons.FirstOrDefault(button => (string)button.Tag == page) is { } current
            ? AutomationProperties.GetName(current) : "";
        RenderTitleBar();
    }

    private void HeadingScrolledOutChanged(object? sender, EventArgs args) => RenderTitleBar();

    // Once the page heading scrolls away, the title bar shows the page name and a compact copy of the page's
    // primary action. The copy invokes the original, so pages need no title bar API.
    private void RenderTitleBar()
    {
        if (_closed) return;
        var scrolled = _canvas is { IsHeadingScrolledOut: true } && TitleBarPageTitle.Text.Length > 0;
        var action = scrolled ? _canvas!.FindPrimaryAction() : null;
        BindPrimaryAction(action);
        var showAction = action is not null && IsDisplayed(action);
        if (action is not null && showAction)
        {
            var label = PrimaryActionLabel(action);
            TitleBarPrimaryAction.Content = label;
            TitleBarPrimaryAction.IsEnabled = action.IsEnabled;
            AutomationProperties.SetName(TitleBarPrimaryAction,
                AutomationProperties.GetName(action) is { Length: > 0 } name ? name : label);
        }
        var changed = SetShown(TitleBarPageTitle, scrolled) | SetShown(TitleBarPrimaryAction, showAction);
        // The TitleBar keeps visible controls in its content clickable; refresh its regions after the button toggles.
        if (changed) DispatcherQueue.TryEnqueue(() => { if (!_closed) AppTitleBar.RecomputeDragRegions(); });
    }

    private static bool SetShown(UIElement element, bool shown)
    {
        var visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (element.Visibility == visibility) return false;
        element.Visibility = visibility;
        return true;
    }

    private bool IsDisplayed(FrameworkElement element)
    {
        for (DependencyObject? current = element; current is not null && !ReferenceEquals(current, _canvas);
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: Visibility.Collapsed }) return false;
        }
        return true;
    }

    private static string PrimaryActionLabel(Button button) =>
        button.Content as string ?? FirstText(button.Content as DependencyObject) ?? AutomationProperties.GetName(button);

    private static string? FirstText(DependencyObject? root)
    {
        if (root is null) return null;
        var pending = new Queue<DependencyObject>([root]);
        while (pending.TryDequeue(out var current))
        {
            if (current is TextBlock { Visibility: Visibility.Visible, Text.Length: > 0 } text) return text.Text;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                pending.Enqueue(VisualTreeHelper.GetChild(current, index));
        }
        return null;
    }

    private void BindPrimaryAction(Button? action)
    {
        if (ReferenceEquals(_primaryAction, action)) return;
        foreach (var (source, property, token) in _primaryActionCallbacks)
            source.UnregisterPropertyChangedCallback(property, token);
        _primaryActionCallbacks.Clear();
        _primaryAction = action;
        if (action is null) return;
        void Watch(DependencyObject source, DependencyProperty property) => _primaryActionCallbacks.Add((source, property,
            source.RegisterPropertyChangedCallback(property, (_, _) => DispatcherQueue.TryEnqueue(RenderTitleBar))));
        Watch(action, ContentControl.ContentProperty);
        Watch(action, Control.IsEnabledProperty);
        Watch(action, UIElement.VisibilityProperty);
        Watch(action, FrameworkElement.StyleProperty);
        Watch(action, AutomationProperties.NameProperty);
        if (action.Content is DependencyObject content)
        {
            var pending = new Queue<DependencyObject>([content]);
            while (pending.TryDequeue(out var current))
            {
                if (current is TextBlock text) Watch(text, TextBlock.TextProperty);
                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                    pending.Enqueue(VisualTreeHelper.GetChild(current, index));
            }
        }
    }

    private void TitleBarPrimaryActionClicked(object sender, RoutedEventArgs args)
    {
        if (_primaryAction is { IsEnabled: true } action
            && FrameworkElementAutomationPeer.CreatePeerForElement(action)?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
            invoke.Invoke();
    }

    // The panel's border draws rounded corners but does not clip; clip the page so its scrollbar and content stay
    // inside the corners.
    private void MainContentSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var visual = ElementCompositionPreview.GetElementVisual(MainContent);
        var radius = new Vector2((float)Math.Max(0, ContentPanel.CornerRadius.TopLeft - ContentPanel.BorderThickness.Left));
        visual.Clip = visual.Compositor.CreateRectangleClip(0, 0, (float)args.NewSize.Width, (float)args.NewSize.Height,
            radius, radius, radius, radius);
    }

    // A window hidden to the tray or minimized is not showing the lesson, so it must not hold the global shortcut.
    // AppWindow.IsVisible stays true for a minimized window, so the presenter state is checked separately.
    private WelcomePage? CurrentWelcome() =>
        !_closed && AppWindow.IsVisible
        && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized }
        && ReferenceEquals(MainContent.Content, _welcome) ? _welcome : null;

    private void OpenShortcutLesson()
    {
        if (!_session.State.IsIdle || _shortcut.IsBusy)
        {
            _session.Notify(NoticeKind.Warning, "Finish dictation first",
                "Wait for the current dictation and insertion to finish before opening the shortcut lesson.",
                NoticeArea.Settings);
            return;
        }
        _welcome.RestartLesson();
        Navigate("welcome");
    }

    private void CompleteWelcome()
    {
        try
        {
            if (!_session.State.IsIdle || _shortcut.IsBusy) return;
            _session.CompleteWelcome();
            Navigate("dictation");
        }
        catch (Exception error) { _session.ReportUiError(error); }
    }

    private void ShellThemeChanged(FrameworkElement sender, object args) => RefreshTitleBarColors();
    private void ContrastThemeChanged(ThemeSettings sender, object args) => RefreshTitleBarColors();

    private void RefreshTitleBarColors()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed || AppTitleBar is null) return;
            AppWindow.TitleBar.ButtonForegroundColor = ((SolidColorBrush)AppTitleBar.Foreground).Color;
            AppWindow.TitleBar.ButtonInactiveForegroundColor = ((SolidColorBrush)ShellRoot.BorderBrush).Color;
        });
    }

    private async Task OpenMeetingAsync(MeetingDocument? document)
    {
        if (_meetingRecoveryTask is not null) await _meetingRecoveryTask;
        if (!_meetingRecoveryComplete) throw new InvalidOperationException("Meeting storage must initialize successfully before opening a meeting.");
        if (_closing || _closed) return;
        OpenMeeting(document).Activate();
    }

    private MeetingWindow OpenMeeting(MeetingDocument? document)
    {
        var existing = _meetingWindows.FirstOrDefault(item => document is null
            ? item.savedId is null && (item.session.IsActive || item.session.Document is null)
            : item.savedId == document.Id || item.session.Document?.Id == document.Id);
        if (existing.window is not null) return existing.window;
        var meeting = new MeetingSession(
            () => new NativeAudioEngine(_paths.NativeLibrary, _paths.ModelCatalog, _session.State.ModelsDirectory,
                deviceProbePath: _paths.DeviceProbe, benchmarkSamplePath: _paths.PublicSample,
                benchmarkCachePath: _paths.DeviceBenchmarks),
            new PermissionCheckedMeetingCaptureFactory(new Ansible.Core.Windows.MeetingCaptureFactory(_meetingDiagnostics), _microphonePermission), _meetingStore,
            () =>
            {
                var current = _session.State;
                return new RecognitionOptions(current.Language, current.Settings.CustomDictionary, current.Settings.Backend);
            },
            reserve: async () =>
            {
                if (!_launchReady || !_meetingRecoveryComplete)
                    throw new InvalidOperationException("Meeting storage must initialize successfully before starting a meeting.");
                if (_closing || _shortcut.IsBusy)
                    throw new InvalidOperationException("Finish dictation before starting a meeting.");
                await _session.ReserveMeetingAsync();
            },
            release: _session.ReleaseMeetingAsync,
            observerFactory: () => new Ansible.Core.Windows.TeamsSpeakerObserver(),
            alignerFactory: () => new NativeMeetingWordAligner(_paths.NativeLibrary, _paths.ModelCatalog, _session.State.ModelsDirectory),
            diagnostics: _meetingDiagnostics, textCleanup: _meetingCleanup,
            getCleanupSettings: () => _session.State.Settings.MeetingCleanup, getModelId: () =>
            {
                var current = _session.State;
                return ModelSelectionPolicy.ExplicitMeetingModelId(current.SelectedModel, current.Settings.ModelId);
            });
        var window = new MeetingWindow(meeting, _meetingStore, document, ShellRoot.RequestedTheme, _meetingDiagnostics.DirectoryPath,
            _meetingCleanup, () => _session.State.Settings.MeetingCleanup);
        try { window.ApplyCapturePrivacy(_session.State.Settings.HideFromCapture); }
        catch (Exception error) { _session.Notify(NoticeKind.Warning, "Screen capture privacy unavailable", error.Message, NoticeArea.Application); }
        _meetingWindows.Add((meeting, window, document?.Id));
        meeting.StateChanged += state => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed || _closing) return;
            _suggestions.Refresh();
        });
        window.Closed += async (_, _) =>
        {
            try
            {
                await meeting.DisposeAsync();
                _meetingWindows.RemoveAll(item => ReferenceEquals(item.window, window));
                if (ReferenceEquals(MainContent.Content, _meetings)) await _meetings.RefreshAsync();
            }
            catch (Exception error) { _session.ReportUiError(error, NoticeArea.Meetings); }
        };
        return window;
    }

    private bool MeetingSuggestionsBlocked => !_launchReady || !_meetingRecoveryComplete || !_session.State.IsIdle ||
        _shortcut.IsBusy ||
        _meetingWindows.Any(meeting => meeting.session.IsActive);

    private void RenderMeetingSuggestion()
    {
        // Open notetaker is ignored while a confirmation runs; MeetingSuggestions guards the second request.
        _notices.Set(MeetingSuggestionNoticeKey, _suggestions.Offer is null ? null : new NoticeContent(NoticeSeverity.Information,
            "Teams microphone is active.", "Start local notetaking? Recording starts only when you click Start.",
            [new("Open notetaker", OpenSuggestedMeeting)]), dismissed: _suggestions.Dismiss);
    }

    private async void OpenSuggestedMeeting()
    {
        var outcome = await _suggestions.ConfirmAsync(candidate =>
        {
            var window = OpenMeeting(null);
            if (!window.SelectDetectedMeeting(candidate)) return false;
            window.Activate(); // Opening the notetaker does not start capture.
            return true;
        });
        if (!_closing && !_closed && outcome is MeetingSuggestionOutcome.Rejected or MeetingSuggestionOutcome.Failed)
            Navigate("meetings");
    }

    private void ChangeHotkey(DictationShortcut choice)
    {
        if (!_session.State.IsIdle || _shortcut.IsBusy)
            throw new InvalidOperationException("Finish dictation before changing its shortcut.");
        var replacement = new GlobalDictationHotkey(WinRT.Interop.WindowNative.GetWindowHandle(this),
            choice, HotkeyChanged);
        _hotkey?.Dispose();
        _hotkey = replacement;
        _configuredHotkey = choice;
        _session.SetShortcut(choice);
    }

    private void ChangeDictationMode(bool pushToTalk)
    {
        if (!_session.State.IsIdle || _shortcut.IsBusy)
            throw new InvalidOperationException("Finish dictation before changing its activation mode.");
        _session.SetPushToTalk(pushToTalk);
    }

    private void CaptureShortcut(bool capturing)
    {
        if (capturing)
        {
            if (!_session.State.IsIdle || _shortcut.IsBusy)
                throw new InvalidOperationException("Finish dictation before recording a shortcut.");
            _hotkey?.Suspend();
        }
        else { _hotkey?.Resume(); }
    }

    private void SessionChanged(SessionSnapshot state)
    {
        if (_closed) { return; }
        var notice = state.Notice;
        _notices.Set(SessionNoticeKey, notice.ShouldDisplay ? new NoticeContent(
            notice.Kind == NoticeKind.Error ? NoticeSeverity.Error : NoticeSeverity.Warning, notice.Title, notice.Message) : null,
            notice.Area, () => _session.DismissNotice(notice));
        RenderMicrophonePermission();
        _suggestions.Refresh();
        if (_capturePrivacy != state.Settings.HideFromCapture)
        {
            _capturePrivacy = state.Settings.HideFromCapture;
            var failures = new List<string>();
            foreach (var meeting in _meetingWindows.ToArray())
            {
                try { meeting.window.ApplyCapturePrivacy(state.Settings.HideFromCapture); }
                catch (Exception error) { failures.Add(error.Message); }
            }
            try { _floating?.ApplyCapturePrivacy(state.Settings.HideFromCapture); }
            catch (Exception error) { failures.Add(error.Message); }
            if (failures.Count > 0)
                _session.Notify(NoticeKind.Warning, "Screen capture privacy unavailable", string.Join(" ", failures.Distinct()), NoticeArea.Application);
        }
        var showWaveform = FloatingPreviewPresentation.ShouldShowWaveform(state);
        if (!showWaveform)
        {
            _popupFailed = false;
            if (!state.IsLiveOperation) { _overlayAnchor = null; }
        }
        if (showWaveform && _floating is null && !_popupFailed && !_creatingFloating)
        {
            _creatingFloating = true;
            try
            {
                _floating = new FloatingDictationWindow(_session, this);
                _floating.Closed += (_, _) => _floating = null;
            }
            catch (Exception error)
            {
                _popupFailed = true;
                _session.ReportUiError(new InvalidOperationException("The floating preview could not be opened.", error));
            }
            finally { _creatingFloating = false; }
        }
        if (showWaveform) { _floating?.ShowPreview(_overlayAnchor); }
        else { _floating?.HidePreview(); }
    }

    private async void HotkeyChanged(bool pressed)
    {
        try
        {
            if (pressed && CurrentWelcome() is { AcceptsShortcut: false }) return;
            if (pressed) { await _shortcut.PressedAsync(_configuredHotkey); }
            else { await _shortcut.ReleasedAsync(); }
        }
        catch (Exception error) { _session.ReportUiError(error); }
        finally { CurrentWelcome()?.RefreshAfterShortcut(); }
    }

    private void CloseRequested(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) { return; }
        args.Cancel = true;
        if (_closing) { return; }
        try
        {
            EnsureTrayIcon();
            AppWindow.Hide();
        }
        catch (Exception error) { TrayFailed(error); }
    }

    private void EnsureTrayIcon()
    {
        _tray ??= new SystemTrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this),
            Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), ShowFromTray, ExitRequested, TrayFailed);
        _tray.EnsureVisible();
        _notices.Set(TrayErrorNoticeKey, null);
    }

    private void ShowFromTray()
    {
        if (_closed) return;
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    private void TrayFailed(Exception error)
    {
        Debug.WriteLine($"Ansible: tray unavailable: {error}");
        ShowFromTray();
        _notices.Set(TrayErrorNoticeKey, new NoticeContent(NoticeSeverity.Error, "Tray icon unavailable",
            $"{error.Message} Try closing the window again, or choose Exit Ansible to stop the app.",
            [new("Exit Ansible", ExitRequested)], IsClosable: false));
    }

    private async void ExitRequested()
    {
        if (_closing) { return; }
        _closing = true;
        MainContent.IsEnabled = false;
        _notices.Set(ShutdownErrorNoticeKey, null);
        _shortcut.BeginShutdown();
        _suggestions.BeginShutdown();
        _startupCancellation.Cancel();
        var diagnosticGeneration = _meetingDiagnostics.CurrentGeneration;
        var diagnosticStartedAt = diagnosticGeneration is not null ? Stopwatch.GetTimestamp() : 0;
        try
        {
            if (_startupTask is not null) await _startupTask;
            if (_meetingRecoveryTask is not null) await _meetingRecoveryTask;
            if (_permissionRetryTask is not null) await _permissionRetryTask;
            if (_microphoneSettingsTask is not null) await _microphoneSettingsTask;
            await _meetings.BeginShutdownAsync();
            await _suggestions.JoinAsync();
            var meetings = _meetingWindows.ToArray();
            await Task.WhenAll(meetings.Select(meeting => meeting.window.BeginShutdownAsync()));
            foreach (var meeting in meetings) await meeting.session.DisposeAsync();
            foreach (var meeting in _meetingWindows.ToArray()) meeting.window.Close();
            await Task.WhenAll(_session.CloseAsync(), _models.DisposeAsync().AsTask(), _dictation.FinishBackgroundWorkAsync());
            await _shortcut.JoinAsync();
            if (_floating is not null) { await _floating.ClosePreviewAsync(); }
            await _history.DisposeAsync();
            LogApplication(ApplicationDiagnosticStage.Shutdown, DiagnosticOutcome.Completed, diagnosticGeneration, diagnosticStartedAt);
            await _meetingDiagnostics.DisposeAsync();
            _allowClose = true;
            Close();
        }
        catch (Exception error)
        {
            ShowFromTray();
            LogApplication(ApplicationDiagnosticStage.Shutdown, DiagnosticOutcome.Failed, diagnosticGeneration, diagnosticStartedAt, error);
            Debug.WriteLine($"Ansible: native shutdown needs recovery: {error.Message}");
            ShowShutdownError($"{error.Message} Waiting for owned work to stop.");
            var failures = new List<Exception> { error };
            _suggestions.BeginShutdown();
            try { await _session.CancelAsync(); }
            catch (Exception stopError) { failures.Add(stopError); }
            var joined = Task.WhenAll(new[]
            {
                _startupTask, _meetingRecoveryTask, _permissionRetryTask, _microphoneSettingsTask,
                _suggestions.JoinAsync(), _shortcut.JoinAsync()
            }.OfType<Task>());
            try { await joined; }
            catch (Exception)
            {
                if (joined.Exception is { } joinError) failures.AddRange(joinError.Flatten().InnerExceptions);
            }
            _shortcut.ResumeAfterFailedShutdown();
            _startupCancellation.Dispose();
            _startupCancellation = new CancellationTokenSource();
            var failure = failures.Count == 1 ? error : new AggregateException("Shutdown and recovery reported failures.", failures);
            ShowShutdownError($"{failure.Message} Try Exit from the tray menu again.");
            _closing = false;
            MainContent.IsEnabled = true;
            _meetings.ResumeAfterFailedShutdown();
            _permissionCheckComplete = true;
            RenderMicrophonePermission();
            _suggestions.ResumeAfterFailedShutdown();
        }
    }

    private void ShowShutdownError(string message) => _notices.Set(ShutdownErrorNoticeKey,
        new NoticeContent(NoticeSeverity.Error, "Close failed", message, IsClosable: false));

    private void WindowClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        if (_themeSettings is not null) _themeSettings.Changed -= ContrastThemeChanged;
        _themeSettings = null;
        _tray?.Dispose();
        _dictation.Dispose();
        _history.Dispose();
        _hotkey?.Dispose();
        _shortcut.Dispose();
        _startupCancellation.Dispose();
        _suggestions.Dispose();
        _observer.Dispose();
        AppWindow.Closing -= CloseRequested;
    }
}
