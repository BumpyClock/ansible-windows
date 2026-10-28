using System.Diagnostics;

namespace Ansible.Core;

/// <summary>The focused destination captured at shortcut press.</summary>
public interface IShortcutTextTarget
{
    CaretBounds? Caret { get; }

    /// <summary>Inserts final speech once. The adapter re-checks <paramref name="isCurrent"/> immediately before input.</summary>
    Task<TextDeliveryOutcome> InsertAsync(string text, Func<bool> isCurrent, CancellationToken cancellationToken);
}

/// <summary>
/// Inspects the current foreground destination. Throws when the destination cannot receive text; the exception
/// message is the user-facing reason. Insertion settings are snapshotted from <c>settings</c>.
/// </summary>
public interface IShortcutTargetInspector
{
    Task<IShortcutTextTarget> InspectAsync(AppSettings settings, CancellationToken cancellationToken);
}

/// <summary>
/// Owns one shortcut dictation attempt: press and release, the push-to-talk tail, admission, destination
/// inspection, recognition start, insert-once delivery, warnings, diagnostics, and the shutdown join.
/// Call every member on the UI thread. Continuations return to the caller's synchronization context, so fields
/// stay single-threaded; only the held flag and <see cref="IsDeliveryCurrent"/> are read from delivery threads.
/// </summary>
public sealed class ShortcutDictation : IDisposable
{
    private readonly DictationSession _session;
    private readonly IShortcutTargetInspector _inspector;
    private readonly LocalDiagnosticLog? _diagnostics;
    private readonly Action<Guid, TextDeliveryStatus> _deliveryRecorded;
    private readonly Action<CaretBounds?> _anchorChanged;
    private readonly ShortcutCaptureCoordinator _coordinator;
    private CancellationTokenSource _cancellation = new();
    private volatile bool _held;
    private volatile bool _closing;
    private volatile bool _closed;
    private Task<IShortcutTextTarget>? _captureTask;
    private Task? _pressTask;
    private Task? _deliveryTask;
    private Task? _releaseTailTask;
    private FinalTextDelivery? _delivery;
    private Task<SessionOutcome>? _deliveryOperation;
    private Guid? _deliveryOperationId;

    public ShortcutDictation(
        DictationSession session,
        IShortcutTargetInspector inspector,
        LocalDiagnosticLog? diagnostics,
        Action<Guid, TextDeliveryStatus> deliveryRecorded,
        Action<CaretBounds?> anchorChanged,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _session = session;
        _inspector = inspector;
        _diagnostics = diagnostics;
        _deliveryRecorded = deliveryRecorded;
        _anchorChanged = anchorChanged;
        _coordinator = new ShortcutCaptureCoordinator(delay);
    }

    /// <summary>True while an admitted press owns a pending inspection or a delivery is still running.</summary>
    public bool IsBusy => _coordinator.HasPendingCapture || _deliveryTask is { IsCompleted: false };

    public async Task PressedAsync(DictationShortcut shortcut)
    {
        _held = true;
        if (_closed || _closing) { return; }
        _coordinator.NotePressed();
        try
        {
            var task = HandlePressAsync(shortcut);
            _pressTask = _pressTask is { IsCompleted: false } previous ? Task.WhenAll(previous, task) : task;
            await task;
        }
        catch (Exception error) { _session.ReportUiError(error); }
    }

    // Marks a pending push-to-talk capture stale so its inspection cannot start a late recording. Once admitted,
    // capture retains a short tail owned by this release and delivery operation.
    public async Task ReleasedAsync()
    {
        _held = false;
        if (_closed || _closing) { return; }
        try
        {
            var state = _session.State;
            var release = _coordinator.NoteReleased();
            if (state.Settings.PushToTalk && _delivery is not null)
            {
                var task = FinishReleasedDictationAsync(release, _delivery, state.OperationId);
                _releaseTailTask = _releaseTailTask is { IsCompleted: false } previous
                    ? Task.WhenAll(previous, task) : task;
                await task;
            }
        }
        catch (Exception error) { _session.ReportUiError(error); }
    }

