using System.Threading.Channels;
using System.Diagnostics;

namespace Ansible.Core;

public enum MeetingSessionPhase { Idle, Starting, Recording, Paused, Finishing, Completed, Failed, Refining }
public enum MeetingTextCleanupState { None, Running, Completed, Cancelled, Failed }

public sealed record MeetingSessionSnapshot(long Version, MeetingSessionPhase Phase, MeetingDocument? Document,
    double CapturedSeconds, double PendingSeconds, string? Error)
{
    /// <summary>Why an unconfigured meeting skipped an installed pair, or null when the first candidate was used.</summary>
    public string? SelectionNotice { get; init; }
    public MeetingCaptureHealth? CaptureHealth { get; init; }
    public MeetingTextCleanupState TextCleanup { get; init; }
    public string? TextCleanupError { get; init; }
}

public sealed class MeetingSession : IAsyncDisposable
{
    private const int PacketBytes = 3200;
    private const long MaximumSamples = 16000L * 120 * 60;
    private readonly object _gate = new();
    private readonly object _updates = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly MeetingPersistence _persistence;
    private readonly SemaphoreSlim _resources = new(1, 1);
    private readonly SemaphoreSlim _disposal = new(1, 1);
    private readonly Func<IMeetingRecognitionEngine> _engineFactory;
    private readonly IMeetingCaptureFactory _captureFactory;
    private readonly MeetingStore _store;
    private readonly LocalDiagnosticLog? _diagnostics;
    private readonly IMeetingTextCleanup? _textCleanup;
    private readonly Func<MeetingCleanupSettings>? _getCleanupSettings;
    private MeetingCleanupSettings _cleanupSettings = new();
    private CancellationTokenSource? _textCleanupCancellation;
    private Guid _diagnosticOperationId;
    private long? _diagnosticGeneration;
    private long _diagnosticStartedAt;
    private int _diagnosticTerminal;
    private volatile MeetingDiagnosticStage _diagnosticStage;
    private MeetingDiagnosticFailure? _diagnosticFailure;
    private long _recognitionUpdates;
    private long _processedSamples = -1;
    private double _nextPipelineLogSeconds;
    private readonly Func<RecognitionOptions> _getOptions;
    private readonly Func<string?>? _getModelId;
    private readonly Func<Task> _reserve;
    private readonly Func<Task> _release;
    private readonly Func<IMeetingSpeakerObserver>? _observerFactory;
    private readonly Func<IMeetingWordAligner>? _alignerFactory;
    private MeetingSpeakerEvidence? _evidence;
    private MeetingSessionSnapshot _state = new(0, MeetingSessionPhase.Idle, null, 0, 0, null);
    private MeetingCaptureRequest? _request;
    private IMeetingRecognitionEngine? _engine;
    private IMeetingCapture? _capture;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _preparation;
    private Channel<byte[]>? _audio;
    private PausableReader? _reader;
    private MeetingTranscript _transcript = new();
    private TranscriptUpdate? _pendingUpdate;
    private Task _pump = Task.CompletedTask;
    private Task _recognition = Task.CompletedTask;
    private long _capturedSamples;
    private bool _reserved;
    private bool _disposed;
    private int _disposing;
    private bool _discarding;

    private sealed record MeetingDiagnosticFailure(MeetingDiagnosticStage Stage, int ErrorHResult);

    public MeetingSession(Func<IMeetingRecognitionEngine> engineFactory, IMeetingCaptureFactory captureFactory,
        MeetingStore store, Func<RecognitionOptions> getOptions, Func<Task>? reserve = null, Func<Task>? release = null,
        Func<IMeetingSpeakerObserver>? observerFactory = null, Func<IMeetingWordAligner>? alignerFactory = null,
        Func<string?>? getModelId = null, Func<MeetingDocument, Task>? writeMeeting = null,
        LocalDiagnosticLog? diagnostics = null, IMeetingTextCleanup? textCleanup = null,
        Func<MeetingCleanupSettings>? getCleanupSettings = null)
    {
        _engineFactory = engineFactory;
        _captureFactory = captureFactory;
        _store = store;
        _diagnostics = diagnostics;
        _textCleanup = textCleanup;
        _getCleanupSettings = getCleanupSettings;
        _getOptions = getOptions;
        _getModelId = getModelId;
        _reserve = reserve ?? (() => Task.CompletedTask);
        _release = release ?? (() => Task.CompletedTask);
        _observerFactory = observerFactory;
        _alignerFactory = alignerFactory;
        _persistence = new(writeMeeting ?? (document => _store.SaveAsync(document)), StorageFailed, diagnostics);
    }

    public event Action<MeetingSessionSnapshot>? StateChanged;
    public MeetingSessionSnapshot State { get { lock (_gate) return _state; } }
    public MeetingDocument? Document => State.Document;
    public bool IsActive { get { lock (_gate) return _reserved; } }

