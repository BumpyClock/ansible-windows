using System.Diagnostics;
using Ansible.Core;
using Microsoft.UI;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ansible;

public sealed partial class MainWindow : Window
{
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

    internal MainWindow(DictationSession session, AppPaths paths, HttpClient modelDownloads, MicrophonePermissionPreflight microphonePermission,
        LocalDiagnosticLog diagnostics)
    {
        InitializeComponent();
        _session = session;
        _microphonePermission = microphonePermission;
        _paths = paths;
        _meetingStore = new MeetingStore(Path.Combine(Path.GetDirectoryName(paths.Settings)!, "meetings"));
        _meetingDiagnostics = diagnostics;
        _meetingCleanup = new(diagnostics);
        _meetings = new MeetingsPage(session, _meetingStore, OpenMeetingAsync, () => WinRT.Interop.WindowNative.GetWindowHandle(this), diagnostics);
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
        _dictation = new DictationPage(session, paths, () => WinRT.Interop.WindowNative.GetWindowHandle(this), _history);
        _dictation.ShortcutLessonRequested += OpenShortcutLesson;
        _settings = new MainPage(session, paths, () => WinRT.Interop.WindowNative.GetWindowHandle(this),
            ChangeHotkey, ChangeDictationMode, CaptureShortcut, diagnostics);
        _models = new ModelManagementPage(session, paths, () => WinRT.Interop.WindowNative.GetWindowHandle(this), modelDownloads,
            () => { Navigate("dictation"); return _dictation.TranscribeFileAsync(); }, diagnostics);
        _customWords = new CustomWordsPage(session, () => Navigate("models"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
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
        AddNoticeShowAnimations();
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
        MicrophonePermissionMessage.Text = status switch
        {
            MicrophonePermissionStatus.DeniedByUser => "Enable microphone access for Ansible and desktop apps in Windows Settings, then check again.",
            MicrophonePermissionStatus.DeniedBySystem => "Windows is blocking microphone access. Check Windows Settings or contact your administrator.",
            MicrophonePermissionStatus.NotDeclared => "This package does not declare microphone access. Install an Ansible package with microphone capability.",
            MicrophonePermissionStatus.PromptRequired => "Microphone access is required. Check again to request access.",
            _ => "Microphone access could not be checked. Review Windows privacy settings, then check again."
        };
        MicrophonePermissionNotice.Visibility = allowed || _dismissedMicrophonePermissionStatus == status
            ? Visibility.Collapsed : Visibility.Visible;
        CheckMicrophonePermissionButton.IsEnabled = _permissionRetryTask is not { IsCompleted: false };
    }

    private void MicrophonePermissionNoticeDismissed(object sender, RoutedEventArgs args)
    {
        if (_microphonePermission.Status != MicrophonePermissionStatus.Allowed)
            _dismissedMicrophonePermissionStatus = _microphonePermission.Status;
        MicrophonePermissionNotice.Visibility = Visibility.Collapsed;
    }

    private void MicrophonePermissionNoticeSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var available = MicrophonePermissionActions.ActualWidth;
        if (available <= 0) return;
        var gap = (double)Application.Current.Resources["NavItemGap"];
        var required = MicrophonePermissionActions.Children.OfType<FrameworkElement>()
            .Sum(child => child.DesiredSize.Width) + gap * Math.Max(0, MicrophonePermissionActions.Children.Count - 1);
        var orientation = required <= available ? Orientation.Horizontal : Orientation.Vertical;
        if (MicrophonePermissionActions.Orientation != orientation)
            MicrophonePermissionActions.Orientation = orientation;
    }

    private async void CheckMicrophonePermissionClicked(object sender, RoutedEventArgs args)
    {
        if (_closing || _closed || !_permissionCheckComplete || _permissionRetryTask is { IsCompleted: false }) return;
        _permissionRetryTask = RetryMicrophonePermissionAsync();
        await _permissionRetryTask;
    }

    private async Task RetryMicrophonePermissionAsync()
    {
        _dismissedMicrophonePermissionStatus = null;
        CheckMicrophonePermissionButton.IsEnabled = false;
        try { await _microphonePermission.InitializeAsync(_startupCancellation.Token); }
        catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested) { }
        catch (Exception error) { if (!_closing) _session.ReportUiError(error, NoticeArea.Application); }
        finally
        {
            RenderMicrophonePermission();
            if (!_closed && !_closing) CheckMicrophonePermissionButton.IsEnabled = true;
        }
    }

