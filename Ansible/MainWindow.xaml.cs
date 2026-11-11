using System.Diagnostics;
using System.Numerics;
using Ansible.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Ansible;

public sealed partial class MainWindow : Window
{
    // Notice keys, one per source. A source's newest notice replaces its previous one.
    private const string SessionNoticeKey = "session";
    private const string MicrophonePermissionNoticeKey = "microphone-permission";
    private const string MicrophoneCheckNoticeKey = "microphone-check";
    private const string MicrophoneSettingsNoticeKey = "microphone-settings";
    private const string MeetingSuggestionNoticeKey = "meeting-suggestion";
    private const string MeetingDetectionNoticeKey = "meeting-detection";
    private const string MeetingRecoveryNoticeKey = "meeting-recovery";
    private const string MeetingWindowNoticeKey = "meeting-window";
    private const string StartupNoticeKey = "startup";
    private const string ShortcutNoticeKey = "shortcut";
    private const string ShortcutRegistrationNoticeKey = "shortcut-registration";
    private const string RecordRegistrationNoticeKey = "record-shortcut-registration";
    private const string RecordPasteRegistrationNoticeKey = "record-paste-shortcut-registration";
    private const string CaptureStorageNoticeKey = "capture-storage";
    private const string ShortcutLessonNoticeKey = "shortcut-lesson";
    internal const string CapturePrivacyNoticeKey = "capture-privacy";
    private const string TrayErrorNoticeKey = "tray-error";
    private const string ShutdownErrorNoticeKey = "shutdown-error";
    private readonly DictationSession _session;
    private readonly DictationPage _dictation;
    private readonly WelcomeDialog _welcome;
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
    private GlobalDictationHotkey? _recordHotkey;
    private GlobalDictationHotkey? _recordPasteHotkey;
    private readonly CaptureStore _captures;
    private readonly WindowsRecordTakeFactory _recordFactory;
    private RecordEscapeKey? _recordEscape;
    private Task? _recordCancelTask;
    private SystemTrayIcon? _tray;
    private DictationShortcut _configuredHotkey = DictationShortcut.Default;
    private DictationShortcut _configuredRecordHotkey = DictationShortcut.RecordDefault;
    private readonly ShortcutDictation _shortcut;
    private FloatingDictationWindow? _floating;
    private bool _initialized;
    private bool _compactNavigation;
    private bool _meetingRecoveryComplete;
    private bool _closed;
    private bool _allowClose;
    private bool _closing;
    private bool _popupFailed;
    private bool _creatingFloating;
    // Caret of the shortcut destination, read once at press time. Null for in-app dictation or an unknown caret.
    private CaretBounds? _overlayAnchor;
    private bool? _capturePrivacy;
    private PageCanvas? _noticeCanvas;
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
    private readonly ShellPages _pages;