    public async Task StartAsync(MeetingCaptureRequest request)
    {
        try { request.Validate(); }
        catch (Exception error)
        {
            if (_diagnostics?.CurrentGeneration is { } generation)
            {
                var id = Guid.NewGuid();
                var started = Stopwatch.GetTimestamp();
                _diagnostics.Write(new MeetingDiagnostic(id, DateTimeOffset.UtcNow, MeetingDiagnosticKind.Starting,
                    MeetingDiagnosticStage.Prepare), generation);
                _diagnostics.Write(new MeetingDiagnostic(id, DateTimeOffset.UtcNow, MeetingDiagnosticKind.Terminal,
                    MeetingDiagnosticStage.Prepare, MeetingDiagnosticOutcome.Failed,
                    DurationMilliseconds: Stopwatch.GetElapsedTime(started).TotalMilliseconds, ErrorHResult: error.HResult),
                    generation);
            }
            throw;
        }
        await _commands.WaitAsync();
        CancellationTokenSource? preparation = null;
        var admitted = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed || Volatile.Read(ref _disposing) != 0, this);
            if (IsActive) throw new InvalidOperationException("End the current meeting before starting another.");
            await _recognition;
            _diagnosticGeneration = _diagnostics?.CurrentGeneration;
            _diagnosticOperationId = _diagnosticGeneration is null ? Guid.Empty : Guid.NewGuid();
            _diagnosticStartedAt = _diagnosticGeneration is null ? 0 : Stopwatch.GetTimestamp();
            lock (_gate)
            {
                _diagnosticTerminal = 0;
                _diagnosticFailure = null;
            }
            _diagnosticStage = MeetingDiagnosticStage.Prepare;
            admitted = true;
            LogMeeting(MeetingDiagnosticKind.Starting);
            await SaveAsync();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposing) != 0, this);
                _preparation = preparation = new();
            }
            var options = _getOptions() ?? throw new InvalidOperationException("Meeting recognition settings are unavailable.");
            _cleanupSettings = _getCleanupSettings?.Invoke() ?? new();
            _cleanupSettings.Validate();
            if (_diagnosticGeneration is { } generation)
                options = options with { Diagnostics = new(_diagnostics!, generation, _diagnosticOperationId, options.Backend) };
            var selectedId = _getModelId?.Invoke();
            await _reserve();
            lock (_gate)
            {
                _reserved = true;
                _state = new(_state.Version + 1, MeetingSessionPhase.Starting,
                    new MeetingDocument { Status = MeetingStatus.Starting }, 0, 0, null);
                _pendingUpdate = null;
            }
            Publish();
            _request = request;
            _capturedSamples = 0;
            _recognitionUpdates = 0;
            _processedSamples = -1;
            _transcript = new();
            _discarding = false;
            _recognition = Task.CompletedTask;
            _pump = Task.CompletedTask;
            _cancellation?.Dispose();
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(preparation.Token);
            var evidence = _evidence = new MeetingSpeakerEvidence(_observerFactory, _alignerFactory, request.ProcessId,
                (kind, available, error) => LogMeeting(kind, error: error, available: available), _cancellation.Token);
            _audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(300)
            { SingleWriter = true, SingleReader = true, AllowSynchronousContinuations = false });
            _reader = new PausableReader(_audio.Reader, evidence.Append);
            await SaveAsync();
            _engine = _engineFactory();
            var models = await _engine.ConnectAsync(_cancellation.Token);
            _diagnosticStage = MeetingDiagnosticStage.SelectModel;
            var candidates = ModelSelectionPolicy.MeetingCandidates(models, selectedId);
            var requested = options;
            var skipped = new List<string>();
            AudioModel? model = null;
            for (var index = 0; index < candidates.Count; index++)
            {
                model = candidates[index];
                LogMeeting(MeetingDiagnosticKind.Candidate, model: model, explicitModel: selectedId is not null);
                options = requested with { CustomDictionary = ModelSelectionPolicy.EffectiveDictionary(model, requested.CustomDictionary) };
                try
                {
                    await _engine.PrepareMeetingAsync(model, options, _cancellation.Token);
                    break;
                }
                catch (MeetingRealTimeException error) when (index < candidates.Count - 1)
                {
                    LogMeeting(MeetingDiagnosticKind.CandidateRejected, model: model,
                        explicitModel: selectedId is not null, error: error);
                    // Unconfigured meetings continue down the documented order; an explicit choice is its only candidate.
                    skipped.Add(error.Message);
                }
                catch (MeetingRealTimeException error) when (skipped.Count > 0)
                {
                    LogMeeting(MeetingDiagnosticKind.CandidateRejected, model: model,
                        explicitModel: selectedId is not null, error: error);
                    throw new MeetingRealTimeException(error.ModelId, error.Backend, error.RealTimeFactor, error.Maximum, string.Join(" ", skipped));
                }
                catch (MeetingRealTimeException error)
                {
                    LogMeeting(MeetingDiagnosticKind.CandidateRejected, model: model,
                        explicitModel: selectedId is not null, error: error);
                    throw;
                }
            }
            if (skipped.Count > 0)
            {
                lock (_gate) _state = _state with { Version = _state.Version + 1,
                    SelectionNotice = $"{string.Join(" ", skipped)} Using {model!.Id} instead." };
                Publish();
            }
            _cancellation.Token.ThrowIfCancellationRequested();
            _diagnosticStage = MeetingDiagnosticStage.Capture;
            _capture = await _captureFactory.StartAsync(request, _cancellation.Token);
            _cancellation.Token.ThrowIfCancellationRequested();
            await AttachCaptureAsync(_capture);
            _pump = PumpAsync(_capture, _cancellation.Token);
            _diagnosticStage = MeetingDiagnosticStage.Recognize;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _recognition = Task.Run(() => RecognizeAsync(model!, options, ready));
            if (await Task.WhenAny(ready.Task, _recognition) == _recognition)
            {
                await _recognition;
                throw new InvalidOperationException(State.Error ?? "Meeting recognition stopped during startup.");
            }
            await ready.Task;
            if (State.Phase == MeetingSessionPhase.Failed || !IsActive)
                throw new InvalidOperationException(State.Error ?? "Meeting recognition stopped during startup.");
            SetPhase(MeetingSessionPhase.Recording, MeetingStatus.Recording);
            await SaveAsync();
        }
        catch (Exception error)
        {
            var failureStage = _diagnosticStage;
            if (admitted && error is not OperationCanceledException)
                RememberDiagnosticFailure(failureStage, error);
            try
            {
                if (error is MeetingCaptureOwnershipException ownership) _capture = ownership.Capture;
                if (admitted && IsActive)
                {
                    FailMeeting(failureStage, error, recordDiagnostic: error is not OperationCanceledException);
                    _cancellation?.Cancel();
                    _reader?.Resume();
                    _audio?.Writer.TryComplete(error);
                    await _recognition;
                    try { await ReleaseResourcesAsync(); }
                    finally { await SaveAsync(); }
                }
            }
            finally
            {
                if (admitted)
                    LogMeetingTerminal(error is OperationCanceledException ? MeetingDiagnosticOutcome.Cancelled :
                        MeetingDiagnosticOutcome.Failed, error, failureStage);
            }
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_preparation, preparation)) _preparation = null;
                preparation?.Dispose();
            }
            _commands.Release();
        }
    }

    private void CancelPreparation()
    {
        lock (_gate) _preparation?.Cancel();
    }

    public async Task PauseAsync()
    {
        await _commands.WaitAsync();
        try
        {
            RequirePhase(MeetingSessionPhase.Recording);
            _reader!.Pause();
            SetPhase(MeetingSessionPhase.Paused, MeetingStatus.Paused);
            await StopCaptureAsync();
            await SaveAsync();
            LogMeeting(MeetingDiagnosticKind.Paused);
        }
        catch (Exception error)
        {
            FailMeeting(MeetingDiagnosticStage.Cleanup, error);
            _cancellation?.Cancel();
            _reader?.Resume();
            _audio?.Writer.TryComplete(error);
            await _recognition;
            throw;
        }
        finally { _commands.Release(); }
    }

    public async Task ResumeAsync()
    {
        await _commands.WaitAsync();
        IMeetingCapture? started = null;
        try
        {
            RequirePhase(MeetingSessionPhase.Paused);
            if (_capture is not null) throw new InvalidOperationException("Release the previous capture before resuming the meeting.");
            started = _capture = await _captureFactory.StartAsync(_request!, _cancellation!.Token);
            await AttachCaptureAsync(started);
            SetPhase(MeetingSessionPhase.Recording, MeetingStatus.Recording);
            await Task.Run(FlushPendingUpdate);
            _reader!.Resume();
            _pump = PumpAsync(started, _cancellation.Token);
            await SaveAsync();
            LogMeeting(MeetingDiagnosticKind.Resumed);
        }
        catch (MeetingCaptureOwnershipException error)
        {
            _capture = error.Capture;
            await FailResumeAsync(error);
            throw;
        }
        // A factory failure before a capture exists leaves the session paused and retryable. Any later failure
        // leaves a live capture, so it must fail the meeting and release the capture and reservation.
        catch (Exception error) when (started is not null)
        {
            await FailResumeAsync(error);
            throw;
        }
        finally { _commands.Release(); }
    }

    private async Task FailResumeAsync(Exception error)
    {
        FailMeeting(MeetingDiagnosticStage.Capture, error);
        _cancellation!.Cancel();
        _reader!.Resume();
        _audio!.Writer.TryComplete(error);
        await _recognition;
        try { await ReleaseResourcesAsync(); }
        finally { await SaveAsync(); }
    }

    public async Task EndAsync() => await FinishAsync(false);
    public async Task DiscardAsync()
    {
        CancelPreparation();
        await FinishAsync(true);
    }

    private async Task FinishAsync(bool discard)
    {
        await _commands.WaitAsync();
        try
        {
            if (!IsActive)
            {
                await _recognition;
                await SaveAsync();
                return;
            }
            SetPhase(MeetingSessionPhase.Finishing, MeetingStatus.Finishing);
            if (discard)
            {
                lock (_gate) _discarding = true;
                _cancellation!.Cancel();
            }
            _reader!.Resume();
            await StopCaptureAsync();
            if (!discard) await Task.Run(FlushPendingUpdate);
            else { lock (_gate) _pendingUpdate = null; }
            _audio!.Writer.TryComplete();
            await _recognition;
            if (!discard && State.Error is null)
            {
                SetPhase(MeetingSessionPhase.Refining, MeetingStatus.Finishing);
                lock (_gate) _state = _state with { Document = _evidence!.NameSpeakers(_state.Document!) };
                Publish();
            }
            await ReleaseResourcesAsync();
            if (State.Error is null)
                SetPhase(MeetingSessionPhase.Completed, discard ? MeetingStatus.Interrupted : MeetingStatus.Completed);
            else SetPhase(MeetingSessionPhase.Failed, MeetingStatus.Failed);
            await SaveAsync();
            LogMeetingTerminal(State.Error is null ? discard ? MeetingDiagnosticOutcome.Discarded :
                MeetingDiagnosticOutcome.Completed : MeetingDiagnosticOutcome.Failed);
            if (!discard && State.Error is null)
                await CleanCompletedMeetingAsync();
        }
        catch (Exception error)
        {
            FailMeeting(MeetingDiagnosticStage.Cleanup, error);
            _cancellation?.Cancel();
            _reader?.Resume();
            _audio?.Writer.TryComplete(error);
            try
            {
                await _recognition;
                await SaveAsync();
            }
            finally { LogMeetingTerminal(MeetingDiagnosticOutcome.Failed, error); }
            throw;
        }
        finally { _commands.Release(); }
    }

    private async Task RecognizeAsync(AudioModel model, RecognitionOptions options, TaskCompletionSource ready)
    {
        try
        {
            await _engine!.StreamMeetingAsync(model, _reader!, options, new InlineProgress(ReceiveUpdate),
                _cancellation!.Token, () => ready.TrySetResult());
            if (State.Phase is MeetingSessionPhase.Starting or MeetingSessionPhase.Recording or MeetingSessionPhase.Paused)
                throw new InvalidOperationException("The recognition stream ended before the meeting was ended.");
        }
        catch (OperationCanceledException) when (_cancellation!.IsCancellationRequested) { }
        catch (Exception error)
        {
            FailMeeting(MeetingDiagnosticStage.Recognize, error, publish: false);
            _cancellation!.Cancel();
            _reader!.Resume();
            _audio!.Writer.TryComplete(error);
            Publish();
        }
        finally
        {
            LogPipeline();
            if (State.Phase == MeetingSessionPhase.Failed)
            {
                try { await ReleaseResourcesAsync(); await SaveAsync(); }
                catch (Exception error) { SetPhase(MeetingSessionPhase.Failed, MeetingStatus.Failed, $"{State.Error} {error.Message}"); }
                LogMeetingTerminal(MeetingDiagnosticOutcome.Failed);
            }
        }
    }

    private async Task PumpAsync(IAudioCapture capture, CancellationToken token)
    {
        var buffer = new byte[PacketBytes];
        var used = 0;
        try
        {
            await foreach (var packet in capture.Audio.ReadAllAsync(token))
            {
                if (packet.Length == 0 || packet.Length % 2 != 0)
                    throw new InvalidDataException("Meeting capture returned incomplete PCM16 samples.");
                var index = 0;
                while (index < packet.Length)
                {
                    var count = Math.Min(PacketBytes - used, packet.Length - index);
                    packet.AsSpan(index, count).CopyTo(buffer.AsSpan(used));
                    used += count;
                    index += count;
                    if (used == PacketBytes)
                    {
                        QueueAudio(buffer);
                        buffer = new byte[PacketBytes];
                        used = 0;
                    }
                }
            }
            if (used > 0) QueueAudio(buffer.AsSpan(0, used).ToArray());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            FailMeeting(MeetingDiagnosticStage.Capture, error, publish: false);
            _audio!.Writer.TryComplete(error);
            Publish();
            _cancellation!.Cancel();
            _reader!.Resume();
        }
    }

    private void QueueAudio(byte[] packet)
    {
        var captured = Interlocked.Add(ref _capturedSamples, packet.Length / 2);
        if (captured > MaximumSamples) throw new InvalidOperationException("The meeting reached its two-hour capture limit. The existing transcript is preserved.");
        if (!_audio!.Writer.TryWrite(packet))
            throw new InvalidOperationException("Transcription fell more than 30 seconds behind capture. Capture stopped to keep memory bounded; the existing transcript is preserved.");
        lock (_gate)
            _state = _state with { Version = _state.Version + 1, CapturedSeconds = captured / 16000.0,
                PendingSeconds = Math.Max(0, (captured - _reader!.ReadSamples) / 16000.0) };
        Publish();
    }

    private void ReceiveUpdate(TranscriptUpdate update)
    {
        lock (_updates)
        {
            lock (_gate)
            {
                if (_discarding) return;
                if (_state.Phase == MeetingSessionPhase.Paused) { _pendingUpdate = update; return; }
                _pendingUpdate = null;
            }
            ApplyUpdate(update);
        }
        Publish();
        QueueSave();
    }

    private void ApplyUpdate(TranscriptUpdate update)
    {
        var processed = update.ProcessedSamples ?? _reader!.ReadSamples;
        if (processed < 0 || processed > _reader!.ReadSamples)
            throw new InvalidDataException("Recognition returned a timestamp outside the consumed audio.");
        Interlocked.Increment(ref _recognitionUpdates);
        Interlocked.Exchange(ref _processedSamples, update.ProcessedSamples ?? -1);
        var previous = Document!.Turns;
        var evidence = _evidence!;
        MeetingTurn[] turns;
        if (update.SpeakerTurns is { } timed)
        {
            turns = ValidateTimedTurns(timed, processed, update.IsFinal);
            evidence.RefineTimedTurns(turns, previous);
        }
        else
        {
            turns = _transcript.Update(update.Text, processed / 16000.0, update.IsFinal).ToArray();
            evidence.RefineChunkTurns(turns, previous, processed);
        }
        lock (_gate)
        {
            if (_discarding) return;
            if (_state.Phase == MeetingSessionPhase.Paused) { _pendingUpdate = update; return; }
            var document = _state.Document! with { Turns = turns, UpdatedAt = DateTimeOffset.UtcNow };
            _state = _state with { Version = _state.Version + 1, Document = evidence.NameSpeakers(document),
                PendingSeconds = Math.Max(0, (_capturedSamples - _reader!.ReadSamples) / 16000.0) };
        }
    }

    private static MeetingTurn[] ValidateTimedTurns(IReadOnlyList<MeetingTurn> turns, long processed, bool final)
    {
        if (turns.Count > 262144) throw new InvalidDataException("Meeting recognition returned too many timed turns.");
        var result = new MeetingTurn[turns.Count];
        double previousStart = 0;
        for (var index = 0; index < result.Length; index++)
        {
            var turn = turns[index];
            if (turn is null || string.IsNullOrWhiteSpace(turn.SpeakerId) || string.IsNullOrWhiteSpace(turn.Text) ||
                !double.IsFinite(turn.StartSeconds) || !double.IsFinite(turn.EndSeconds) ||
                turn.StartSeconds < previousStart || turn.EndSeconds < turn.StartSeconds ||
                turn.EndSeconds > processed / 16000.0 || !Enum.IsDefined(turn.TimingQuality) ||
                !Enum.IsDefined(turn.Source) || final && turn.IsProvisional)
                throw new InvalidDataException("Meeting recognition returned invalid timed speaker turns.");
            previousStart = turn.StartSeconds;
            result[index] = turn;
        }
        return result;
    }

    private void FlushPendingUpdate()
    {
        lock (_updates)
        {
            TranscriptUpdate update;
            lock (_gate)
            {
                if (_pendingUpdate is not { } pending) return;
                update = pending;
                _pendingUpdate = null;
            }
            ApplyUpdate(update);
        }
        Publish();
    }

    public async Task RenameAsync(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title.Trim().Length > 500) throw new ArgumentException("Meeting titles can contain at most 500 characters.");
        await _commands.WaitAsync();
        try
        {
            lock (_gate) _state = _state with { Version = _state.Version + 1,
                Document = (_state.Document ?? throw new InvalidOperationException("Start a meeting first.")) with { Title = title.Trim(), UpdatedAt = DateTimeOffset.UtcNow } };
            Publish();
            await SaveAsync();
        }
        finally { _commands.Release(); }
    }

    public async Task ApplyCleanupAsync(IReadOnlyList<string> texts)
    {
        await _commands.WaitAsync();
        try
        {
            if (IsActive || Document?.Status != MeetingStatus.Completed || Document.Turns.Any(turn => turn.IsProvisional))
                throw new InvalidOperationException("End the meeting before cleaning its transcript.");
            SetTextCleanupState(MeetingTextCleanupState.None);
            await ApplyCleanupCoreAsync(texts);
        }
        finally { _commands.Release(); }
    }

    private async Task ApplyCleanupCoreAsync(IReadOnlyList<string> texts)
    {
        MeetingDocument updated;
        lock (_gate)
        {
            var document = _state.Document ?? throw new InvalidOperationException("No meeting transcript is open.");
            if (texts.Count != document.Turns.Count || texts.Any(text => string.IsNullOrWhiteSpace(text) || text.Contains('\0')))
                throw new ArgumentException("Cleanup must retain every existing transcript turn.");
            var turns = document.Turns.Select((turn, index) => turn with
                { OriginalText = turn.OriginalText ?? turn.Text, Text = texts[index] }).ToArray();
            updated = document with { Turns = turns, UpdatedAt = DateTimeOffset.UtcNow };
        }
        long revision;
        lock (_gate)
        {
            revision = _state.Version + 1;
            _state = _state with { Version = revision };
            _persistence.Queue(revision, updated);
        }
        await FlushMeetingAsync(revision);
        lock (_gate) _state = _state with { Version = _state.Version + 1, Document = updated };
        Publish();
    }

    public void CancelTextCleanup()
    {
        lock (_gate) _textCleanupCancellation?.Cancel();
    }

    private async Task CleanCompletedMeetingAsync()
    {
        if (!_cleanupSettings.Automatic || Document is not { Status: MeetingStatus.Completed, Turns.Count: > 0 } document ||
            document.Turns.Any(turn => turn.IsProvisional)) return;
        using var cancellation = new CancellationTokenSource();
        // DisposeAsync sets _disposing and then cancels under _gate. Checking and publishing under the same
        // lock means disposal either finds this source and cancels it, or this method sees disposal and
        // never calls the provider.
        lock (_gate)
        {
            if (Volatile.Read(ref _disposing) != 0) return;
            _textCleanupCancellation = cancellation;
        }
        SetTextCleanupState(MeetingTextCleanupState.Running);
        try
        {
            var provider = _textCleanup ?? throw new InvalidOperationException("Transcript cleanup is not configured. Open Settings.");
            var texts = await provider.CleanAsync(document, _cleanupSettings.Options, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            await ApplyCleanupCoreAsync(texts);
            SetTextCleanupState(MeetingTextCleanupState.Completed);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetTextCleanupState(MeetingTextCleanupState.Cancelled);
        }
        catch (Exception error)
        {
            SetTextCleanupState(MeetingTextCleanupState.Failed, error.Message);
        }
        finally { lock (_gate) _textCleanupCancellation = null; }
    }

    private void SetTextCleanupState(MeetingTextCleanupState state, string? error = null)
    {
        lock (_gate) _state = _state with { Version = _state.Version + 1, TextCleanup = state, TextCleanupError = error };
        Publish();
    }

    private async Task AttachCaptureAsync(IMeetingCapture capture)
    {
        _nextPipelineLogSeconds = 0;
        capture.HealthChanged += CaptureHealthChanged;
        if (capture.Health is { } health) CaptureHealthChanged(health);
        await _evidence!.AttachAsync(capture, Interlocked.Read(ref _capturedSamples));
    }

    private void CaptureHealthChanged(MeetingCaptureHealth health)
    {
        lock (_gate) _state = _state with { Version = _state.Version + 1, CaptureHealth = health };
        Publish();
        if (health.ElapsedSeconds < _nextPipelineLogSeconds) return;
        _nextPipelineLogSeconds = health.ElapsedSeconds + 5;
        LogPipeline();
    }

    private void LogPipeline()
    {
        if (_diagnostics?.Enabled != true) return;
        var state = State;
        if (state.CaptureHealth is not { } health) return;
        var processed = Interlocked.Read(ref _processedSamples);
        _diagnostics?.Write(new MeetingCaptureDiagnostic(health.CaptureId, DateTimeOffset.UtcNow, MeetingCaptureDiagnosticKind.Pipeline,
            MeetingCaptureStage.Mix, health, Pipeline: new(state.Phase, Interlocked.Read(ref _capturedSamples) / 16000.0,
                (_reader?.ReadSamples ?? 0) / 16000.0, Interlocked.Read(ref _recognitionUpdates),
                processed < 0 ? null : processed / 16000.0))
        {
            MeetingOperationId = _diagnosticGeneration is not null &&
                _diagnosticGeneration == _diagnostics?.CurrentGeneration ? _diagnosticOperationId : null
        });
    }

    private void LogMeeting(MeetingDiagnosticKind kind, MeetingDiagnosticOutcome? outcome = null,
        AudioModel? model = null, bool? explicitModel = null, Exception? error = null, bool? available = null,
        MeetingDiagnosticStage? stage = null, int? errorHResult = null)
    {
        if (_diagnosticGeneration is not { } generation) return;
        var family = model?.Family switch
        {
            "nemotron_asr" => MeetingModelFamily.Nemotron,
            "kroko_asr" => MeetingModelFamily.Kroko,
            "vibevoice_asr_streaming" => MeetingModelFamily.VibeVoiceStreaming,
            _ => (MeetingModelFamily?)null
        };
        var captureId = _capture?.Health?.CaptureId;
        _diagnostics!.Write(new MeetingDiagnostic(_diagnosticOperationId, DateTimeOffset.UtcNow, kind,
            stage ?? _diagnosticStage, outcome, family, explicitModel, captureId,
            kind == MeetingDiagnosticKind.Terminal ? Stopwatch.GetElapsedTime(_diagnosticStartedAt).TotalMilliseconds : null,
            errorHResult ?? error?.HResult)
        {
            Available = available,
            ModelId = model is null ? null : NativeDiagnosticContext.SafeModelId(model.Id),
            RetainedResources = kind == MeetingDiagnosticKind.Terminal
                ? _capture?.IsReleased == false || _engine is not null || _evidence?.HasResources == true ||
                    IsActive : null,
            RealTimeFactor = error is MeetingRealTimeException realtime && double.IsFinite(realtime.RealTimeFactor)
                ? realtime.RealTimeFactor : null,
            MaximumRealTimeFactor = error is MeetingRealTimeException limit && double.IsFinite(limit.Maximum)
                ? limit.Maximum : null,
            QualifiedBackend = (error as MeetingRealTimeException)?.Backend
        }, generation);
    }

    private void LogMeetingTerminal(MeetingDiagnosticOutcome outcome, Exception? error = null,
        MeetingDiagnosticStage? stage = null)
    {
        MeetingDiagnosticFailure? failure;
        lock (_gate)
        {
            if (_diagnosticTerminal != 0) return;
            _diagnosticTerminal = 1;
            failure = _diagnosticFailure;
        }
        LogMeeting(MeetingDiagnosticKind.Terminal, failure is null ? outcome : MeetingDiagnosticOutcome.Failed,
            error: error, stage: failure?.Stage ?? stage, errorHResult: failure?.ErrorHResult);
    }

    public async Task RenameSpeakerAsync(string id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await _commands.WaitAsync();
        try
        {
            lock (_gate)
            {
                var document = _state.Document ?? throw new InvalidOperationException("Start a meeting first.");
                if (!document.Turns.Any(turn => turn.SpeakerId == id)) throw new ArgumentException("This speaker does not appear in the meeting.");
                var names = document.SpeakerNames.ToDictionary(pair => pair.Key, pair => pair.Value);
                names[id] = name.Trim();
                _state = _state with { Version = _state.Version + 1, Document = document with { SpeakerNames = names, UpdatedAt = DateTimeOffset.UtcNow } };
            }
            Publish();
            await SaveAsync();
        }
        finally { _commands.Release(); }
    }

    private long QueueSave()
    {
        lock (_gate)
        {
            if (_state.Document is { } document) _persistence.Queue(_state.Version, document);
            return _state.Version;
        }
    }

    private Task SaveAsync() => Document is null ? Task.CompletedTask : FlushMeetingAsync(QueueSave());

    private async Task FlushMeetingAsync(long revision)
    {
        var generation = _diagnostics?.CurrentGeneration;
        var started = generation is null ? 0 : Stopwatch.GetTimestamp();
        Exception? failure = null;
        try { await _persistence.FlushAsync(revision); }
        catch (Exception error)
        {
            failure = error;
            RememberDiagnosticFailure(MeetingDiagnosticStage.Storage, error);
            throw;
        }
        finally
        {
            if (generation is { } value)
                _diagnostics!.Write(new StorageDiagnostic(DateTimeOffset.UtcNow, StorageDiagnosticCategory.Meeting,
                    StorageDiagnosticStage.Flush, failure is null ? DiagnosticOutcome.Completed : DiagnosticOutcome.Failed,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds, failure?.HResult), value);
        }
    }

    private void StorageFailed(Exception error)
    {
        // Inactive edits report failure to their awaiting caller and retain their committed transcript.
        if (!IsActive) return;
        _diagnosticStage = MeetingDiagnosticStage.Storage;
        FailMeeting(MeetingDiagnosticStage.Storage, error, $"Meeting transcript save failed. {error.Message}");
        _cancellation?.Cancel();
        _reader?.Resume();
        _audio?.Writer.TryComplete(error);
    }

    private async Task StopCaptureAsync()
    {
        await _resources.WaitAsync();
        try { await StopCaptureCoreAsync(); }
        finally { _resources.Release(); }
    }

    private async Task StopCaptureCoreAsync()
    {
        var capture = _capture;
        if (capture is null) return;
        await _evidence!.DetachAsync(async () =>
        {
            await capture.StopAsync();
            await _pump;
        });
        LogPipeline();
        capture.HealthChanged -= CaptureHealthChanged;
        await capture.DisposeAsync();
        if (!capture.IsReleased) throw new InvalidOperationException("Meeting audio resources remain owned. New capture is blocked.");
        _capture = null;
    }

    private async Task ReleaseResourcesAsync()
    {
        await _resources.WaitAsync();
        try
        {
            await StopCaptureCoreAsync();
            if (_engine is { } engine) { await Task.Run(async () => await engine.DisposeAsync()); _engine = null; }
            if (_evidence is { } evidence) await evidence.DisposeAsync();
            if (IsActive)
            {
                await _release();
                lock (_gate)
                {
                    _reserved = false;
                    _state = _state with { Version = _state.Version + 1 };
                }
                Publish();
            }
        }
        finally { _resources.Release(); }
    }

    private void RequirePhase(MeetingSessionPhase phase)
    {
        if (State.Phase != phase || !IsActive) throw new InvalidOperationException($"The meeting must be {phase.ToString().ToLowerInvariant()} for this action.");
    }

    private void RememberDiagnosticFailure(MeetingDiagnosticStage stage, Exception error)
    {
        lock (_gate) RememberDiagnosticFailureLocked(stage, error);
    }

    private void RememberDiagnosticFailureLocked(MeetingDiagnosticStage stage, Exception error)
    {
        if (_diagnosticGeneration is not null && _diagnosticTerminal == 0 && _diagnosticFailure is null)
            _diagnosticFailure = new(stage, error.HResult);
    }

    private void FailMeeting(MeetingDiagnosticStage stage, Exception error, string? message = null,
        bool recordDiagnostic = true, bool publish = true)
    {
        lock (_gate)
        {
            if (recordDiagnostic) RememberDiagnosticFailureLocked(stage, error);
            _state = _state with
            {
                Version = _state.Version + 1,
                Phase = MeetingSessionPhase.Failed,
                Error = _state.Phase == MeetingSessionPhase.Failed && _state.Error is not null
                    ? _state.Error : message ?? error.Message,
                Document = _state.Document is { } document
                    ? document with { Status = MeetingStatus.Failed, UpdatedAt = DateTimeOffset.UtcNow } : null
            };
        }
        if (publish) Publish();
    }

    private void SetPhase(MeetingSessionPhase phase, MeetingStatus status, string? error = null)
    {
        lock (_gate) _state = _state with { Version = _state.Version + 1, Phase = phase, Error = error ?? _state.Error,
            Document = _state.Document is { } document ? document with { Status = status, UpdatedAt = DateTimeOffset.UtcNow } : null };
        Publish();
    }

    private void Publish() => StateChanged?.Invoke(State);

    // Concurrent callers queue behind one teardown. A failed teardown leaves _disposed false, so the next caller retries it.
    public async ValueTask DisposeAsync()
    {
        await _disposal.WaitAsync();
        try { await DisposeCoreAsync(); }
        finally { _disposal.Release(); }
    }

    private async Task DisposeCoreAsync()
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _disposing, 1);
        CancelTextCleanup();
        CancelPreparation();
        await EndAsync();
        await _recognition;
        await SaveAsync();
        await _persistence.DisposeAsync();
        _disposed = true;
        _cancellation?.Dispose();
    }

    private sealed class InlineProgress(Action<TranscriptUpdate> report) : IProgress<TranscriptUpdate>
    {
        public void Report(TranscriptUpdate value) => report(value);
    }

    private sealed class PausableReader(ChannelReader<byte[]> reader, Action<byte[]> consumed) : ChannelReader<byte[]>
    {
        private readonly object _gate = new();
        private TaskCompletionSource? _resume;
        private long _readSamples;
        public long ReadSamples => Interlocked.Read(ref _readSamples);
        public override Task Completion => reader.Completion;
        public void Pause() { lock (_gate) _resume ??= new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public void Resume() { lock (_gate) { _resume?.TrySetResult(); _resume = null; } }
        public override bool TryRead(out byte[] item)
        {
            lock (_gate)
            {
                if (_resume is null && reader.TryRead(out item!))
                {
                    Interlocked.Add(ref _readSamples, item.Length / 2);
                    consumed(item);
                    return true;
                }
                item = null!;
                return false;
            }
        }
        public override async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                Task? resume;
                lock (_gate) resume = _resume?.Task;
                if (resume is not null) { await resume.WaitAsync(cancellationToken); continue; }
                var available = await reader.WaitToReadAsync(cancellationToken);
                lock (_gate) { if (_resume is null) return available; }
            }
        }
    }
}
