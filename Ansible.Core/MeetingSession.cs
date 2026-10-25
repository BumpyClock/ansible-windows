using System.Threading.Channels;

namespace Ansible.Core;

public enum MeetingSessionPhase { Idle, Starting, Recording, Paused, Finishing, Completed, Failed, Refining }

public sealed record MeetingSessionSnapshot(long Version, MeetingSessionPhase Phase, MeetingDocument? Document,
    double CapturedSeconds, double PendingSeconds, string? Error)
{
    /// <summary>Why an unconfigured meeting skipped an installed pair, or null when the first candidate was used.</summary>
    public string? SelectionNotice { get; init; }
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
    private readonly Func<IMeetingRecognitionEngine> _engineFactory;
    private readonly IMeetingCaptureFactory _captureFactory;
    private readonly MeetingStore _store;
    private readonly Func<RecognitionOptions> _getOptions;
    private readonly Func<string?>? _getModelId;
    private readonly Func<Task> _reserve;
    private readonly Func<Task> _release;
    private readonly Func<IMeetingSpeakerObserver>? _observerFactory;
    private readonly Func<IMeetingWordAligner>? _alignerFactory;
    private IMeetingSpeakerObserver? _observer;
    private IMeetingWordAligner? _aligner;
    private bool _alignmentUnavailable;
    private MeetingAudioTimeline _timeline = new();
    private MeetingSpeakerMatcher _speakerMatcher = new();
    private MeetingSessionSnapshot _state = new(0, MeetingSessionPhase.Idle, null, 0, 0, null);
    private MeetingCaptureRequest? _request;
    private IMeetingRecognitionEngine? _engine;
    private IAudioCapture? _capture;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _preparation;
    private Channel<byte[]>? _audio;
    private PausableReader? _reader;
    private MeetingTranscript _transcript = new();
    private TranscriptUpdate? _pendingUpdate;
    private Task _pump = Task.CompletedTask;
    private Task _recognition = Task.CompletedTask;
    private long _capturedSamples;
    private long _captureClockBase;
    private bool _reserved;
    private bool _disposed;
    private int _disposing;
    private bool _discarding;

    public MeetingSession(Func<IMeetingRecognitionEngine> engineFactory, IMeetingCaptureFactory captureFactory,
        MeetingStore store, Func<RecognitionOptions> getOptions, Func<Task>? reserve = null, Func<Task>? release = null,
        Func<IMeetingSpeakerObserver>? observerFactory = null, Func<IMeetingWordAligner>? alignerFactory = null,
        Func<string?>? getModelId = null, Func<MeetingDocument, Task>? writeMeeting = null)
    {
        _engineFactory = engineFactory;
        _captureFactory = captureFactory;
        _store = store;
        _getOptions = getOptions;
        _getModelId = getModelId;
        _reserve = reserve ?? (() => Task.CompletedTask);
        _release = release ?? (() => Task.CompletedTask);
        _observerFactory = observerFactory;
        _alignerFactory = alignerFactory;
        _persistence = new(writeMeeting ?? (document => _store.SaveAsync(document)), StorageFailed);
    }

    public event Action<MeetingSessionSnapshot>? StateChanged;
    public MeetingSessionSnapshot State { get { lock (_gate) return _state; } }
    public MeetingDocument? Document => State.Document;
    public bool IsActive { get { lock (_gate) return _reserved; } }

    public async Task StartAsync(MeetingCaptureRequest request)
    {
        request.Validate();
        await _commands.WaitAsync();
        CancellationTokenSource? preparation = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed || Volatile.Read(ref _disposing) != 0, this);
            if (IsActive) throw new InvalidOperationException("End the current meeting before starting another.");
            await SaveAsync();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposing) != 0, this);
                _preparation = preparation = new();
            }
            var options = _getOptions() ?? throw new InvalidOperationException("Meeting recognition settings are unavailable.");
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
            _transcript = new();
            _timeline = new();
            _speakerMatcher = new();
            _alignmentUnavailable = false;
            _discarding = false;
            _recognition = Task.CompletedTask;
            _pump = Task.CompletedTask;
            _cancellation?.Dispose();
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(preparation.Token);
            _audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(300)
            { SingleWriter = true, SingleReader = true, AllowSynchronousContinuations = false });
            _reader = new PausableReader(_audio.Reader, _timeline.Append);
            await SaveAsync();
            _engine = _engineFactory();
            var models = await _engine.ConnectAsync(_cancellation.Token);
            var candidates = SelectCandidates(models, selectedId);
            var requested = options;
            var skipped = new List<string>();
            AudioModel? model = null;
            for (var index = 0; index < candidates.Count; index++)
            {
                model = candidates[index];
                options = requested with { CustomDictionary = model.SupportsCustomDictionary ? requested.CustomDictionary : "" };
                try
                {
                    await _engine.PrepareMeetingAsync(model, options, _cancellation.Token);
                    break;
                }
                catch (MeetingRealTimeException error) when (index < candidates.Count - 1)
                {
                    // Unconfigured meetings continue down the documented order; an explicit choice is its only candidate.
                    skipped.Add(error.Message);
                }
                catch (MeetingRealTimeException error) when (skipped.Count > 0)
                {
                    throw new MeetingRealTimeException(error.ModelId, error.Backend, error.RealTimeFactor, error.Maximum, string.Join(" ", skipped));
                }
            }
            if (skipped.Count > 0)
            {
                lock (_gate) _state = _state with { Version = _state.Version + 1,
                    SelectionNotice = $"{string.Join(" ", skipped)} Using {model!.Id} instead." };
                Publish();
            }
            _cancellation.Token.ThrowIfCancellationRequested();
            _capture = await _captureFactory.StartAsync(request, _cancellation.Token);
            _cancellation.Token.ThrowIfCancellationRequested();
            AttachSourceMetadata(_capture);
            _pump = PumpAsync(_capture, _cancellation.Token);
            await StartObserverAsync();
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
            if (error is CaptureOwnershipException ownership) _capture = ownership.Capture;
            if (IsActive)
            {
                SetPhase(MeetingSessionPhase.Failed, MeetingStatus.Failed, error.Message);
                _cancellation?.Cancel();
                _reader?.Resume();
                _audio?.Writer.TryComplete(error);
                await _recognition;
                try { await ReleaseResourcesAsync(); }
                finally { await SaveAsync(); }
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

    private static bool UsesCompanion(AudioModel model) => model.Family is "nemotron_asr" or "kroko_asr";

    private static bool IsCompanion(AudioModel model) => model.Id == AudioModelDefaults.DiarizationModelId &&
        model.Family == "nemotron_3_diar" && model.Task == "diar" && model.Mode == "streaming";

    // Explicit: that model alone. Unconfigured: paired Nemotron, paired Kroko, then VibeVoice, as documented.
    private static IReadOnlyList<AudioModel> SelectCandidates(IReadOnlyList<AudioModel> models, string? selectedId)
    {
        var companionInstalled = models.Any(IsCompanion);
        if (selectedId is not null)
        {
            var selected = models.FirstOrDefault(candidate => candidate.Id == selectedId && SupportsMeeting(candidate))
                ?? throw new InvalidOperationException($"The selected meeting model '{selectedId}' is not installed or cannot stream meetings.");
            if (UsesCompanion(selected) && !companionInstalled)
                throw new InvalidOperationException($"Install Nemotron 3 Diarization Q8 before starting a meeting with '{selectedId}'.");
            return [selected];
        }
        var candidates = new List<AudioModel>();
        if (companionInstalled)
        {
            foreach (var id in new[] { AudioModelDefaults.RecognitionModelId, AudioModelDefaults.KrokoModelId })
                if (models.FirstOrDefault(candidate => candidate.Id == id && UsesCompanion(candidate) && SupportsMeeting(candidate)) is { } paired)
                    candidates.Add(paired);
        }
        if (models.FirstOrDefault(candidate => candidate.Family == "vibevoice_asr_streaming" && SupportsMeeting(candidate)) is { } vibeVoice)
            candidates.Add(vibeVoice);
        if (candidates.Count == 0)
            throw new InvalidOperationException("Install Nemotron or Kroko ASR with Nemotron 3 Diarization, or VibeVoice ASR Streaming 1.5B before starting a meeting.");
        return candidates;
    }

    public static bool SupportsMeeting(AudioModel model) => model.Task != "diar" && model.Mode == "streaming" &&
        model.Family is "nemotron_asr" or "kroko_asr" or "vibevoice_asr_streaming";

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
        }
        catch (Exception error)
        {
            SetPhase(MeetingSessionPhase.Failed, MeetingStatus.Failed, error.Message);
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
        try
        {
            RequirePhase(MeetingSessionPhase.Paused);
            if (_capture is not null) throw new InvalidOperationException("Release the previous capture before resuming the meeting.");
            _capture = await _captureFactory.StartAsync(_request!, _cancellation!.Token);
            AttachSourceMetadata(_capture);
            await StartObserverAsync();
            SetPhase(MeetingSessionPhase.Recording, MeetingStatus.Recording);
            await Task.Run(FlushPendingUpdate);
            _reader!.Resume();
            _pump = PumpAsync(_capture, _cancellation.Token);
            await SaveAsync();
        }
        catch (CaptureOwnershipException error)
        {
            _capture = error.Capture;
            SetPhase(MeetingSessionPhase.Failed, MeetingStatus.Failed, error.Message);
            _cancellation!.Cancel();
            _reader!.Resume();
            _audio!.Writer.TryComplete(error);
            await _recognition;
            await ReleaseResourcesAsync();
            await SaveAsync();
            throw;
        }
        finally { _commands.Release(); }
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
            if (!IsActive) { await SaveAsync(); return; }
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
                lock (_gate) _state = _state with { Document = _speakerMatcher.Refine(_state.Document!) };
                Publish();
            }
            await ReleaseResourcesAsync();
            if (State.Error is null)
                SetPhase(MeetingSessionPhase.Completed, discard ? MeetingStatus.Interrupted : MeetingStatus.Completed);
            else SetPhase(MeetingSessionPhase.Failed, MeetingStatus.Failed);
            await SaveAsync();
        }
        catch (Exception error)
        {
            SetPhase(MeetingSessionPhase.Failed, MeetingStatus.Failed, error.Message);
            _cancellation?.Cancel();
            _reader?.Resume();
            _audio?.Writer.TryComplete(error);
            await _recognition;
            await SaveAsync();
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
            _cancellation!.Cancel();
            _reader!.Resume();
            _audio!.Writer.TryComplete(error);
            SetPhase(MeetingSessionPhase.Failed, MeetingStatus.Failed, error.Message);
        }
        finally
        {
            if (State.Phase == MeetingSessionPhase.Failed)
            {
                try { await ReleaseResourcesAsync(); await SaveAsync(); }
                catch (Exception error) { SetPhase(MeetingSessionPhase.Failed, MeetingStatus.Failed, $"{State.Error} {error.Message}"); }
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
            _audio!.Writer.TryComplete(error);
            SetPhase(MeetingSessionPhase.Failed, MeetingStatus.Failed, error.Message);
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
        var turns = update.SpeakerTurns is { } timed
            ? ValidateTimedTurns(timed, processed, update.IsFinal)
            : _transcript.Update(update.Text, processed / 16000.0, update.IsFinal).ToArray();
        var previous = Document!.Turns;
        for (var index = 0; update.SpeakerTurns is null && index < turns.Length; index++)
        {
            var old = index < previous.Count ? previous[index] : null;
            if (old?.SpeakerId == turns[index].SpeakerId && old.Text == turns[index].Text)
                turns[index] = old with { IsProvisional = turns[index].IsProvisional };
        }
        if (update.SpeakerTurns is null) RefineTiming(turns, processed);
        for (var index = 0; index < turns.Length; index++)
        {
            var turn = turns[index];
            if (turn.TimingQuality != MeetingTimingQuality.Aligned) continue;
            var old = index < previous.Count ? previous[index] : null;
            var unchangedBounds = old?.TimingQuality == MeetingTimingQuality.Aligned &&
                old.StartSeconds == turn.StartSeconds && old.EndSeconds == turn.EndSeconds;
            turns[index] = turn with { Source = unchangedBounds ? old!.Source : _timeline.Source(turn.StartSeconds, turn.EndSeconds) };
        }
        lock (_gate)
        {
            if (_discarding) return;
            if (_state.Phase == MeetingSessionPhase.Paused) { _pendingUpdate = update; return; }
            var document = _state.Document! with { Turns = turns, UpdatedAt = DateTimeOffset.UtcNow };
            _state = _state with { Version = _state.Version + 1, Document = _speakerMatcher.Refine(document),
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
            await _persistence.FlushAsync(revision);
            lock (_gate) _state = _state with { Version = _state.Version + 1, Document = updated };
            Publish();
        }
        finally { _commands.Release(); }
    }

    private void AttachSourceMetadata(IAudioCapture capture)
    {
        var start = Interlocked.Read(ref _capturedSamples);
        Interlocked.Exchange(ref _captureClockBase, start);
        if (capture is IMeetingAudioSourceMetadata metadata)
            metadata.Activity += activity => _timeline.Observe(activity with { StartSample = start + activity.StartSample });
    }

    private async Task StartObserverAsync()
    {
        if (_observerFactory is null || _request?.ProcessId is not > 0)
            return;
        _observer ??= _observerFactory();
        try
        {
            await _observer.StartAsync(_request.ProcessId.Value, ObservationSamplePosition,
                _speakerMatcher.Observe, _cancellation!.Token);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Trace.TraceWarning("Ansible: automatic speaker names unavailable. {0}", error.Message);
        }
    }

    private long ObservationSamplePosition() => _capture is IMeetingAudioSourceMetadata metadata
        ? Interlocked.Read(ref _captureClockBase) + metadata.ClockSamplePosition
        : Interlocked.Read(ref _capturedSamples);

    private void RefineTiming(MeetingTurn[] turns, long processed)
    {
        if (_alignerFactory is null || _alignmentUnavailable) return;
        var clip = _timeline.Clip(processed);
        if (clip is null) return;
        var origin = clip.Value.StartSample / 16000.0;
        var indices = Enumerable.Range(0, turns.Length).Where(index =>
            !turns[index].IsProvisional && (turns[index].TimingQuality == MeetingTimingQuality.Aligned
                ? turns[index].EndSeconds > origin : turns[index].StartSeconds >= origin)).ToArray();
        if (!indices.Any(index => turns[index].TimingQuality != MeetingTimingQuality.Aligned)) return;
        try
        {
            _aligner ??= _alignerFactory();
            _aligner.InitializeAsync(_cancellation!.Token).GetAwaiter().GetResult();
            var candidates = indices.Select(index => turns[index] with
                { StartSeconds = Math.Max(0, turns[index].StartSeconds - origin), EndSeconds = turns[index].EndSeconds - origin }).ToArray();
            var aligned = _aligner.AlignAsync(clip.Value.Audio, candidates, _cancellation.Token).GetAwaiter().GetResult();
            for (var index = 0; index < indices.Length; index++)
                if (turns[indices[index]].TimingQuality != MeetingTimingQuality.Aligned && aligned[index].TimingQuality == MeetingTimingQuality.Aligned)
                    turns[indices[index]] = aligned[index] with
                        { StartSeconds = aligned[index].StartSeconds + origin, EndSeconds = aligned[index].EndSeconds + origin };
        }
        catch (OperationCanceledException) when (_cancellation!.IsCancellationRequested) { }
        catch (Exception error)
        {
            _alignmentUnavailable = true;
            System.Diagnostics.Trace.TraceWarning("Ansible: optional timing refinement unavailable. {0}", error.Message);
        }
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

    private Task SaveAsync() => Document is null ? Task.CompletedTask : _persistence.FlushAsync(QueueSave());

    private void StorageFailed(Exception error)
    {
        // Inactive edits report failure to their awaiting caller and retain their committed transcript.
        if (!IsActive) return;
        SetPhase(MeetingSessionPhase.Failed, MeetingStatus.Failed, $"Meeting transcript save failed. {error.Message}");
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
        if (_observer is not null)
        {
            await _observer.StopAsync();
            _speakerMatcher.Observe(new(ObservationSamplePosition(), [], null, MeetingSpeakerReadStatus.Unavailable));
        }
        var capture = _capture;
        if (capture is null) return;
        await capture.StopAsync();
        await _pump;
        await capture.DisposeAsync();
        if (!capture.IsReleased) throw new InvalidOperationException("Meeting audio resources remain owned. Retry ending the meeting.");
        _capture = null;
    }

    private async Task ReleaseResourcesAsync()
    {
        await _resources.WaitAsync();
        try
        {
            await StopCaptureCoreAsync();
            if (_engine is { } engine) { await Task.Run(async () => await engine.DisposeAsync()); _engine = null; }
            if (_aligner is { } aligner) { await Task.Run(async () => await aligner.DisposeAsync()); _aligner = null; }
            if (_observer is { } observer) { await observer.DisposeAsync(); _observer = null; }
            _timeline = new();
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

    private void SetPhase(MeetingSessionPhase phase, MeetingStatus status, string? error = null)
    {
        lock (_gate) _state = _state with { Version = _state.Version + 1, Phase = phase, Error = error ?? _state.Error,
            Document = _state.Document is { } document ? document with { Status = status, UpdatedAt = DateTimeOffset.UtcNow } : null };
        Publish();
    }

    private void Publish() => StateChanged?.Invoke(State);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _disposing, 1);
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
