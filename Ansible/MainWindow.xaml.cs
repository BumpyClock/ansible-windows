using System.Diagnostics;
using Ansible.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ansible;

public sealed partial class MainWindow : Window
{
    private readonly DictationSession _session;
    private readonly InsightsPage _insights;
    private readonly DictationPage _dictation;
    private readonly MainPage _settings;
    private readonly ModelManagementPage _models;
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
    private bool _meetingRecoveryComplete;
    private bool _closed;
    private bool _allowClose;
    private bool _closing;
    private bool _popupFailed;
    private bool _creatingFloating;
    private bool? _capturePrivacy;
    private readonly ShortcutCaptureCoordinator _captureCoordinator = new();
    private readonly CancellationTokenSource _deliveryCancellation = new();
    private Task<(bool available, CapturedTextTarget? target, string reason)>? _captureTask;
    private Task? _deliveryTask;
    private Task? _releaseTailTask;
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
    private Task? _permissionRetryTask;
    private Task? _microphoneSettingsTask;
    private bool _launchReady;
    private bool _permissionCheckComplete;

    internal MainWindow(DictationSession session, AppPaths paths, HttpClient modelDownloads, MicrophonePermissionPreflight microphonePermission)
    {
        InitializeComponent();
        _session = session;
        _microphonePermission = microphonePermission;
        _paths = paths;
        _meetingStore = new MeetingStore(Path.Combine(Path.GetDirectoryName(paths.Settings)!, "meetings"));
        _meetings = new MeetingsPage(_meetingStore, OpenMeetingAsync, () => WinRT.Interop.WindowNative.GetWindowHandle(this));
        _insights = new InsightsPage(session);
        _dictation = new DictationPage(session, paths, () => WinRT.Interop.WindowNative.GetWindowHandle(this));
        _settings = new MainPage(session, paths, () => WinRT.Interop.WindowNative.GetWindowHandle(this),
            ChangeHotkey, ChangeDictationMode, CaptureShortcut);
        _models = new ModelManagementPage(session, paths, () => WinRT.Interop.WindowNative.GetWindowHandle(this), modelDownloads);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var size = new Windows.Graphics.SizeInt32(Math.Min(1700, area.Width - 60), Math.Min(1100, area.Height - 60));
        AppWindow.Resize(size);
        AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
        MainContent.Loaded += Initialize;
        Activated += async (_, _) =>
        {
            if (_closed || _closing) return;
            if (_permissionCheckComplete && _permissionRetryTask is not { IsCompleted: false })
            {
                _microphonePermission.CheckAccess();
                RenderMicrophonePermission();
            }
            await _meetings.RefreshAsync();
        };
        AppWindow.Closing += CloseRequested;
        Closed += WindowClosed;
        Navigate("dictation");
        _observer = new UiSessionObserver(session, DispatcherQueue, SessionChanged);
    }

    private async void Initialize(object sender, RoutedEventArgs args)
    {
        if (_initialized) { return; }
        _initialized = true;
        _startupTask = InitializeAsync(_startupCancellation.Token);
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
            Exception? meetingRecoveryError = null;
            IReadOnlyList<MeetingReadFailure> meetingRecoveryFailures = [];
            try
            {
                meetingRecoveryFailures = await _meetingStore.MarkInterruptedSessionsAsync(token);
                _meetingRecoveryComplete = true;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                meetingRecoveryError = error;
            }
            token.ThrowIfCancellationRequested();
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
            catch (Exception error) { _session.Notify(NoticeKind.Error, "Shortcut unavailable", error.Message); }
            if (meetingRecoveryError is not null)
                _session.Notify(NoticeKind.Error, "Meeting recovery failed", meetingRecoveryError.Message);
            else if (meetingRecoveryFailures.Count > 0)
                _session.Notify(NoticeKind.Warning, "Some saved transcripts unavailable",
                    "Open Meetings to export and remove unreadable files. New meetings are still available.");
            StartMeetingDetection();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { _session.ReportUiError(error); }
    }

