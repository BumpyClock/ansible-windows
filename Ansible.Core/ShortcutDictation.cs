using System.Diagnostics;

namespace Ansible.Core;

/// <summary>The focused destination captured at shortcut press.</summary>
public interface IShortcutTextTarget
{
    CaretBounds? Caret { get; }

    /// <summary>Inserts final speech once. The adapter re-checks <paramref name="isCurrent"/> immediately before input.</summary>
    Task<TextDeliveryOutcome> InsertAsync(BundlePayload payload, Func<bool> isCurrent, CancellationToken cancellationToken);
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
/// Owns shortcut dictation and explicit record paste: press and release, the push-to-talk tail, admission,
/// destination inspection, recognition, retained record bundles, delivery, diagnostics, and the shutdown join.
/// Call every member on the UI thread. Continuations return to the caller's synchronization context, so fields
/// stay single-threaded; only the hold flags and <see cref="IsDeliveryCurrent"/> are read from delivery threads.
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
    private Guid? _deliveryCurrencyId;
    private (Guid OperationId, SessionOutcome Outcome)? _recordResult;
    private volatile bool _pasteHeld;
    private ShortcutActivation? _owner;
    private bool _pushToTalk;
    private volatile bool _discarded;
    private CancellationTokenSource? _attemptCancellation;
    public bool HasRecordAttempt => _owner == ShortcutActivation.Record;
    public event Action? RecordAttemptChanged;
    public BundlePayload? RecordPayload => _recordResult?.Outcome.Result?.Payload;
    public event Action? RecordResultChanged;

    public async Task CancelRecordAsync()
    {
        if (!HasRecordAttempt) return;
        var id = _deliveryOperationId;
        var work = _deliveryTask;
        _discarded = true;
        _attemptCancellation?.Cancel();
        if (id is not null && _session.State.OperationId == id)
            await _session.CancelAsync();
        if (work is not null) await work;
        if (id is { } completedId)
            _session.DiscardRecordResult(completedId);
    }

    /// <summary>Pastes the latest completed record take into the destination focused at this press.</summary>
    public Task PasteRecordPressedAsync()
    {
        if (_closed || _closing || _pasteHeld) return Task.CompletedTask;
        _pasteHeld = true;
        var state = _session.State;
        if (!state.Settings.RecordChordEnabled)
            _session.Notify(NoticeKind.Warning, "Record paste unavailable", "Enable Record chord in Settings before pasting a record take.");
        else if (IsBusy || !state.IsIdle && !(state.Phase == DictationPhase.RecoveryRequired &&
                     _recordResult?.Outcome.MediaResourcesRetained == true))
            _session.Notify(NoticeKind.Warning, "Record paste busy", "Finish capture, recognition, or insertion before pasting the record take.");
        else if (_recordResult is not { } result)
            _session.Notify(NoticeKind.Warning, "No record take", "Complete a record take before using the paste shortcut.");
        else
        {
            _pushToTalk = false;
            _deliveryOperation = Task.FromResult(result.Outcome);
            _deliveryOperationId = result.OperationId;
            // Currency belongs to this press, not the old record operation. Voice results never replace the bundle.
            _deliveryCurrencyId = state.OperationId;
            _attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
            _deliveryTask = PasteRecordAsync(state.Settings);
        }
        return Task.CompletedTask;
    }

    /// <summary>Releases the paste chord so its admitted delivery may send input.</summary>
    public void PasteRecordReleased() => _pasteHeld = false;

    private async Task PasteRecordAsync(AppSettings settings)
    {
        var token = _attemptCancellation!.Token;
        Task<IShortcutTextTarget>? inspection = null;
        try
        {
            inspection = _inspector.InspectAsync(settings, token);
            _captureTask = inspection;
            await inspection;
            token.ThrowIfCancellationRequested();
            _delivery = CreateDelivery(inspection, settings, record: true, () => _pasteHeld);
            await DeliverAsync(_deliveryOperation!, _delivery);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            _session.Notify(NoticeKind.Warning, "Record paste unavailable", error.Message);
        }
        finally
        {
            if (ReferenceEquals(_captureTask, inspection)) _captureTask = null;
            if (_attemptCancellation is not null) ClearAttempt();
        }
    }

    private void SetOwner(ShortcutActivation? owner)
    {
        var wasRecord = HasRecordAttempt;
        _owner = owner;
        if (wasRecord != HasRecordAttempt) RecordAttemptChanged?.Invoke();
    }

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

    public async Task PressedAsync(DictationShortcut shortcut, ShortcutActivation activation = ShortcutActivation.Voice)
    {
        if (_owner is { } owner && owner != activation) return;
        _held = true;
        if (_closed || _closing) { return; }
        _coordinator.NotePressed();
        try
        {
            var task = HandlePressAsync(shortcut, activation);
            _pressTask = _pressTask is { IsCompleted: false } previous ? Task.WhenAll(previous, task) : task;
            await task;
        }
        catch (Exception error) { _session.ReportError(error); }
    }

