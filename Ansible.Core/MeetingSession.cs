using System.Threading.Channels;

namespace Ansible.Core;

public enum MeetingSessionPhase { Idle, Starting, Recording, Paused, Finishing, Completed, Failed, Refining }

public sealed record MeetingSessionSnapshot(long Version, MeetingSessionPhase Phase, MeetingDocument? Document,
    double CapturedSeconds, double PendingSeconds, string? Error)
{
    public string? NamingStatus { get; init; }
    public string? RefinementStatus { get; init; }
    public string DeviceSelectionSummary { get; init; } = "";
}

public sealed class MeetingSession : IAsyncDisposable
{
    private const int PacketBytes = 3200;
    private const long MaximumSamples = 16000L * 120 * 60;
    private readonly object _gate = new();
    private readonly object _updates = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly SemaphoreSlim _saving = new(1, 1);
    private readonly SemaphoreSlim _resources = new(1, 1);
    private readonly Func<IMeetingRecognitionEngine> _engineFactory;
    private readonly IMeetingCaptureFactory _captureFactory;
    private readonly MeetingStore _store;
    private readonly RecognitionOptions _options;
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
        MeetingStore store, RecognitionOptions options, Func<Task>? reserve = null, Func<Task>? release = null,
        Func<IMeetingSpeakerObserver>? observerFactory = null, Func<IMeetingWordAligner>? alignerFactory = null)
    {
        _engineFactory = engineFactory;
        _captureFactory = captureFactory;
        _store = store;
        _options = options;
        _reserve = reserve ?? (() => Task.CompletedTask);
        _release = release ?? (() => Task.CompletedTask);
        _observerFactory = observerFactory;
        _alignerFactory = alignerFactory;
    }

    public event Action<MeetingSessionSnapshot>? StateChanged;
    public MeetingSessionSnapshot State { get { lock (_gate) return _state; } }
    public MeetingDocument? Document => State.Document;
    public bool IsActive { get { lock (_gate) return _reserved; } }

    public async Task StartAsync(MeetingCaptureRequest request)
    {
        request.Validate();
        await _commands.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed || Volatile.Read(ref _disposing) != 0, this);
            if (IsActive) throw new InvalidOperationException("End the current meeting before starting another.");
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
            _cancellation = new();
            _audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(300)
            { SingleWriter = true, SingleReader = true, AllowSynchronousContinuations = false });
            _reader = new PausableReader(_audio.Reader, _timeline.Append);
            await SaveAsync();
            _engine = _engineFactory();
            var models = await _engine.ConnectAsync(_cancellation.Token);
            var model = models.FirstOrDefault(candidate => candidate.Family == "vibevoice_asr_streaming" && candidate.Mode == "streaming")
                ?? throw new InvalidOperationException("Install VibeVoice ASR Streaming 1.5B in Speech models before starting a meeting.");
            await _engine.PrepareMeetingAsync(model, _options, _cancellation.Token);
            _capture = await _captureFactory.StartAsync(request, _cancellation.Token);
            AttachSourceMetadata(_capture);
            _pump = PumpAsync(_capture, _cancellation.Token);
            await StartObserverAsync();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _recognition = RecognizeAsync(model, ready);
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
        finally { _commands.Release(); }
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
    public async Task DiscardAsync() => await FinishAsync(true);

    private async Task FinishAsync(bool discard)
    {
        await _commands.WaitAsync();
        try
        {
            if (!IsActive) return;
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

    private async Task RecognizeAsync(AudioModel model, TaskCompletionSource ready)
    {
        try
        {
            await _engine!.StreamMeetingAsync(model, _reader!, _options, new InlineProgress(ReceiveUpdate),
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
        SaveAsync().GetAwaiter().GetResult();
    }

    private void ApplyUpdate(TranscriptUpdate update)
    {
        var processed = update.ProcessedSamples ?? _reader!.ReadSamples;
        if (processed < 0 || processed > _reader!.ReadSamples)
            throw new InvalidDataException("Recognition returned a timestamp outside the consumed audio.");
        var turns = _transcript.Update(update.Text, processed / 16000.0, update.IsFinal).ToArray();
        var previous = Document!.Turns;
        for (var index = 0; index < turns.Length; index++)
        {
            var old = index < previous.Count ? previous[index] : null;
            if (old?.SpeakerId == turns[index].SpeakerId && old.Text == turns[index].Text)
                turns[index] = old with { IsProvisional = turns[index].IsProvisional };
        }
        RefineTiming(turns, processed);
        for (var index = 0; index < turns.Length; index++)
            if (turns[index].TimingQuality == MeetingTimingQuality.Aligned &&
                !(index < previous.Count && previous[index].Text == turns[index].Text && previous[index].TimingQuality == MeetingTimingQuality.Aligned))
                turns[index] = turns[index] with { Source = _timeline.Source(turns[index].StartSeconds, turns[index].EndSeconds) };
        lock (_gate)
        {
            if (_discarding) return;
            if (_state.Phase == MeetingSessionPhase.Paused) { _pendingUpdate = update; return; }
            var document = _state.Document! with { Turns = turns, UpdatedAt = DateTimeOffset.UtcNow };
            _state = _state with { Version = _state.Version + 1, Document = _speakerMatcher.Refine(document),
                PendingSeconds = Math.Max(0, (_capturedSamples - _reader!.ReadSamples) / 16000.0) };
        }
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
            await _saving.WaitAsync();
            try
            {
                await _store.SaveAsync(updated);
                lock (_gate)
                {
                    _state = _state with { Version = _state.Version + 1, Document = updated };
                }
            }
            finally { _saving.Release(); }
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
        {
            UpdateRefinementStatus(naming: "Automatic names require a selected Teams meeting window.");
            return;
        }
        _observer ??= _observerFactory();
        try
        {
            await _observer.StartAsync(_request.ProcessId.Value, ObservationSamplePosition, observation =>
            {
                _speakerMatcher.Observe(observation);
                UpdateRefinementStatus(naming: observation.Status switch
                {
                    MeetingSpeakerReadStatus.Readable => "Teams speaker evidence available; names require consistent timed turns.",
                    MeetingSpeakerReadStatus.Ambiguous => "Teams speaker evidence is ambiguous; keeping speaker labels.",
                    _ => "Teams speaker names unavailable in this window; keeping speaker labels."
                });
            }, _cancellation!.Token);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            UpdateRefinementStatus(naming: "Automatic names unavailable for the selected app; keeping editable model labels.");
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
            UpdateRefinementStatus(refinement: "Local ASR anchors refine confident turns; other times remain approximate.");
        }
        catch (OperationCanceledException) when (_cancellation!.IsCancellationRequested) { }
        catch (Exception error)
        {
            _alignmentUnavailable = true;
            UpdateRefinementStatus(refinement: $"Optional timing refinement unavailable: {error.Message}");
        }
    }

    private void UpdateRefinementStatus(string? naming = null, string? refinement = null)
    {
        lock (_gate)
        {
            var nextNaming = naming ?? _state.NamingStatus;
            var nextRefinement = refinement ?? _state.RefinementStatus;
            if (nextNaming == _state.NamingStatus && nextRefinement == _state.RefinementStatus) return;
            _state = _state with { Version = _state.Version + 1, NamingStatus = nextNaming, RefinementStatus = nextRefinement };
        }
        Publish();
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

    private async Task SaveAsync()
    {
        await _saving.WaitAsync();
        try { if (Document is { } document) await _store.SaveAsync(document); }
        finally { _saving.Release(); }
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
            if (_engine is { } engine) { await engine.DisposeAsync(); _engine = null; }
            if (_aligner is { } aligner) { await aligner.DisposeAsync(); _aligner = null; }
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
            DeviceSelectionSummary = _engine?.DeviceSelectionSummary ?? _state.DeviceSelectionSummary,
            Document = _state.Document is { } document ? document with { Status = status, UpdatedAt = DateTimeOffset.UtcNow } : null };
        Publish();
    }

    private void Publish() => StateChanged?.Invoke(State);

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposing, 1);
        await EndAsync();
        await _recognition;
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