    internal MainWindow(DictationSession session, AppPaths paths, HttpClient modelDownloads, MicrophonePermissionPreflight microphonePermission,
        LocalDiagnosticLog diagnostics, CaptureStore captures, WindowsRecordTakeFactory recordFactory)
    {
        InitializeComponent();
        Notices.Attach(_notices);
        Notices.CoveredHeightChanged += (_, _) => { if (_noticeCanvas is not null) _noticeCanvas.BottomInset = Notices.CoveredHeight; };
        _session = session;
        _microphonePermission = microphonePermission;
        _paths = paths;
        _captures = captures;
        _recordFactory = recordFactory;
        _recordFactory.BeforeCapture = PrepareRecordCapture;
        _meetingStore = new MeetingStore(Path.Combine(Path.GetDirectoryName(paths.Settings)!, "meetings"));
        _meetingDiagnostics = diagnostics;
        _meetingCleanup = new(diagnostics);
        _meetings = new MeetingsPage(_meetingStore, OpenMeetingAsync, () => WinRT.Interop.WindowNative.GetWindowHandle(this), diagnostics, _notices);
        _history = new HomeDictationHistory(session, Path.Combine(Path.GetDirectoryName(paths.Settings)!, "dictation-history.json"), diagnostics);
        _shortcut = new ShortcutDictation(session,
            new WelcomeShortcutTargetInspector(DispatcherQueue, CurrentWelcome,
                new WindowsShortcutTargetInspector(WinRT.Interop.WindowNative.GetWindowHandle(this))),
            diagnostics, (operationId, status) => _history.RecordDelivery(operationId, status), anchor =>
            {
                _overlayAnchor = anchor;
                if (FloatingPreviewPresentation.ShouldShowWaveform(_session.State)) { _floating?.ShowPreview(_overlayAnchor); }
            });
        _shortcut.RecordAttemptChanged += RecordAttemptChanged;
        _suggestions = new MeetingSuggestions(new Ansible.Core.Windows.TeamsMeetingDetector(),
            () => _session.State.Settings.AutoDetectMeetings, () => MeetingSuggestionsBlocked,
            candidate => _meetingWindows.Any(meeting => meeting.session.IsActive && meeting.window.SelectedMeetingIdentity == candidate),
            (title, message) => _notices.Set(MeetingDetectionNoticeKey,
                new NoticeContent(NoticeSeverity.Warning, title, message), NoticeArea.Meetings));
        _suggestions.Changed += RenderMeetingSuggestion;
        // Page callbacks run after _pages is assigned below.
        _welcome = new WelcomeDialog(session, () => _shortcut.IsBusy, _notices);
        _dictation = new DictationPage(session, paths, () => WinRT.Interop.WindowNative.GetWindowHandle(this), _history, _notices, _shortcut);
        _settings = new MainPage(session, paths, () => WinRT.Interop.WindowNative.GetWindowHandle(this),
            ChangeHotkey, ChangeDictationMode, CaptureShortcut, diagnostics, _notices,
            ChangeRecordHotkey, EnableRecordChord, captures, ChangeRecordPasteHotkey);
        _settings.ShortcutLessonRequested += OpenShortcutLesson;
        _models = new ModelManagementPage(session, paths, () => WinRT.Interop.WindowNative.GetWindowHandle(this), modelDownloads,
            () => { Navigate(_pages!.Home); return _dictation.TranscribeFileAsync(); }, diagnostics, _notices);
        _customWords = new CustomWordsPage(session, () => Navigate(_pages!.Models), _notices);
        _pages = new ShellPages(
            Home: ShellPage.InSidebar(_dictation, DictationNav, DictationIndicator, NoticeArea.Dictation),
            Meetings: ShellPage.InSidebar(_meetings, MeetingsNav, MeetingsIndicator, NoticeArea.Meetings),
            Models: ShellPage.InSidebar(_models, ModelsNav, ModelsIndicator, NoticeArea.Models),
            Dictionary: ShellPage.InSidebar(_customWords, CustomWordsNav, CustomWordsIndicator, NoticeArea.Dictionary),
            Settings: ShellPage.InSidebar(_settings, SettingsNav, SettingsIndicator, NoticeArea.Settings));
        AppTitleBar.Attach(this);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
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
        Navigate(_pages.Home);
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
            try
            {
                await WindowsRecordTakeFactory.RefreshClipboardProtectionAsync();
                var cleanup = await _captures.CleanupAsync(token);
                if (cleanup.FailedTakes > 0)
                    _notices.Set(CaptureStorageNoticeKey, new NoticeContent(NoticeSeverity.Warning,
                        "Some captures could not be deleted", "Close apps using saved captures and retry Delete all captures in Settings."), NoticeArea.Settings);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { _notices.SetError(CaptureStorageNoticeKey, error, NoticeArea.Settings); }
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
                _notices.Set(ShortcutRegistrationNoticeKey, new NoticeContent(NoticeSeverity.Error, "Shortcut unavailable", error.Message),
                    NoticeArea.Settings | NoticeArea.Dictation);
            }
            if (_session.State.Settings.RecordChordEnabled)
            {
                try
                {
                    _configuredRecordHotkey = _session.State.Settings.RecordShortcut;
                    _recordHotkey = new GlobalDictationHotkey(WinRT.Interop.WindowNative.GetWindowHandle(this),
                        _configuredRecordHotkey, RecordHotkeyChanged);
                }
                catch (Exception error) { _notices.SetError(RecordRegistrationNoticeKey, error, NoticeArea.Settings | NoticeArea.Dictation); }
                try
                {
                    _recordPasteHotkey = new GlobalDictationHotkey(WinRT.Interop.WindowNative.GetWindowHandle(this),
                        _session.State.Settings.RecordPasteShortcut, RecordPasteHotkeyChanged);
                }
                catch (Exception error) { _notices.SetError(RecordPasteRegistrationNoticeKey, error, NoticeArea.Settings | NoticeArea.Dictation); }
            }
            if (!_session.State.Settings.WelcomeCompleted) ShowWelcome();
            else if (_session.State.Phase == DictationPhase.Disconnected && _session.State.Models.Count == 0) Navigate(_pages.Models);
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
            _notices.SetError(StartupNoticeKey, error, NoticeArea.Application);
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
        NoticeContent? notice = null;
        try
        {
            var failures = await Task.Run(() => _meetingStore.MarkInterruptedSessionsAsync(token), token);
            token.ThrowIfCancellationRequested();
            _meetingRecoveryComplete = true;
            if (failures.Count > 0)
                notice = new(NoticeSeverity.Warning, "Some saved transcripts unavailable",
                    "Open Meetings to export and remove unreadable files. New meetings are still available.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception error) { notice = new(NoticeSeverity.Error, "Meeting recovery failed", error.Message); }
        // Initialization publishes its own notice. Report recovery problems afterward without delaying dictation.
        if (notice is not null && _startupTask is not null) await _startupTask;
        if (token.IsCancellationRequested || _closed || _closing) return;
        if (notice is not null) _notices.Set(MeetingRecoveryNoticeKey, notice, NoticeArea.Meetings);
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
        // Short on purpose. The notice shows two buttons beside the text, and a longer body pushes them onto their own row.
        var message = status switch
        {
            MicrophonePermissionStatus.DeniedByUser => "Turn it on for Ansible and desktop apps.",
            MicrophonePermissionStatus.DeniedBySystem => "Turn it on in Windows, or ask your administrator.",
            MicrophonePermissionStatus.NotDeclared => "Install an Ansible package that declares the microphone capability.",
            MicrophonePermissionStatus.PromptRequired => "Select Check again to request access.",
            _ => "Ansible could not check it. Review Windows privacy settings."
        };
        _notices.Set(MicrophonePermissionNoticeKey, new NoticeContent(NoticeSeverity.Warning, "Microphone access required", message,
            [new("Open Windows Settings", OpenMicrophoneSettings), new("Check again", CheckMicrophonePermission)]),
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
        _notices.Set(MicrophoneCheckNoticeKey, null);
        try { await _microphonePermission.InitializeAsync(_startupCancellation.Token); }
        catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested) { }
        catch (Exception error) { if (!_closing) _notices.SetError(MicrophoneCheckNoticeKey, error, NoticeArea.Application); }
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
        _notices.Set(MicrophoneSettingsNoticeKey, null);
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone")) && !_closing)
                _notices.Set(MicrophoneSettingsNoticeKey, new NoticeContent(NoticeSeverity.Warning, "Windows Settings unavailable",
                    "Open Windows Settings > Privacy & security > Microphone."), NoticeArea.Application);
        }
        catch (Exception error) { if (!_closing) _notices.SetError(MicrophoneSettingsNoticeKey, error, NoticeArea.Application); }
    }

    private void NavigateClicked(object sender, RoutedEventArgs args) =>
        Navigate(_pages.Sidebar.First(page => ReferenceEquals(page.Nav, sender)));

    private void ShellSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var compact = args.NewSize.Width < (double)Application.Current.Resources["CompactNavigationThreshold"];
        if (_compactNavigation == compact) return;
        _compactNavigation = compact;
        var resources = Application.Current.Resources;
        SidebarColumn.Width = (GridLength)resources[compact ? "CompactSidebarColumnWidth" : "SidebarColumnWidth"];
        Sidebar.Padding = (Thickness)resources[compact ? "CompactSidebarPadding" : "SidebarPadding"];
        AppTitleBar.SetCompact(compact);
        foreach (var button in _pages.Sidebar.Select(page => page.Nav))
        {
            // A local value outlasts Navigate's style swaps, so the icon stays centred in the compact rail.
            button.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            if (button.Content is StackPanel content)
            {
                content.Spacing = compact ? 0 : (double)resources["NavLabelGap"];
                foreach (var label in content.Children.OfType<TextBlock>())
                    label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            }
            // The rail has no labels, so each entry names itself on hover and keyboard focus, beside the rail.
            ToolTipService.SetToolTip(button, compact ? AutomationProperties.GetName(button) : null);
            ToolTipService.SetPlacement(button, Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Right);
        }
    }