    // Marks a pending push-to-talk capture stale so its inspection cannot start a late recording. Once admitted,
    // capture retains a short tail owned by this release and delivery operation.
    public async Task ReleasedAsync(ShortcutActivation activation = ShortcutActivation.Voice)
    {
        if (_owner is { } owner && owner != activation) return;
        _held = false;
        if (_closed || _closing) { return; }
        try
        {
            var state = _session.State;
            var release = _coordinator.NoteReleased();
            if (_pushToTalk && _deliveryOperation is not null)
            {
                var task = FinishReleasedDictationAsync(release, _deliveryOperation, state.OperationId);
                _releaseTailTask = _releaseTailTask is { IsCompleted: false } previous
                    ? Task.WhenAll(previous, task) : task;
                await task;
            }
        }
        catch (Exception error) { _session.ReportError(error); }
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
        ShortcutCaptureCoordinator.Release release, Task<SessionOutcome> operation, Guid? operationId)
    {
        var token = _cancellation.Token;
        try
        {
            if (!await _coordinator.WaitForReleaseTailAsync(release, token)) { return; }
            if (_closing || _closed || _held || !ReferenceEquals(_deliveryOperation, operation)) { return; }
            var state = _session.State;
            if (state.OperationId != operationId || !_pushToTalk) { return; }
            if (state.CanFinish) { await _session.FinishAsync(); }
            else if (state.Phase == DictationPhase.Preparing) { await _session.CancelAsync(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { _session.ReportError(error); }
    }

    // Voice recording and destination inspection start together. Record takes never inspect a destination;
    // only an explicit later paste press may choose one.
    private async Task HandlePressAsync(DictationShortcut shortcut, ShortcutActivation activation)
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
            if (!_pushToTalk) { await _session.FinishAsync(); }
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
        if (activation == ShortcutActivation.Record && !state.Settings.RecordChordEnabled) return;
        var recordOptions = activation == ShortcutActivation.Record ? _session.SnapshotRecordTake() : null;
        _discarded = false;
        SetOwner(activation);
        _pushToTalk = activation == ShortcutActivation.Record ? !state.Settings.RecordToggleMode : state.Settings.PushToTalk;
        var capture = _coordinator.TryBeginCapture(_pushToTalk, _deliveryTask is { IsCompleted: false });
        if (capture is null) { SetOwner(null); Record(ShortcutDiagnosticOutcome.AdmissionRejected); return; }
        Task<IShortcutTextTarget>? inspection = null;
        try
        {
            // Let the hotkey adapter report an already-released chord before admitting recording.
            await Task.Yield();
            if (token.IsCancellationRequested || _closing || _closed || _discarded) { Record(ShortcutDiagnosticOutcome.Cancelled); return; }
            if (!_coordinator.IsCurrent(capture)) { Record(ShortcutDiagnosticOutcome.Stale); return; }
            var current = _session.State;
            if (!current.CanStart || current.SelectedModel?.CanInsertDictation != true)
            { Record(ShortcutDiagnosticOutcome.AdmissionRejected); return; }
            if (!_coordinator.TryStartDelivery(capture)) { Record(ShortcutDiagnosticOutcome.Stale); return; }
            var shortcutTimestamp = generation == _diagnostics?.CurrentGeneration && generation is not null ? started : (long?)null;
            if (recordOptions is not null)
            {
                // A new take releases the factory's prior media lease. Never keep that old bundle pasteable.
                _recordResult = null;
                RecordResultChanged?.Invoke();
                _attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
                _anchorChanged(null);
                _deliveryOperation = _session.StartShortcutDictationAsync(shortcutTimestamp, recordTakeOptions: recordOptions);
                _deliveryOperationId = _session.State.OperationId;
                _deliveryCurrencyId = _deliveryOperationId;
                _deliveryTask = RetainRecordAsync(_deliveryOperation, _deliveryOperationId!.Value);
                Record(ShortcutDiagnosticOutcome.Admitted, _deliveryOperationId);
                return;
            }
            inspection = _inspector.InspectAsync(current.Settings, token);
            _captureTask = inspection;
            StartDelivery(inspection, shortcutTimestamp, shortcut, current.Settings);
            var operationId = _deliveryOperationId;
            var inspected = await inspection;
            if (token.IsCancellationRequested || _closing || _closed) { Record(ShortcutDiagnosticOutcome.Cancelled); return; }
            if (_deliveryOperationId == operationId && _session.State.IsLiveOperation) { _anchorChanged(inspected.Caret); }
            Record(ShortcutDiagnosticOutcome.Admitted, operationId);
        }
        catch (Exception error)
        {
            Record(ShortcutDiagnosticOutcome.AdmissionRejected, error: error);
            if (_deliveryOperation is null && _attemptCancellation is not null) ClearAttempt();
            throw;
        }
        finally
        {
            _coordinator.Abandon(capture);
            if (ReferenceEquals(_captureTask, inspection)) { _captureTask = null; }
            if (_deliveryOperation is null) SetOwner(null);
        }
    }

    private FinalTextDelivery CreateDelivery(Task<IShortcutTextTarget> target, AppSettings settings,
        bool record, Func<bool> typingDeferred)
    {
        var method = record ? TextInsertionMethod.Paste : settings.InsertionMethod;
        var pasteShortcut = record ? PasteShortcut.ControlV : settings.PasteShortcut;
        var diagnostics = _diagnostics;
        var diagnosticGeneration = diagnostics?.CurrentGeneration;
        return new FinalTextDelivery(
            insert: async (pending, token) =>
            {
                var inspected = await target.WaitAsync(token);
                return await inspected.InsertAsync(pending, IsDeliveryCurrent, token);
            },
            isCurrent: IsDeliveryCurrent,
            typingDeferred: typingDeferred,
            diagnostic: diagnosticGeneration is { } generation ? trace =>
            {
                if (_deliveryOperationId is { } id)
                    diagnostics!.Write(new DeliveryDiagnostic(id, DateTimeOffset.UtcNow, trace.Outcome, method,
                        method == TextInsertionMethod.Paste ? pasteShortcut : null,
                        trace.ResultToDeliveryMilliseconds, trace.DeferredMilliseconds, trace.ErrorHResult)
                    {
                        Reason = trace.Reason, ResultToSendMilliseconds = trace.ResultToSendMilliseconds,
                        VerificationMilliseconds = trace.VerificationMilliseconds
                    }, generation);
            } : null);
    }

    private void StartDelivery(Task<IShortcutTextTarget> target, long? shortcutTimestamp,
        DictationShortcut shortcut, AppSettings settings)
    {
        var delivery = CreateDelivery(target, settings, record: false, () => _held && shortcut.Modifiers != 0);
        _delivery = delivery;
        _attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
        _anchorChanged(null);
        try
        {
            var operation = _session.StartShortcutDictationAsync(shortcutTimestamp, target);
            _deliveryOperation = operation;
            _deliveryOperationId = _session.State.OperationId;
            _deliveryCurrencyId = _deliveryOperationId;
            _deliveryTask = DeliverAsync(operation, delivery);
        }
        catch
        {
            _delivery = null;
            _anchorChanged(null);
            _deliveryOperation = null;
            _deliveryOperationId = null;
            _attemptCancellation.Dispose();
            _attemptCancellation = null;
            throw;
        }
    }

    // Delivery must still own the session currency captured at admission before any target insertion.
    private bool IsDeliveryCurrent()
    {
        var operation = _deliveryOperation;
        if (operation is null || _closing || _closed || _discarded) { return false; }
        var state = _session.State;
        return state.OperationId == _deliveryCurrencyId &&
            (state.IsIdle || state.Phase == DictationPhase.RecoveryRequired && operation.IsCompletedSuccessfully && operation.Result.MediaResourcesRetained) &&
            operation.IsCompletedSuccessfully
            && operation.Result.Kind == SessionOutcomeKind.Completed;
    }

    private async Task RetainRecordAsync(Task<SessionOutcome> operation, Guid operationId)
    {
        try
        {
            var outcome = await operation;
            if (!_discarded && !_closing && !_closed && outcome.Kind == SessionOutcomeKind.Completed &&
                outcome.Result is { SpeechText: { } speech, Payload: { } payload } && !string.IsNullOrWhiteSpace(speech))
            {
                // Freeze the externally supplied frame list while retaining the complete recognition outcome.
                var retainedResult = outcome.Result with
                {
                    Payload = payload with { Frames = Array.AsReadOnly(payload.Frames.ToArray()) },
                    WordTimings = outcome.Result.WordTimings is { } timings
                        ? timings with { Words = Array.AsReadOnly(timings.Words.ToArray()) } : null
                };
                _recordResult = (operationId, outcome with { Result = retainedResult });
                RecordResultChanged?.Invoke();
            }
        }
        catch (Exception error) { _session.ReportError(error); }
        finally { ClearAttempt(); }
    }

    private void ClearAttempt()
    {
        _delivery = null;
        _deliveryOperation = null;
        _deliveryOperationId = null;
        _deliveryCurrencyId = null;
        _attemptCancellation?.Dispose();
        _attemptCancellation = null;
        SetOwner(null);
    }

    private async Task DeliverAsync(Task<SessionOutcome> operation, FinalTextDelivery delivery)
    {
        var token = _attemptCancellation!.Token;
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
        catch (Exception error) { _session.ReportError(error); }
        finally
        {
            if (ReferenceEquals(_delivery, delivery))
            {
                ClearAttempt();
            }
        }
    }
}