    /// <summary>Stops admitting presses and cancels delivery. Call <see cref="JoinAsync"/> next.</summary>
    public void BeginShutdown()
    {
        _closing = true;
        _cancellation.Cancel();
    }

    /// <summary>Waits for every owned inspection, press, delivery, and release-tail task.</summary>
    public async Task JoinAsync()
    {
        if (_captureTask is { } capture)
        {
            try { await capture; }
            catch (Exception error) { Debug.WriteLine($"Ansible: target inspection failed during close: {error.Message}"); }
        }
        if (_pressTask is { } press)
        {
            try { await press; }
            catch (Exception error) { Debug.WriteLine($"Ansible: shortcut press failed during close: {error.Message}"); }
        }
        if (_deliveryTask is { } delivery) { await delivery; }
        if (_releaseTailTask is { } releaseTail) { await releaseTail; }
    }

    /// <summary>Accepts shortcut presses again after a shutdown that could not complete.</summary>
    public void ResumeAfterFailedShutdown()
    {
        _pressTask = null;
        _cancellation.Dispose();
        _cancellation = new CancellationTokenSource();
        _closing = false;
    }

    public void Dispose()
    {
        _closed = true;
        _cancellation.Dispose();
    }

    private async Task FinishReleasedDictationAsync(
        ShortcutCaptureCoordinator.Release release, FinalTextDelivery delivery, Guid? operationId)
    {
        var token = _cancellation.Token;
        try
        {
            if (!await _coordinator.WaitForReleaseTailAsync(release, token)) { return; }
            if (_closing || _closed || _held || !ReferenceEquals(_delivery, delivery)) { return; }
            var state = _session.State;
            if (state.OperationId != operationId || !state.Settings.PushToTalk) { return; }
            if (state.CanFinish) { await _session.FinishAsync(); }
            else if (state.Phase == DictationPhase.Preparing) { await _session.CancelAsync(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { _session.ReportUiError(error); }
    }

    // Recording and destination inspection start together. The session buffers speech until the destination
    // is accepted; insertion still requires the original field identity when recognition finishes.
    private async Task HandlePressAsync(DictationShortcut shortcut)
    {
        var token = _cancellation.Token;
        var generation = _diagnostics?.CurrentGeneration;
        var started = generation is null ? 0 : Stopwatch.GetTimestamp();
        var attemptId = generation is null ? Guid.Empty : Guid.NewGuid();
        var recorded = false;
        void Record(ShortcutDiagnosticOutcome outcome, Guid? operationId = null, Exception? error = null)
        {
            if (generation is not { } value || recorded) return;
            recorded = true;
            _diagnostics!.Write(new ShortcutDiagnostic(attemptId, DateTimeOffset.UtcNow, outcome,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, operationId, error?.HResult), value);
        }
        if (_coordinator.HasPendingCapture) { Record(ShortcutDiagnosticOutcome.CapturePending); return; }
        var state = _session.State;
        if (state.CanFinish)
        {
            if (!state.Settings.PushToTalk) { await _session.FinishAsync(); }
            return;
        }
        if (_captureTask is { IsCompleted: false }) { Record(ShortcutDiagnosticOutcome.CapturePending); return; }
        if (_deliveryTask is { IsCompleted: false }) { Record(ShortcutDiagnosticOutcome.DeliveryPending); return; }
        if (!state.CanStart)
        {
            if (state.IsIdle)
                _session.Notify(NoticeKind.Warning, "Dictation unavailable", "Choose a speech model that supports microphone dictation before using the shortcut.");
            Record(ShortcutDiagnosticOutcome.SessionUnavailable);
            return;
        }
        if (state.SelectedModel?.CanInsertDictation != true)
        {
            _session.Notify(NoticeKind.Warning, "Speech text required",
                "This model does not support speech text extraction for shortcut insertion. Choose another speech model. Manual transcription remains available.");
            Record(ShortcutDiagnosticOutcome.ModelIneligible);
            return;
        }
        var capture = _coordinator.TryBeginCapture(state.Settings.PushToTalk, _deliveryTask is { IsCompleted: false });
        if (capture is null) { Record(ShortcutDiagnosticOutcome.AdmissionRejected); return; }
        Task<IShortcutTextTarget>? inspection = null;
        try
        {
            // Let the hotkey adapter report an already-released chord before admitting recording.
            await Task.Yield();
            if (token.IsCancellationRequested || _closing || _closed) { Record(ShortcutDiagnosticOutcome.Cancelled); return; }
            if (!_coordinator.IsCurrent(capture)) { Record(ShortcutDiagnosticOutcome.Stale); return; }
            var current = _session.State;
            if (!current.CanStart || current.SelectedModel?.CanInsertDictation != true)
            { Record(ShortcutDiagnosticOutcome.AdmissionRejected); return; }
            if (!_coordinator.TryStartDelivery(capture)) { Record(ShortcutDiagnosticOutcome.Stale); return; }
            inspection = _inspector.InspectAsync(current.Settings, token);
            _captureTask = inspection;
            StartDelivery(inspection, generation == _diagnostics?.CurrentGeneration && generation is not null ? started : null,
                shortcut, current.Settings);
            var operationId = _deliveryOperationId;
            var inspected = await inspection;
            if (token.IsCancellationRequested || _closing || _closed) { Record(ShortcutDiagnosticOutcome.Cancelled); return; }
            if (_deliveryOperationId == operationId && _session.State.IsLiveOperation) { _anchorChanged(inspected.Caret); }
            Record(ShortcutDiagnosticOutcome.Admitted, operationId);
        }
        catch (Exception error) { Record(ShortcutDiagnosticOutcome.AdmissionRejected, error: error); throw; }
        finally
        {
            _coordinator.Abandon(capture);
            if (ReferenceEquals(_captureTask, inspection)) { _captureTask = null; }
        }
    }

    private void StartDelivery(Task<IShortcutTextTarget> target, long? shortcutTimestamp,
        DictationShortcut shortcut, AppSettings settings)
    {
        var modifiers = shortcut.Modifiers;
        var method = settings.InsertionMethod;
        var pasteShortcut = settings.PasteShortcut;
        var diagnostics = _diagnostics;
        var diagnosticGeneration = diagnostics?.CurrentGeneration;
        Guid? diagnosticOperationId = null;
        var delivery = new FinalTextDelivery(
            insert: async (pending, token) =>
            {
                var inspected = await target.WaitAsync(token);
                return await inspected.InsertAsync(pending, IsDeliveryCurrent, token);
            },
            isCurrent: IsDeliveryCurrent,
            typingDeferred: () => _held && modifiers != 0,
            diagnostic: diagnosticGeneration is { } generation ? trace =>
            {
                if (diagnosticOperationId is { } id)
                    diagnostics!.Write(new DeliveryDiagnostic(id, DateTimeOffset.UtcNow, trace.Outcome, method,
                        method == TextInsertionMethod.Paste ? pasteShortcut : null,
                        trace.ResultToDeliveryMilliseconds, trace.DeferredMilliseconds, trace.ErrorHResult)
                    {
                        Reason = trace.Reason, ResultToSendMilliseconds = trace.ResultToSendMilliseconds,
                        VerificationMilliseconds = trace.VerificationMilliseconds
                    }, generation);
            } : null);
        _delivery = delivery;
        _anchorChanged(null);
        try
        {
            var operation = _session.StartShortcutDictationAsync(shortcutTimestamp, target);
            _deliveryOperation = operation;
            _deliveryOperationId = _session.State.OperationId;
            diagnosticOperationId = _deliveryOperationId;
            _deliveryTask = DeliverAsync(operation, delivery);
        }
        catch
        {
            _delivery = null;
            _anchorChanged(null);
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
        var token = _cancellation.Token;
        var operationId = _deliveryOperationId;
        try
        {
            var outcome = await delivery.RunAsync(operation, token);
            if (!IsDeliveryCurrent() || outcome.Kind != SessionOutcomeKind.Completed) { return; }
            // Real insertion outcome for the Home timeline tag. Deferred-only deliveries report nothing.
            if (operationId is { } completedId && delivery.Delivery is { } reported)
                _deliveryRecorded(completedId, reported.Status);
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
}