    private async void OpenMicrophoneSettingsClicked(object sender, RoutedEventArgs args)
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

    // Notices toggle Visibility rather than joining the panel, so an implicit show animation lets them rise into
    // place instead of popping. Skipped when Windows animations are off.
    private void AddNoticeShowAnimations()
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var resources = Application.Current.Resources;
        var duration = TimeSpan.FromMilliseconds((double)resources["MotionNormalMilliseconds"]);
        var offset = (float)(double)resources["MotionEntranceOffset"];
        var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(ShellRoot).Compositor;
        foreach (UIElement notice in new UIElement[] { MicrophonePermissionNotice, MeetingSuggestion, ShutdownError })
        {
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(notice, true);
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Target = "Opacity";
            fade.InsertKeyFrame(0, 0);
            fade.InsertKeyFrame(1, 1);
            fade.Duration = duration;
            var rise = compositor.CreateVector3KeyFrameAnimation();
            rise.Target = "Translation";
            rise.InsertKeyFrame(0, new System.Numerics.Vector3(0, offset, 0));
            rise.InsertKeyFrame(1, System.Numerics.Vector3.Zero);
            rise.Duration = duration;
            var show = compositor.CreateAnimationGroup();
            show.Add(fade);
            show.Add(rise);
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetImplicitShowAnimation(notice, show);
        }
    }

    private void ShellSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var compact = args.NewSize.Width < (double)Application.Current.Resources["CompactNavigationThreshold"];
        if (_compactNavigation == compact) return;
        _compactNavigation = compact;
        SidebarColumn.Width = (GridLength)Application.Current.Resources[compact ? "CompactSidebarColumnWidth" : "SidebarColumnWidth"];
        Sidebar.Padding = (Thickness)Application.Current.Resources[compact ? "CompactSidebarPadding" : "SidebarPadding"];
        AppWordmark.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
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
    }

    // A window hidden to the tray is not showing the lesson, so it must not hold the global shortcut.
    private WelcomePage? CurrentWelcome() =>
        !_closed && AppWindow.IsVisible && ReferenceEquals(MainContent.Content, _welcome) ? _welcome : null;

    private void OpenShortcutLesson()
    {
        if (!_session.State.IsIdle || _shortcut.IsBusy)
        {
            _session.Notify(NoticeKind.Warning, "Finish dictation first",
                "Wait for the current dictation and insertion to finish before opening the shortcut lesson.");
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
        var offer = _suggestions.Offer;
        MeetingSuggestion.IsOpen = offer is not null;
        MeetingSuggestion.Visibility = offer is null ? Visibility.Collapsed : Visibility.Visible;
        OpenSuggestedMeetingButton.IsEnabled = !_suggestions.IsConfirming && offer is not null;
    }

    private void MeetingSuggestionDismissed(InfoBar sender, object args) => _suggestions.Dismiss();

    private async void OpenSuggestedMeetingClicked(object sender, RoutedEventArgs args)
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
        TrayError.IsOpen = false;
        TrayError.Visibility = Visibility.Collapsed;
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
        TrayError.Message = $"{error.Message} Try closing the window again, or choose Exit Ansible to stop the app.";
        TrayError.Visibility = Visibility.Visible;
        TrayError.IsOpen = true;
    }

    private void ExitClicked(object sender, RoutedEventArgs args) => ExitRequested();

    private async void ExitRequested()
    {
        if (_closing) { return; }
        _closing = true;
        MainContent.IsEnabled = false;
        ShutdownError.IsOpen = false;
        ShutdownError.Visibility = Visibility.Collapsed;
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
            ShutdownError.Message = $"{error.Message} Waiting for owned work to stop.";
            ShutdownError.Visibility = Visibility.Visible;
            ShutdownError.IsOpen = true;
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
            ShutdownError.Message = $"{failure.Message} Try Exit from the tray menu again.";
            _closing = false;
            MainContent.IsEnabled = true;
            _meetings.ResumeAfterFailedShutdown();
            _permissionCheckComplete = true;
            RenderMicrophonePermission();
            _suggestions.ResumeAfterFailedShutdown();
        }
    }

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