    private void NoticeColumnChanged(object? sender, EventArgs args) => AlignNotices();

    // The notice band shares the current page's column, so a notice starts and ends where the page content does.
    private void AlignNotices()
    {
        if (_noticeCanvas is null) return;
        var (left, right, maxWidth) = _noticeCanvas.Column;
        Notices.AlignWith(left, right, maxWidth);
    }

    private void Navigate(ShellPage page)
    {
        if (page == _pages.Settings && _permissionCheckComplete && _microphonePermission.Status != MicrophonePermissionStatus.Allowed)
        {
            _dismissedMicrophonePermissionStatus = null;
            RenderMicrophonePermission();
        }
        // Reselecting the current page keeps its state and scroll position.
        if (ReferenceEquals(MainContent.Content, page.Page)) return;
        MainContent.Content = page.Page;
        foreach (var item in _pages.Sidebar) item.SetSelected(ReferenceEquals(item.Nav, page.Nav));
        // Notices sit in a band under the current page's scroll viewport, so only that page ends its viewport above them.
        if (_noticeCanvas is not null)
        {
            _noticeCanvas.BottomInset = 0;
            _noticeCanvas.ColumnChanged -= NoticeColumnChanged;
        }
        _noticeCanvas = page.Canvas;
        _noticeCanvas.ColumnChanged += NoticeColumnChanged;
        AlignNotices();
        Notices.ShowPage(page.Area);
        _noticeCanvas.BottomInset = Notices.CoveredHeight;
        AppTitleBar.ShowPage(page.Title, page.Canvas);
    }

