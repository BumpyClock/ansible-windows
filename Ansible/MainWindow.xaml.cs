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
    private readonly HomeDictationHistory _history;
    private readonly MainPage _settings;
    private readonly ModelManagementPage _models;
    private readonly CustomWordsPage _customWords;
    private readonly MeetingsPage _meetings;
    private readonly MeetingStore _meetingStore;
    private readonly AppPaths _paths;
    private readonly List<(MeetingSession session, MeetingWindow window, Guid? savedId)> _meetingWindows = [];
    private readonly UiSessionObserver _observer;
    private GlobalDictationHotkey? _hotkey;
    private DictationShortcut _configuredHotkey = DictationShortcut.Default;
    private volatile bool _shortcutHeld;
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
    private readonly ShortcutCaptureCoordinator _captureCoordinator = new();
    private CancellationTokenSource _deliveryCancellation = new();
    private Task<(bool available, CapturedTextTarget? target, string reason)>? _captureTask;
    private Task? _deliveryTask;
    private Task? _releaseTailTask;
    private Task? _pressTask;
    private FinalTextDelivery? _delivery;
    private Task<SessionOutcome>? _deliveryOperation;
    private Guid? _deliveryOperationId;
    private readonly IMeetingDetector _meetingDetector = new Ansible.Core.Windows.TeamsMeetingDetector();
    private readonly MeetingDetectionTracker _meetingDetection = new();
    private readonly Stopwatch _detectionClock = Stopwatch.StartNew();
    private CancellationTokenSource? _detectionCancellation;
    private Task? _detectionTask;
    private MeetingDetectionCandidate? _detectedMeeting;
    private MeetingDetectionCandidate? _offeredMeeting;
    private bool _detectionUnavailableReported;
    private bool _openingSuggestedMeeting;
    private Task? _detectionActionTask;
    private bool _hasDetectionObservation;
    private bool? _detectionEnabled;
    private readonly MicrophonePermissionPreflight _microphonePermission;
    private CancellationTokenSource _startupCancellation = new();
    private Task? _startupTask;
    private Task? _meetingRecoveryTask;
    private Task? _permissionRetryTask;
    private Task? _microphoneSettingsTask;
    private bool _launchReady;
    private bool _permissionCheckComplete;
    private MicrophonePermissionStatus? _dismissedMicrophonePermissionStatus;

    internal MainWindow(DictationSession session, AppPaths paths, HttpClient modelDownloads, MicrophonePermissionPreflight microphonePermission)
    {
        InitializeComponent();
        _session = session;
        _microphonePermission = microphonePermission;
        _paths = paths;
        _meetingStore = new MeetingStore(Path.Combine(Path.GetDirectoryName(paths.Settings)!, "meetings"));
        _meetings = new MeetingsPage(session, _meetingStore, OpenMeetingAsync, () => WinRT.Interop.WindowNative.GetWindowHandle(this));
        _history = new HomeDictationHistory(session);
        _dictation = new DictationPage(session, paths, () => WinRT.Interop.WindowNative.GetWindowHandle(this), _history);
        _settings = new MainPage(session, paths, () => WinRT.Interop.WindowNative.GetWindowHandle(this),
            ChangeHotkey, ChangeDictationMode, CaptureShortcut);
        _models = new ModelManagementPage(session, paths, () => WinRT.Interop.WindowNative.GetWindowHandle(this), modelDownloads,
            () => { Navigate("dictation"); return _dictation.TranscribeFileAsync(); });
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
        try
        {
            // Let the initial Window.Activate finish before Windows can display consent.
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            try { await _microphonePermission.InitializeAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // The preflight retains Unavailable and blocks capture. File/model workflows still initialize.
                Debug.WriteLine($"Ansible: microphone permission preflight failed, HRESULT 0x{error.HResult:X8}.");
            }
            token.ThrowIfCancellationRequested();
            _permissionCheckComplete = true;
            RenderMicrophonePermission();
            await _session.InitializeAsync();
            token.ThrowIfCancellationRequested();
            if (_closed || _closing) { return; }
            _launchReady = true;
            if (_session.State.Phase == DictationPhase.Disconnected && _session.State.Models.Count == 0)
                Navigate("models");
            try
            {
                _configuredHotkey = _session.State.Settings.Shortcut;
                _hotkey = new GlobalDictationHotkey(WinRT.Interop.WindowNative.GetWindowHandle(this),
                    _configuredHotkey, HotkeyChanged);
            }
            catch (Exception error) { _session.Notify(NoticeKind.Error, "Shortcut unavailable", error.Message, NoticeArea.Settings | NoticeArea.Dictation); }
            StartMeetingDetection();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { _session.ReportUiError(error, NoticeArea.Application); }
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
        Page target = page switch { "settings" => _settings, "dictation" => _dictation, "models" => _models,
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
            var selected = (string)button.Tag == page;
            button.Style = (Style)Application.Current.Resources[
                selected ? "SelectedNavigationButtonStyle" : "NavigationButtonStyle"];
            indicator.Style = (Style)Application.Current.Resources[
                selected ? "SelectedNavigationIconStyle" : "NavigationIconStyle"];
            AutomationProperties.SetHelpText(button, selected ? "Current page" : "");
            AutomationProperties.SetItemStatus(button, selected ? "Current page" : "");
        }
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
            new PermissionCheckedMeetingCaptureFactory(new Ansible.Core.Windows.MeetingCaptureFactory(), _microphonePermission), _meetingStore,
            () =>
            {
                var current = _session.State;
                return new RecognitionOptions(current.Language, current.Settings.CustomDictionary, current.Settings.Backend);
            },
            reserve: async () =>
            {
                if (!_launchReady || !_meetingRecoveryComplete)
                    throw new InvalidOperationException("Meeting storage must initialize successfully before starting a meeting.");
                if (_closing || _captureCoordinator.HasPendingCapture || _deliveryTask is { IsCompleted: false })
                    throw new InvalidOperationException("Finish dictation before starting a meeting.");
                await _session.ReserveMeetingAsync();
            },
            release: _session.ReleaseMeetingAsync,
            observerFactory: () => new Ansible.Core.Windows.TeamsSpeakerObserver(),
            alignerFactory: () => new NativeMeetingWordAligner(_paths.NativeLibrary, _paths.ModelCatalog, _session.State.ModelsDirectory),
            getModelId: () =>
            {
                var current = _session.State;
                return current.SelectedModel is { } selected
                    && current.Settings.ModelId == selected.Id
                    && MeetingSession.SupportsMeeting(selected)
                    ? selected.Id : null;
            });
        var window = new MeetingWindow(meeting, _meetingStore, document, ShellRoot.RequestedTheme);
        try { window.ApplyCapturePrivacy(_session.State.Settings.HideFromCapture); }
        catch (Exception error) { _session.Notify(NoticeKind.Warning, "Screen capture privacy unavailable", error.Message, NoticeArea.Application); }
        _meetingWindows.Add((meeting, window, document?.Id));
        meeting.StateChanged += state => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed || _closing) return;
            if (meeting.IsActive && _detectedMeeting is { } detected && window.SelectedMeetingIdentity == detected)
                _meetingDetection.Dismiss(detected);
            RefreshMeetingSuggestion();
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

    private bool MeetingDetectionBusy => _closed || _closing || !_launchReady || !_meetingRecoveryComplete || !_session.State.IsIdle ||
        _captureCoordinator.HasPendingCapture || _deliveryTask is { IsCompleted: false } ||
        _meetingWindows.Any(meeting => meeting.session.IsActive);

    private void StartMeetingDetection()
    {
        if (!_launchReady || _closing || _closed || _detectionTask is { IsCompleted: false }) return;
        _detectionCancellation?.Dispose();
        _detectionCancellation = new CancellationTokenSource();
        var token = _detectionCancellation.Token;
        _detectionTask = Task.Run(() => MonitorMeetingsAsync(token));
    }

    private async Task MonitorMeetingsAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_session.State.Settings.AutoDetectMeetings)
                {
                    try
                    {
                        var candidate = await _meetingDetector.ReadAsync(token);
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (_closed || _closing || token.IsCancellationRequested) return;
                            _detectionUnavailableReported = false;
                            _hasDetectionObservation = true;
                            _detectedMeeting = candidate;
                            if (candidate is not null && _meetingWindows.Any(meeting => meeting.session.IsActive && meeting.window.SelectedMeetingIdentity == candidate))
                                _meetingDetection.Dismiss(candidate);
                            _meetingDetection.Update(candidate, _detectionClock.Elapsed,
                                _session.State.Settings.AutoDetectMeetings, MeetingDetectionBusy);
                            RefreshMeetingSuggestion();
                        });
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (_closed || _closing || token.IsCancellationRequested) return;
                            _hasDetectionObservation = false;
                            _meetingDetection.InvalidateObservation();
                            HideMeetingSuggestion(); // A failed scan does not end or reset the detected episode.
                            if (_detectionUnavailableReported) return;
                            _detectionUnavailableReported = true;
                            _session.Notify(NoticeKind.Warning, "Meeting detection unavailable",
                                $"{error.Message} You can start notetaking from Meetings. Detection will retry.", NoticeArea.Meetings);
                        });
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void RefreshMeetingSuggestion()
    {
        if (_closed || _closing) { HideMeetingSuggestion(); return; }
        var enabled = _session.State.Settings.AutoDetectMeetings;
        if (_detectionEnabled != enabled)
        {
            _detectionEnabled = enabled;
            _hasDetectionObservation = false;
            _meetingDetection.InvalidateObservation();
        }
        if (!enabled || MeetingDetectionBusy || !_hasDetectionObservation)
        {
            _meetingDetection.InvalidateObservation();
            HideMeetingSuggestion();
            return;
        }
        var offer = _meetingDetection.CurrentOffer;
        _offeredMeeting = offer;
        MeetingSuggestion.IsOpen = offer is not null;
        MeetingSuggestion.Visibility = offer is null ? Visibility.Collapsed : Visibility.Visible;
        OpenSuggestedMeetingButton.IsEnabled = !_openingSuggestedMeeting && offer is not null;
    }

    private void HideMeetingSuggestion()
    {
        _offeredMeeting = null;
        MeetingSuggestion.IsOpen = false;
        MeetingSuggestion.Visibility = Visibility.Collapsed;
    }

    private void MeetingSuggestionDismissed(InfoBar sender, object args)
    {
        if (_offeredMeeting is { } candidate) _meetingDetection.Dismiss(candidate);
        HideMeetingSuggestion();
    }

    private async void OpenSuggestedMeetingClicked(object sender, RoutedEventArgs args)
    {
        if (_detectionActionTask is { IsCompleted: false }) return;
        _detectionActionTask = OpenSuggestedMeetingAsync();
        await _detectionActionTask;
    }

    private async Task OpenSuggestedMeetingAsync()
    {
        if (_openingSuggestedMeeting || MeetingDetectionBusy || !_session.State.Settings.AutoDetectMeetings ||
            _offeredMeeting is not { } expected || _detectionCancellation is null) return;
        _openingSuggestedMeeting = true;
        OpenSuggestedMeetingButton.IsEnabled = false;
        try
        {
            var current = await Task.Run(() => _meetingDetector.ReadAsync(_detectionCancellation.Token));
            if (current != expected || MeetingDetectionBusy || !_session.State.Settings.AutoDetectMeetings || _offeredMeeting != expected)
            { HideMeetingSuggestion(); return; }
            var window = OpenMeeting(null);
            if (!window.SelectDetectedMeeting(expected))
            {
                HideMeetingSuggestion();
                _session.Notify(NoticeKind.Warning, "Meeting changed",
                    "The detected Teams app is no longer available. Select your meeting app from Meetings.", NoticeArea.Meetings);
                Navigate("meetings");
                return;
            }
            _meetingDetection.Dismiss(expected);
            HideMeetingSuggestion();
            window.Activate(); // Only the explicit action activates the notetaker; it never starts capture.
        }
        catch (OperationCanceledException) when (_closing || _closed) { }
        catch (Exception error)
        {
            HideMeetingSuggestion();
            _session.Notify(NoticeKind.Warning, "Could not open notetaker", error.Message, NoticeArea.Meetings);
            Navigate("meetings");
        }
        finally { _openingSuggestedMeeting = false; }
    }

    private void ChangeHotkey(DictationShortcut choice)
    {
        if (!_session.State.IsIdle || _captureCoordinator.HasPendingCapture || _deliveryTask is { IsCompleted: false })
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
        if (!_session.State.IsIdle || _captureCoordinator.HasPendingCapture || _deliveryTask is { IsCompleted: false })
            throw new InvalidOperationException("Finish dictation before changing its activation mode.");
        _session.SetPushToTalk(pushToTalk);
    }

    private void CaptureShortcut(bool capturing)
    {
        if (capturing)
        {
            if (!_session.State.IsIdle || _captureCoordinator.HasPendingCapture || _deliveryTask is { IsCompleted: false })
                throw new InvalidOperationException("Finish dictation before recording a shortcut.");
            _hotkey?.Suspend();
        }
        else { _hotkey?.Resume(); }
    }

    private void SessionChanged(SessionSnapshot state)
    {
        if (_closed) { return; }
        RenderMicrophonePermission();
        RefreshMeetingSuggestion();
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
        var showWaveform = state.IsCaptureOpen || state.Phase is DictationPhase.Preparing or DictationPhase.Recording;
        if (!showWaveform) { _popupFailed = false; _overlayAnchor = null; }
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
        _shortcutHeld = pressed;
        if (_closed || _closing) { return; }
        if (pressed) { _captureCoordinator.NotePressed(); }
        try
        {
            if (!pressed) { await HandleReleaseAsync(); }
            else
            {
                var task = HandlePressAsync();
                _pressTask = _pressTask is { IsCompleted: false } previous ? Task.WhenAll(previous, task) : task;
                await task;
            }
        }
        catch (Exception error) { _session.ReportUiError(error); }
    }

    // Shortcut release. Marks a pending push-to-talk capture stale so its inspection cannot start a late
    // recording. Once admitted, capture retains a short tail owned by this release and delivery operation.
    private async Task HandleReleaseAsync()
    {
        var state = _session.State;
        var release = _captureCoordinator.NoteReleased();
        if (state.Settings.PushToTalk && _delivery is not null)
        {
            var task = FinishReleasedDictationAsync(release, _delivery, state.OperationId);
            _releaseTailTask = _releaseTailTask is { IsCompleted: false } previous
                ? Task.WhenAll(previous, task) : task;
            await task;
        }
    }

    private async Task FinishReleasedDictationAsync(
        ShortcutCaptureCoordinator.Release release, FinalTextDelivery delivery, Guid? operationId)
    {
        var token = _deliveryCancellation.Token;
        try
        {
            if (!await _captureCoordinator.WaitForReleaseTailAsync(release, token)) { return; }
            if (_closing || _closed || _shortcutHeld || !ReferenceEquals(_delivery, delivery)) { return; }
            var state = _session.State;
            if (state.OperationId != operationId || !state.Settings.PushToTalk) { return; }
            if (state.CanFinish) { await _session.FinishAsync(); }
            else if (state.Phase == DictationPhase.Preparing) { await _session.CancelAsync(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { _session.ReportUiError(error); }
    }

    // Shortcut press. Admits at most one interaction to own the target inspection; a press arriving while an
    // inspection is pending or a delivery is active is ignored. After the inspection completes it rechecks
    // ownership and idle admission before starting delivery, so a stale or superseded press cannot start or
    // overwrite another interaction's recording.
    private async Task HandlePressAsync()
    {
        var token = _deliveryCancellation.Token;
        if (_captureCoordinator.HasPendingCapture) { return; }
        var state = _session.State;
        if (state.CanFinish)
        {
            if (!state.Settings.PushToTalk) { await _session.FinishAsync(); }
            return;
        }
        if (_deliveryTask is { IsCompleted: false }) { return; }
        if (!state.CanStart)
        {
            if (state.IsIdle)
                _session.Notify(NoticeKind.Warning, "Dictation unavailable", "Choose a speech model that supports microphone dictation before using the shortcut.");
            return;
        }
        if (state.SelectedModel?.CanInsertDictation != true)
        {
            _session.Notify(NoticeKind.Warning, "Speech text required",
                "This model does not support speech text extraction for shortcut insertion. Choose another speech model. Manual transcription remains available.");
            return;
        }
        var capture = _captureCoordinator.TryBeginCapture(state.Settings.PushToTalk, _deliveryTask is { IsCompleted: false });
        if (capture is null) { return; }
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var locateCaret = state.Settings.OverlayFollowsCaret;
        var inspection = Task.Run(() =>
        {
            var available = WindowsTextTarget.TryCapture(windowHandle, locateCaret, out var target, out var reason);
            return (available, target, reason);
        });
        _captureTask = inspection;
        try
        {
            // Let the hotkey adapter report an already-released chord before admitting recording.
            await Task.Yield();
            var inspected = await inspection;
            if (token.IsCancellationRequested || _closing || _closed) { return; }
            if (!_captureCoordinator.IsCurrent(capture)) { return; }
            if (!inspected.available)
            {
                _session.Notify(NoticeKind.Warning, "No insertion target", inspected.reason);
                return;
            }
            var current = _session.State;
            if (!current.CanStart || current.SelectedModel?.CanInsertDictation != true) { return; }
            if (!_captureCoordinator.TryStartDelivery(capture)) { return; }
            StartDelivery(inspected.target!);
        }
        finally
        {
            _captureCoordinator.Abandon(capture);
            if (ReferenceEquals(_captureTask, inspection)) { _captureTask = null; }
        }
    }

    private void StartDelivery(CapturedTextTarget target)
    {
        var modifiers = _configuredHotkey.Modifiers;
        var settings = _session.State.Settings;
        var method = settings.InsertionMethod;
        var typingGap = settings.TypingGapMilliseconds;
        var pasteShortcut = settings.PasteShortcut;
        var delivery = new FinalTextDelivery(
            insert: (pending, token) => Task.Run(() =>
                WindowsTextTarget.Insert(target, pending, method, typingGap, pasteShortcut, token, IsDeliveryCurrent), token),
            isCurrent: IsDeliveryCurrent,
            typingDeferred: () => _shortcutHeld && modifiers != 0);
        _delivery = delivery;
        _overlayAnchor = target.Caret;
        try
        {
            var operation = _session.StartDictationAsync();
            _deliveryOperation = operation;
            _deliveryOperationId = _session.State.OperationId;
            _deliveryTask = DeliverAsync(operation, delivery);
        }
        catch
        {
            _delivery = null;
            _overlayAnchor = null;
            _deliveryOperation = null;
            _deliveryOperationId = null;
            throw;
        }
    }

    // The completed operation must still own the session before any target insertion.
    private bool IsDeliveryCurrent()
    {
        var operation = _deliveryOperation;
        var operationId = _deliveryOperationId;
        if (operation is null || operationId is null || _closing || _closed) { return false; }
        var state = _session.State;
        return state.OperationId == operationId && state.IsIdle && operation.IsCompletedSuccessfully
            && operation.Result.Kind == SessionOutcomeKind.Completed;
    }

    private async Task DeliverAsync(Task<SessionOutcome> operation, FinalTextDelivery delivery)
    {
        var token = _deliveryCancellation.Token;
        var operationId = _deliveryOperationId;
        try
        {
            var outcome = await delivery.RunAsync(operation, token);
            if (!IsDeliveryCurrent() || outcome.Kind != SessionOutcomeKind.Completed) { return; }
            // Real insertion outcome for the Home timeline tag. Deferred-only deliveries report nothing.
            if (operationId is { } completedId && delivery.Delivery is { } reported)
            {
                _history.RecordDelivery(completedId, reported.Status);
            }
            if (delivery.Error is not null)
                _session.Notify(NoticeKind.Warning, "Insertion stopped",
                    $"{delivery.Error} Check the destination before using Copy transcript; do not repeat text that already appeared.");
            else if (delivery.Sent && delivery.Delivery?.Status != TextDeliveryStatus.Verified)
                _session.Notify(NoticeKind.Warning, "Text sent; unverified",
                    delivery.Delivery?.Message ?? "The insertion input was sent. Check the destination.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { _session.ReportUiError(error); }
        finally
        {
            if (ReferenceEquals(_delivery, delivery))
            {
                _delivery = null;
                _deliveryOperation = null;
                _deliveryOperationId = null;
            }
        }
    }

    private async void CloseRequested(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) { return; }
        args.Cancel = true;
        if (_closing) { return; }
        _closing = true;
        MainContent.IsEnabled = false;
        ShutdownError.IsOpen = false;
        ShutdownError.Visibility = Visibility.Collapsed;
        _deliveryCancellation.Cancel();
        _startupCancellation.Cancel();
        try
        {
            if (_startupTask is not null) await _startupTask;
            if (_meetingRecoveryTask is not null) await _meetingRecoveryTask;
            if (_permissionRetryTask is not null) await _permissionRetryTask;
            if (_microphoneSettingsTask is not null) await _microphoneSettingsTask;
            await _meetings.BeginShutdownAsync();
            HideMeetingSuggestion();
            _detectionCancellation?.Cancel();
            if (_detectionTask is not null) await _detectionTask;
            if (_detectionActionTask is not null) await _detectionActionTask;
            var meetings = _meetingWindows.ToArray();
            await Task.WhenAll(meetings.Select(meeting => meeting.window.BeginShutdownAsync()));
            foreach (var meeting in meetings) await meeting.session.DisposeAsync();
            foreach (var meeting in _meetingWindows.ToArray()) meeting.window.Close();
            await Task.WhenAll(_session.CloseAsync(), _models.DisposeAsync().AsTask(), _dictation.FinishBackgroundWorkAsync());
            _history.Dispose();
            if (_captureTask is { } capture)
            {
                try { await capture; }
                catch (Exception error) { Debug.WriteLine($"Ansible: target inspection failed during close: {error.Message}"); }
            }
            if (_pressTask is not null)
            {
                try { await _pressTask; }
                catch (Exception pressError) { Debug.WriteLine($"Ansible: shortcut press failed during close: {pressError.Message}"); }
            }
            if (_deliveryTask is not null) { await _deliveryTask; }
            if (_releaseTailTask is not null) { await _releaseTailTask; }
            if (_floating is not null) { await _floating.ClosePreviewAsync(); }
            _allowClose = true;
            Close();
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Ansible: native shutdown needs recovery: {error.Message}");
            ShutdownError.Message = $"{error.Message} Waiting for owned work to stop.";
            ShutdownError.Visibility = Visibility.Visible;
            ShutdownError.IsOpen = true;
            var failures = new List<Exception> { error };
            _detectionCancellation?.Cancel();
            try { await _session.CancelAsync(); }
            catch (Exception stopError) { failures.Add(stopError); }
            var joined = Task.WhenAll(new[]
            {
                _startupTask, _meetingRecoveryTask, _permissionRetryTask, _microphoneSettingsTask,
                _detectionTask, _detectionActionTask, _captureTask, _pressTask, _deliveryTask, _releaseTailTask
            }.OfType<Task>());
            try { await joined; }
            catch (Exception)
            {
                if (joined.Exception is { } joinError) failures.AddRange(joinError.Flatten().InnerExceptions);
            }
            _pressTask = null;
            _deliveryCancellation.Dispose();
            _deliveryCancellation = new CancellationTokenSource();
            _startupCancellation.Dispose();
            _startupCancellation = new CancellationTokenSource();
            var failure = failures.Count == 1 ? error : new AggregateException("Shutdown and recovery reported failures.", failures);
            ShutdownError.Message = $"{failure.Message} Try closing the window again.";
            _closing = false;
            MainContent.IsEnabled = true;
            _meetings.ResumeAfterFailedShutdown();
            _permissionCheckComplete = true;
            RenderMicrophonePermission();
            StartMeetingDetection();
        }
    }

    private void WindowClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        if (_themeSettings is not null) _themeSettings.Changed -= ContrastThemeChanged;
        _themeSettings = null;
        _dictation.Dispose();
        _history.Dispose();
        _hotkey?.Dispose();
        _deliveryCancellation.Dispose();
        _startupCancellation.Dispose();
        _detectionCancellation?.Dispose();
        _observer.Dispose();
        AppWindow.Closing -= CloseRequested;
    }
}