    private void RenderMicrophonePermission()
    {
        if (_closing || _closed) return;
        if (!_permissionCheckComplete) return;
        var status = _microphonePermission.Status;
        var allowed = status == MicrophonePermissionStatus.Allowed;
        MicrophonePermissionNotice.IsOpen = !allowed;
        MicrophonePermissionNotice.Visibility = allowed ? Visibility.Collapsed : Visibility.Visible;
        MicrophonePermissionNotice.Message = status switch
        {
            MicrophonePermissionStatus.DeniedByUser => "Enable microphone access for Ansible and desktop apps in Windows Settings, then check again. No audio has been captured by this permission check.",
            MicrophonePermissionStatus.DeniedBySystem => "Windows blocks microphone access. Enable microphone access in Windows Settings, or contact your administrator.",
            MicrophonePermissionStatus.NotDeclared => "This package does not declare microphone access. Install an Ansible package with the microphone capability.",
            MicrophonePermissionStatus.PromptRequired => "Microphone permission has not been granted. Check again to request access.",
            _ => "Microphone permission could not be checked. Review Windows microphone privacy settings, then check again."
        };
        CheckMicrophonePermissionButton.IsEnabled = _permissionRetryTask is not { IsCompleted: false };
    }

    private async void CheckMicrophonePermissionClicked(object sender, RoutedEventArgs args)
    {
        if (_closing || _closed || !_permissionCheckComplete || _permissionRetryTask is { IsCompleted: false }) return;
        _permissionRetryTask = RetryMicrophonePermissionAsync();
        await _permissionRetryTask;
    }

