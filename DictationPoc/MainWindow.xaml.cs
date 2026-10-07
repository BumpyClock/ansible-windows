using System.Diagnostics;
using DictationPoc.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DictationPoc;

public sealed partial class MainWindow : Window
{
    private readonly DictationSession _session;
    private readonly InsightsPage _insights;
    private readonly DictationPage _dictation;
    private readonly MainPage _settings;
    private readonly ModelManagementPage _models;
    private readonly UiSessionObserver _observer;
    private GlobalDictationHotkey? _hotkey;
    private DictationShortcut _configuredHotkey = DictationShortcut.Default;
    private volatile bool _shortcutHeld;
    private FloatingDictationWindow? _floating;
    private bool _initialized;
    private bool _closed;
    private bool _allowClose;
    private bool _closing;
    private bool _popupFailed;
    private readonly ShortcutCaptureCoordinator _captureCoordinator = new();
    private readonly CancellationTokenSource _deliveryCancellation = new();
    private Task<(bool available, CapturedTextTarget? target, string reason)>? _captureTask;
    private Task? _deliveryTask;
    private LiveTextDelivery? _delivery;
    private Task<SessionOutcome>? _deliveryOperation;
    private Guid? _deliveryOperationId;

    internal MainWindow(DictationSession session, AppPaths paths, HttpClient modelDownloads)
    {
        InitializeComponent();
        _session = session;
        _insights = new InsightsPage(session);
        _dictation = new DictationPage(session);
        _settings = new MainPage(session, paths, () => WinRT.Interop.WindowNative.GetWindowHandle(this),
            ChangeHotkey, ChangeDictationMode, CaptureShortcut);
        _models = new ModelManagementPage(session, paths, () => WinRT.Interop.WindowNative.GetWindowHandle(this), modelDownloads);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min(1700, area.Width - 60), Math.Min(1100, area.Height - 60)));
        MainContent.Loaded += Initialize;
        AppWindow.Closing += CloseRequested;
        Closed += WindowClosed;
        Navigate("insights");
        _observer = new UiSessionObserver(session, DispatcherQueue, SessionChanged);
    }

    private async void Initialize(object sender, RoutedEventArgs args)
    {
        if (_initialized) { return; }
        _initialized = true;
        try
        {
            await _session.InitializeAsync();
            if (_closed || _closing) { return; }
            if (_session.State.Phase == DictationPhase.Disconnected && _session.State.Models.Count == 0)
                Navigate("models");
            try
            {
                _configuredHotkey = _session.State.Settings.Shortcut;
                _hotkey = new GlobalDictationHotkey(WinRT.Interop.WindowNative.GetWindowHandle(this),
                    _configuredHotkey, HotkeyChanged);
            }
            catch (Exception error) { _session.Notify(NoticeKind.Error, "Shortcut unavailable", error.Message); }
        }
        catch (Exception error) { _session.ReportUiError(error); }
    }

    private void NavigateClicked(object sender, RoutedEventArgs args) => Navigate((string)((Button)sender).Tag);

    private void Navigate(string page)
    {
        MainContent.Content = page switch { "settings" => _settings, "dictation" => _dictation, "models" => _models, _ => _insights };
        foreach (var button in new[] { InsightsNav, DictationNav, SettingsNav, ModelsNav })
        {
            button.Style = (Style)Application.Current.Resources[
                (string)button.Tag == page ? "SelectedNavigationButtonStyle" : "NavigationButtonStyle"];
            AutomationProperties.SetHelpText(button, (string)button.Tag == page ? "Current page" : "");
        }
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
        SidebarStatus.Text = state.Phase == DictationPhase.Disconnected
            ? "Choose a model in Speech models."
            : state.Notice.Title;
        SidebarStatus.Visibility = state.Phase == DictationPhase.Ready &&
            state.Notice.Kind is not (NoticeKind.Warning or NoticeKind.Error)
                ? Visibility.Collapsed : Visibility.Visible;
        var showWaveform = state.Phase is DictationPhase.Preparing or DictationPhase.Recording &&
            (_delivery is null || !state.Settings.PushToTalk || _shortcutHeld);
        if (!showWaveform) { _popupFailed = false; }
        if (showWaveform && _floating is null && !_popupFailed)
        {
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
        }
        if (showWaveform) { _floating?.ShowPreview(); }
        else { _floating?.HidePreview(); }
    }

    private async void HotkeyChanged(bool pressed)
    {
        _shortcutHeld = pressed;
        if (_closed || _closing) { return; }
        try
        {
            if (!pressed) { await HandleReleaseAsync(); }
            else { await HandlePressAsync(); }
        }
        catch (Exception error) { _session.ReportUiError(error); }
    }

    // Shortcut release. Marks a pending push-to-talk capture stale so its inspection cannot start a late
    // recording, wakes the delivery pump to flush text retained while a modifier was held, and finishes or
    // cancels a push-to-talk dictation from a single State snapshot so a Preparing->Recording transition
    // cannot be read inconsistently across two reads.
    private async Task HandleReleaseAsync()
    {
        var state = _session.State;
        if (state.Settings.PushToTalk) { _floating?.HidePreview(); }
        _captureCoordinator.NoteReleased();
        _delivery?.SignalRelease();
        if (state.Settings.PushToTalk && _delivery is not null)
        {
            if (state.CanFinish) { await _session.FinishAsync(); }
            else if (state.Phase == DictationPhase.Preparing) { await _session.CancelAsync(); }
        }
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
                _session.Notify(NoticeKind.Warning, "Dictation unavailable", "Choose a streaming speech model before using the shortcut.");
            return;
        }
        if (state.SelectedModel?.EmitsAuthoritativeLiveSpeech != true)
        {
            _session.Notify(NoticeKind.Warning, "Live recognition model required",
                "This model does not emit authoritative incremental speech in this backend. Choose a live speech model such as Nemotron streaming in Speech models. Manual transcription remains available for other models.");
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
            var inspected = await inspection;
            if (_closing || _closed) { return; }
            if (!_captureCoordinator.IsCurrent(capture)) { return; }
            if (!inspected.available)
            {
                _session.Notify(NoticeKind.Warning, "No insertion target", inspected.reason);
                return;
            }
            if (!_session.State.CanStart) { return; }
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
        var delivery = new LiveTextDelivery(
            insert: (pending, token) => Task.Run(() =>
                WindowsTextTarget.Insert(target, pending, token, IsDeliveryCurrent), token),
            isCurrent: IsDeliveryCurrent,
            typingDeferred: () => _shortcutHeld && modifiers != 0,
            notify: _session.Notify);
        _delivery = delivery;
        try
        {
            var operation = _session.StartDictationAsync(new InlineTranscriptProgress(delivery.Report));
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

    // True while the current live delivery still owns the session operation and the UI can accept inserted
    // text: the operation id is unchanged, the window is not closing, and either recognition is still active
    // or it completed successfully and the session is idle for the final flush.
    private bool IsDeliveryCurrent()
    {
        var operation = _deliveryOperation;
        var operationId = _deliveryOperationId;
        if (operation is null || operationId is null || _closing || _closed) { return false; }
        var state = _session.State;
        if (state.OperationId != operationId) { return false; }
        if (operation.IsCompleted)
            return operation.IsCompletedSuccessfully && operation.Result.Kind == SessionOutcomeKind.Completed && state.IsIdle;
        return state.Phase is DictationPhase.Recording or DictationPhase.Finishing;
    }

    private async Task DeliverAsync(Task<SessionOutcome> operation, LiveTextDelivery delivery)
    {
        var pump = delivery.RunAsync(_deliveryCancellation.Token);
        try
        {
            var outcome = await operation;
            delivery.Complete();
            await pump;
            if (_closing || _closed || _session.State.OperationId != _deliveryOperationId) { return; }
            if (outcome.Kind == SessionOutcomeKind.Completed)
            {
                await delivery.InsertFinalAsync(outcome.Result?.SpeechText, _deliveryCancellation.Token);
                if (_closing || _closed || _session.State.OperationId != _deliveryOperationId) { return; }
                if (delivery.Error is not null)
                    _session.Notify(NoticeKind.Warning, "Insertion stopped",
                        $"{delivery.Error} {(delivery.Cursor.SentText.Length > 0 ? "Previously inserted text remains. " : "")}Use Copy transcript for the final text.");
                else if (delivery.Cursor.SentText.Length > 0)
                    _session.Notify(NoticeKind.Success, "Text sent", "Recognition updates were typed as they arrived. The final result was not inserted again.");
                else
                    _session.Notify(NoticeKind.Information, "No speech to insert",
                        "The model returned no speech text.");
            }
            else if (delivery.Cursor.SentText.Length > 0)
                _session.Notify(outcome.Kind == SessionOutcomeKind.Failed ? NoticeKind.Error : NoticeKind.Warning,
                    "Dictation stopped",
                    $"{outcome.Error?.Message} Previously inserted text remains; unfinished speech was not inserted.");
        }
        catch (OperationCanceledException) when (_deliveryCancellation.IsCancellationRequested) { }
        catch (Exception error) { _session.ReportUiError(error); }
        finally
        {
            delivery.Complete();
            try { await pump; }
            catch (OperationCanceledException) when (_deliveryCancellation.IsCancellationRequested) { }
            if (ReferenceEquals(_delivery, delivery))
            {
                _delivery = null;
                _deliveryOperation = null;
                _deliveryOperationId = null;
            }
        }
    }

    private sealed class InlineTranscriptProgress(Action<TranscriptUpdate> report) : IProgress<TranscriptUpdate>
    {
        public void Report(TranscriptUpdate value) => report(value);
    }

    private async void CloseRequested(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) { return; }
        args.Cancel = true;
        if (_closing) { return; }
        _closing = true;
        _deliveryCancellation.Cancel();
        try
        {
            await Task.WhenAll(_session.CloseAsync(), _models.DisposeAsync().AsTask());
            if (_captureTask is { } capture)
            {
                try { await capture; }
                catch (Exception error) { Debug.WriteLine($"Local Voice: target inspection failed during close: {error.Message}"); }
            }
            if (_deliveryTask is not null) { await _deliveryTask; }
            if (_floating is not null) { await _floating.ClosePreviewAsync(); }
            _allowClose = true;
            Close();
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Local Voice: native shutdown needs recovery: {error.Message}");
            SidebarStatus.Text = $"Close failed. {error.Message} Try closing the window again.";
            _closing = false;
        }
    }

    private void WindowClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _hotkey?.Dispose();
        _deliveryCancellation.Dispose();
        _observer.Dispose();
        AppWindow.Closing -= CloseRequested;
    }
}