    // The content layer's border draws rounded corners but does not clip; clip the layer so the page scrollbar and
    // content stay inside the corners.
    private void MainContentSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var visual = ElementCompositionPreview.GetElementVisual(MainContent);
        var radius = new Vector2((float)Math.Max(0, ContentLayer.CornerRadius.TopLeft - ContentLayer.BorderThickness.Left));
        visual.Clip = visual.Compositor.CreateRectangleClip(0, 0, (float)args.NewSize.Width, (float)args.NewSize.Height,
            radius, radius, radius, radius);
    }

    // A window hidden to the tray or minimized is not showing the lesson, so it must not hold the global shortcut.
    // AppWindow.IsVisible stays true for a minimized window, so the presenter state is checked separately.
    private WelcomeDialog? CurrentWelcome() =>
        !_closed && AppWindow.IsVisible
        && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized }
        && _welcome.IsShowing ? _welcome : null;

    private void OpenShortcutLesson()
    {
        if (!_session.State.IsIdle || _shortcut.IsBusy)
        {
            _notices.Set(ShortcutLessonNoticeKey, new NoticeContent(NoticeSeverity.Warning, "Finish dictation first",
                "Wait for the current dictation and insertion to finish before opening the shortcut lesson."), NoticeArea.Settings);
            return;
        }
        _notices.Set(ShortcutLessonNoticeKey, null);
        ShowWelcome();
    }

    // The lesson opens over whichever page is current. Only one ContentDialog may be open per XamlRoot.
    private async void ShowWelcome()
    {
        if (_closing || _closed || _welcome.IsShowing) return;
        WelcomeExit exit;
        try { exit = await _welcome.ShowLessonAsync(MainContent.XamlRoot); }
        catch (Exception error)
        {
            _notices.SetError(WelcomeDialog.NoticeKey, error, NoticeArea.Application);
            return;
        }
        if (_closing || _closed) return;
        if (exit == WelcomeExit.ModelsRequested) Navigate(_pages.Models);
        else if (exit == WelcomeExit.Finished) CompleteWelcome();
    }

    private void CompleteWelcome()
    {
        try
        {
            if (!_session.State.IsIdle || _shortcut.IsBusy) return;
            _session.CompleteWelcome();
        }
        catch (Exception error) { _notices.SetError(WelcomeDialog.NoticeKey, error, NoticeArea.Application); }
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
        catch (Exception error) { ShowCapturePrivacyFailure(error.Message); }
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
            catch (Exception error) { _notices.SetError(MeetingWindowNoticeKey, error, NoticeArea.Meetings); }
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
            Navigate(_pages.Meetings);
    }

    private void ChangeHotkey(DictationShortcut choice)
    {
        if (!_session.State.IsIdle || _shortcut.IsBusy)
            throw new InvalidOperationException("Finish dictation before changing its shortcut.");
        var settings = _session.State.Settings;
        (settings with { Shortcut = choice }).Validate();
        if (_configuredHotkey == choice && _hotkey is not null) return;
        var replacement = new GlobalDictationHotkey(WinRT.Interop.WindowNative.GetWindowHandle(this),
            choice, HotkeyChanged);
        try { _session.SetShortcut(choice); }
        catch { replacement.Dispose(); throw; }
        _hotkey?.Dispose();
        _hotkey = replacement;
        _configuredHotkey = choice;
        _notices.Set(ShortcutRegistrationNoticeKey, null);
    }

    private void ChangeRecordHotkey(DictationShortcut choice)
    {
        if (!_session.State.IsIdle || _shortcut.IsBusy)
            throw new InvalidOperationException("Finish dictation before changing its shortcut.");
        var settings = _session.State.Settings;
        (settings with { RecordShortcut = choice }).Validate();
        if (!settings.RecordChordEnabled) { _session.SetRecordShortcut(choice); _configuredRecordHotkey = choice; return; }
        if (_configuredRecordHotkey == choice && _recordHotkey is not null) return;
        var replacement = new GlobalDictationHotkey(WinRT.Interop.WindowNative.GetWindowHandle(this), choice, RecordHotkeyChanged);
        try { _session.SetRecordShortcut(choice); }
        catch { replacement.Dispose(); throw; }
        _recordHotkey?.Dispose();
        _recordHotkey = replacement;
        _configuredRecordHotkey = choice;
        _notices.Set(RecordRegistrationNoticeKey, null);
    }

    private void ChangeRecordPasteHotkey(DictationShortcut choice)
    {
        if (!_session.State.IsIdle || _shortcut.IsBusy)
            throw new InvalidOperationException("Finish dictation and paste before changing its shortcut.");
        var settings = _session.State.Settings;
        (settings with { RecordPasteShortcut = choice }).Validate();
        if (!settings.RecordChordEnabled) { _session.SetRecordPasteShortcut(choice); return; }
        if (settings.RecordPasteShortcut == choice && _recordPasteHotkey is not null) return;
        var replacement = new GlobalDictationHotkey(WinRT.Interop.WindowNative.GetWindowHandle(this), choice, RecordPasteHotkeyChanged);
        try { _session.SetRecordPasteShortcut(choice); }
        catch { replacement.Dispose(); throw; }
        _recordPasteHotkey?.Dispose();
        _recordPasteHotkey = replacement;
        _notices.Set(RecordPasteRegistrationNoticeKey, null);
    }

    private void EnableRecordChord(bool enabled)
    {
        if (!_session.State.IsIdle || _shortcut.IsBusy)
            throw new InvalidOperationException("Finish dictation before changing its shortcut.");
        var settings = _session.State.Settings;
        (settings with { RecordChordEnabled = enabled }).Validate();
        if (enabled)
        {
            GlobalDictationHotkey? record = null;
            GlobalDictationHotkey? paste = null;
            try
            {
                record = _recordHotkey ?? new GlobalDictationHotkey(WinRT.Interop.WindowNative.GetWindowHandle(this),
                    settings.RecordShortcut, RecordHotkeyChanged);
                paste = _recordPasteHotkey ?? new GlobalDictationHotkey(WinRT.Interop.WindowNative.GetWindowHandle(this),
                    settings.RecordPasteShortcut, RecordPasteHotkeyChanged);
                _session.SetRecordChordEnabled(true);
            }
            catch
            {
                if (_recordHotkey is null) record?.Dispose();
                if (_recordPasteHotkey is null) paste?.Dispose();
                throw;
            }
            _recordHotkey = record;
            _recordPasteHotkey = paste;
            _configuredRecordHotkey = settings.RecordShortcut;
        }
        else
        {
            _session.SetRecordChordEnabled(false);
            _recordHotkey?.Dispose();
            _recordHotkey = null;
            _recordPasteHotkey?.Dispose();
            _recordPasteHotkey = null;
        }
        _notices.Set(RecordRegistrationNoticeKey, null);
        _notices.Set(RecordPasteRegistrationNoticeKey, null);
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
            var suspended = new List<GlobalDictationHotkey>();
            try
            {
                foreach (var hotkey in new[] { _hotkey, _recordHotkey, _recordPasteHotkey })
                {
                    if (hotkey is null) continue;
                    hotkey.Suspend();
                    suspended.Add(hotkey);
                }
            }
            catch (Exception error)
            {
                var failures = new List<Exception> { error };
                foreach (var hotkey in suspended)
                    try { hotkey.Resume(); } catch (Exception restoreError) { failures.Add(restoreError); }
                throw new AggregateException("A shortcut could not be released for capture.", failures);
            }
        }
        else
        {
            var failures = new List<Exception>();
            try { _hotkey?.Resume(); } catch (Exception error) { failures.Add(error); }
            try { _recordHotkey?.Resume(); } catch (Exception error) { failures.Add(error); }
            try { _recordPasteHotkey?.Resume(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count > 0) throw new AggregateException("A shortcut could not be restored.", failures);
        }
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
            if (failures.Count > 0) ShowCapturePrivacyFailure(string.Join(" ", failures.Distinct()));
            else _notices.Set(CapturePrivacyNoticeKey, null);
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
                _floating = new FloatingDictationWindow(_session, this, _notices);
                _floating.Closed += (_, _) => _floating = null;
            }
            catch (Exception error)
            {
                _popupFailed = true;
                _notices.SetError(FloatingDictationWindow.NoticeKey,
                    new InvalidOperationException("The floating preview could not be opened.", error));
            }
            finally { _creatingFloating = false; }
        }
        if (showWaveform) { _floating?.ShowPreview(_overlayAnchor); }
        else { _floating?.HidePreview(); }
    }

    private void ShowCapturePrivacyFailure(string message) => _notices.Set(CapturePrivacyNoticeKey,
        new NoticeContent(NoticeSeverity.Warning, "Screen capture privacy unavailable", message), NoticeArea.Application);

    private async void HotkeyChanged(bool pressed)
    {
        try
        {
            // The caps follow the chord on every step, before the lesson decides whether it may dictate.
            if (_welcome.IsShowing) _welcome.ShowShortcutHeld(pressed);
            if (pressed && CurrentWelcome() is { AcceptsShortcut: false }) return;
            if (pressed) { await _shortcut.PressedAsync(_configuredHotkey); }
            else { await _shortcut.ReleasedAsync(); }
        }
        catch (Exception error) { _notices.SetError(ShortcutNoticeKey, error); }
        finally { CurrentWelcome()?.RefreshAfterShortcut(); }
    }

    private async void RecordHotkeyChanged(bool pressed)
    {
        try
        {
            if (pressed && _welcome.IsShowing) return;
            if (pressed) await _shortcut.PressedAsync(_configuredRecordHotkey, ShortcutActivation.Record);
            else await _shortcut.ReleasedAsync(ShortcutActivation.Record);
        }
        catch (Exception error) { _notices.SetError(ShortcutNoticeKey, error); }
    }

    private async void RecordPasteHotkeyChanged(bool pressed)
    {
        try
        {
            if (pressed && (_closing || _closed || _welcome.IsShowing)) return;
            if (pressed) await _shortcut.PasteRecordPressedAsync();
            else _shortcut.PasteRecordReleased();
        }
        catch (Exception error) { _notices.SetError(ShortcutNoticeKey, error); }
    }

    private void PrepareRecordCapture()
    {
        // Snapshot runs synchronously on accepted activation, before the native capture worker can see a frame.
        try { _floating?.ForceRecordPrivacy(true); }
        catch (Exception error) { ShowCapturePrivacyFailure(error.Message); throw; }
    }

    private void RecordAttemptChanged()
    {
        try
        {
            if (_shortcut.HasRecordAttempt)
            {
                _recordEscape ??= new RecordEscapeKey(RecordEscapePressed);
            }
            else
            {
                _recordEscape?.Dispose();
                _recordEscape = null;
                _floating?.ForceRecordPrivacy(false);
            }
        }
        catch (Exception error) { _notices.SetError(ShortcutNoticeKey, error); }
    }

    private void RecordEscapePressed()
    {
        if (!_shortcut.HasRecordAttempt || _recordCancelTask is { IsCompleted: false }) return;
        _recordCancelTask = CancelRecordAttemptAsync();
    }

    private async Task CancelRecordAttemptAsync()
    {
        var origin = _recordFactory.TakeOriginTimestamp;
        try
        {
            await _shortcut.CancelRecordAsync();
            if (_session.State.Result?.Payload?.IsRecordTake != true)
                await _recordFactory.DiscardResultAsync(origin);
        }
        catch (Exception error) { _notices.SetError(ShortcutNoticeKey, error); }
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
        // The dialog sits in the popup layer, outside MainContent, so it is closed rather than disabled.
        _welcome.Dismiss();
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
            await Task.WhenAll(_session.CloseAsync(), _models.DisposeAsync().AsTask(), _dictation.FinishBackgroundWorkAsync(), _settings.JoinAsync());
            await _shortcut.JoinAsync();
            if (_recordCancelTask is { } recordCancel) await recordCancel;
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
                _suggestions.JoinAsync(), _shortcut.JoinAsync(), _dictation.FinishBackgroundWorkAsync()
            }.OfType<Task>());
            try { await joined; }
            catch (Exception)
            {
                if (joined.Exception is { } joinError) failures.AddRange(joinError.Flatten().InnerExceptions);
            }
            _shortcut.ResumeAfterFailedShutdown();
            _settings.ResumeAfterFailedShutdown();
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
        AppTitleBar.Detach();
        _tray?.Dispose();
        _dictation.Dispose();
        _history.Dispose();
        _hotkey?.Dispose();
        _recordHotkey?.Dispose();
        _recordPasteHotkey?.Dispose();
        _recordFactory.BeforeCapture = null;
        _shortcut.RecordAttemptChanged -= RecordAttemptChanged;
        _recordEscape?.Dispose();
        _shortcut.Dispose();
        _startupCancellation.Dispose();
        _suggestions.Dispose();
        _observer.Dispose();
        AppWindow.Closing -= CloseRequested;
    }
}