    private async Task RetryMicrophonePermissionAsync()
    {
        CheckMicrophonePermissionButton.IsEnabled = false;
        try { await _microphonePermission.InitializeAsync(_startupCancellation.Token); }
        catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested) { }
        catch (Exception error) { if (!_closing) _session.ReportUiError(error); }
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
                _session.Notify(NoticeKind.Warning, "Windows Settings unavailable", "Open Windows Settings > Privacy & security > Microphone.");
        }
        catch (Exception error) { if (!_closing) _session.ReportUiError(error); }
    }

    private void NavigateClicked(object sender, RoutedEventArgs args) => Navigate((string)((Button)sender).Tag);

    private void Navigate(string page)
    {
        MainContent.Content = page switch { "settings" => _settings, "dictation" => _dictation, "models" => _models, "meetings" => _meetings, _ => _insights };
        foreach (var (button, indicator) in new[]
        {
            (DictationNav, DictationIndicator), (ModelsNav, ModelsIndicator),
            (MeetingsNav, MeetingsIndicator),
            (InsightsNav, InsightsIndicator), (SettingsNav, SettingsIndicator),
        })
        {
            var selected = (string)button.Tag == page;
            button.Style = (Style)Application.Current.Resources[
                selected ? "SelectedNavigationButtonStyle" : "NavigationButtonStyle"];
            indicator.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetHelpText(button, selected ? "Current page" : "");
            AutomationProperties.SetItemStatus(button, selected ? "Current page" : "");
        }
    }

    private Task OpenMeetingAsync(MeetingDocument? document)
    {
        OpenMeeting(document).Activate();
        return Task.CompletedTask;
    }

    private MeetingWindow OpenMeeting(MeetingDocument? document)
    {
        var existing = _meetingWindows.FirstOrDefault(item => document is null
            ? item.savedId is null && (item.session.IsActive || item.session.Document is null)
            : item.savedId == document.Id || item.session.Document?.Id == document.Id);
        if (existing.window is not null) return existing.window;
        var state = _session.State;
        var meeting = new MeetingSession(
            () => new NativeAudioEngine(_paths.NativeLibrary, _paths.ModelCatalog, state.ModelsDirectory,
                deviceProbePath: _paths.DeviceProbe, benchmarkSamplePath: _paths.PublicSample,
                benchmarkCachePath: _paths.DeviceBenchmarks),
            new PermissionCheckedMeetingCaptureFactory(new Ansible.Core.Windows.MeetingCaptureFactory(), _microphonePermission), _meetingStore,
            new RecognitionOptions(state.Language, state.Settings.CustomDictionary, state.Settings.Backend),
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
            alignerFactory: () => new NativeMeetingWordAligner(_paths.NativeLibrary, _paths.ModelCatalog, state.ModelsDirectory));
        var window = new MeetingWindow(meeting, _meetingStore, document);
        try { window.ApplyCapturePrivacy(state.Settings.HideFromCapture); }
        catch (Exception error) { _session.Notify(NoticeKind.Warning, "Screen capture privacy unavailable", error.Message); }
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
            _meetingWindows.RemoveAll(item => ReferenceEquals(item.window, window));
            try { await meeting.DisposeAsync(); await _meetings.RefreshAsync(); }
            catch (Exception error) { _session.ReportUiError(error); }
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
                                $"{error.Message} You can start notetaking from Meetings. Detection will retry.");
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
                _session.Notify(NoticeKind.Information, "Meeting changed", "The detected Teams app is no longer available. Select your meeting app from Meetings.");
                return;
            }
            _meetingDetection.Dismiss(expected);
            HideMeetingSuggestion();
            window.Activate(); // Only the explicit action activates the notetaker; it never starts capture.
        }
        catch (OperationCanceledException) when (_closing || _closed) { }
        catch (Exception error) { HideMeetingSuggestion(); _session.Notify(NoticeKind.Warning, "Meeting detection unavailable", error.Message); }
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
                _session.Notify(NoticeKind.Warning, "Screen capture privacy unavailable", string.Join(" ", failures.Distinct()));
        }
        var showWaveform = state.Phase is DictationPhase.Preparing or DictationPhase.Recording;
        if (!showWaveform) { _popupFailed = false; }
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
        if (showWaveform) { _floating?.ShowPreview(); }
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
            else { await HandlePressAsync(); }
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
        try
        {
            if (!await _captureCoordinator.WaitForReleaseTailAsync(release, _deliveryCancellation.Token)) { return; }
            if (_closing || _closed || _shortcutHeld || !ReferenceEquals(_delivery, delivery)) { return; }
            var state = _session.State;
            if (state.OperationId != operationId || !state.Settings.PushToTalk) { return; }
            if (state.CanFinish) { await _session.FinishAsync(); }
            else if (state.Phase == DictationPhase.Preparing) { await _session.CancelAsync(); }
        }
        catch (OperationCanceledException) when (_deliveryCancellation.IsCancellationRequested) { }
        catch (Exception error) { _session.ReportUiError(error); }
    }

    // Shortcut press. Admits at most one interaction to own the target inspection; a press arriving while an
    // inspection is pending or a delivery is active is ignored. After the inspection completes it rechecks
    // ownership and idle admission before starting delivery, so a stale or superseded press cannot start or
    // overwrite another interaction's recording.
    private async Task HandlePressAsync()
    {
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
        var inspection = Task.Run(() =>
        {
            var available = WindowsTextTarget.TryCapture(windowHandle, out var target, out var reason);
            return (available, target, reason);
        });
        _captureTask = inspection;
        try
        {
            // Let the hotkey adapter report an already-released chord before admitting recording.
            await Task.Yield();
            var inspected = await inspection;
            if (_closing || _closed) { return; }
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
        try
        {
            var outcome = await delivery.RunAsync(operation, _deliveryCancellation.Token);
            if (!IsDeliveryCurrent() || outcome.Kind != SessionOutcomeKind.Completed) { return; }
            if (delivery.Error is not null)
                _session.Notify(NoticeKind.Warning, "Insertion stopped",
                    $"{delivery.Error} Check the destination before using Copy transcript; do not repeat text that already appeared.");
            else if (delivery.Sent)
                _session.Notify(delivery.Delivery?.Status == TextDeliveryStatus.Verified ? NoticeKind.Success : NoticeKind.Information,
                    delivery.Delivery?.Status == TextDeliveryStatus.Verified ? "Insertion verified" : "Text sent; unverified",
                    delivery.Delivery?.Message ?? "The insertion input was sent. Check the destination.", showInStatus: true);
            else
                _session.Notify(NoticeKind.Information, "No speech to insert", "The model returned no speech text.", showInStatus: true);
        }
        catch (OperationCanceledException) when (_deliveryCancellation.IsCancellationRequested) { }
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
        ShutdownError.IsOpen = false;
        ShutdownError.Visibility = Visibility.Collapsed;
        _deliveryCancellation.Cancel();
        _startupCancellation.Cancel();
        try
        {
            if (_startupTask is not null) await _startupTask;
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
            await Task.WhenAll(_session.CloseAsync(), _models.DisposeAsync().AsTask());
            if (_captureTask is { } capture)
            {
                try { await capture; }
                catch (Exception error) { Debug.WriteLine($"Ansible: target inspection failed during close: {error.Message}"); }
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
            ShutdownError.Message = $"{error.Message} Try closing the window again.";
            ShutdownError.Visibility = Visibility.Visible;
            ShutdownError.IsOpen = true;
            _closing = false;
            _meetings.ResumeAfterFailedShutdown();
            // All launch/retry tasks above have been joined; allow a fresh permission retry after failed shutdown.
            _startupCancellation.Dispose();
            _startupCancellation = new CancellationTokenSource();
            _permissionCheckComplete = true;
            RenderMicrophonePermission();
            StartMeetingDetection();
        }
    }

    private void WindowClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _hotkey?.Dispose();
        _deliveryCancellation.Dispose();
        _startupCancellation.Dispose();
        _detectionCancellation?.Dispose();
        _observer.Dispose();
        AppWindow.Closing -= CloseRequested;
    }
}
