using System.Threading.Channels;

namespace Ansible.Core;

public enum MeetingSessionPhase { Idle, Starting, Recording, Paused, Finishing, Completed, Failed }

public sealed record MeetingSessionSnapshot(long Version, MeetingSessionPhase Phase, MeetingDocument? Document,
    double CapturedSeconds, double PendingSeconds, string? Error);

public sealed class MeetingSession : IAsyncDisposable
{
    private const int PacketBytes = 3200;
    private const long MaximumSamples = 16000L * 120 * 60;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly SemaphoreSlim _saving = new(1, 1);
    private readonly SemaphoreSlim _resources = new(1, 1);
    private readonly Func<IMeetingRecognitionEngine> _engineFactory;
    private readonly IMeetingCaptureFactory _captureFactory;
    private readonly MeetingStore _store;
    private readonly RecognitionOptions _options;
    private readonly Func<Task> _reserve;
    private readonly Func<Task> _release;
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
    private bool _reserved;
    private bool _disposed;
    private int _disposing;
    private bool _discarding;

    public MeetingSession(Func<IMeetingRecognitionEngine> engineFactory, IMeetingCaptureFactory captureFactory,
        MeetingStore store, RecognitionOptions options, Func<Task>? reserve = null, Func<Task>? release = null)
    {
        _engineFactory = engineFactory;
        _captureFactory = captureFactory;
        _store = store;
        _options = options;
        _reserve = reserve ?? (() => Task.CompletedTask);
        _release = release ?? (() => Task.CompletedTask);
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
            _discarding = false;
            _recognition = Task.CompletedTask;
            _pump = Task.CompletedTask;
            _cancellation?.Dispose();
            _cancellation = new();
            _audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(300)
            { SingleWriter = true, SingleReader = true, AllowSynchronousContinuations = false });
            _reader = new PausableReader(_audio.Reader);
            await SaveAsync();
            _engine = _engineFactory();
            var models = await _engine.ConnectAsync(_cancellation.Token);
            var model = models.FirstOrDefault(candidate => candidate.Family == "vibevoice_asr_streaming" && candidate.Mode == "streaming")
                ?? throw new InvalidOperationException("Install VibeVoice ASR Streaming 1.5B in Speech models before starting a meeting.");
            _capture = await _captureFactory.StartAsync(request, _cancellation.Token);
            _pump = PumpAsync(_capture, _cancellation.Token);
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
            SetPhase(MeetingSessionPhase.Recording, MeetingStatus.Recording);
            FlushPendingUpdate();
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
            if (!discard) FlushPendingUpdate();
            else { lock (_gate) _pendingUpdate = null; }
            _audio!.Writer.TryComplete();
            await _recognition;
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
        lock (_gate)
        {
            if (_discarding) return;
            if (_state.Phase == MeetingSessionPhase.Paused) { _pendingUpdate = update; return; }
            ApplyUpdate(update);
        }
        Publish();
        SaveAsync().GetAwaiter().GetResult();
    }

    private void ApplyUpdate(TranscriptUpdate update)
    {
        var turns = _transcript.Update(update.Text, _reader!.ReadSamples / 16000.0, update.IsFinal);
        _state = _state with { Version = _state.Version + 1,
            Document = _state.Document! with { Turns = turns, UpdatedAt = DateTimeOffset.UtcNow },
            PendingSeconds = Math.Max(0, (_capturedSamples - _reader.ReadSamples) / 16000.0) };
    }

    private void FlushPendingUpdate()
    {
        lock (_gate)
        {
            if (_pendingUpdate is not { } update) return;
            _pendingUpdate = null;
            ApplyUpdate(update);
        }
        Publish();
    }

    public async Task RenameAsync(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title.Trim().Length > 500) throw new ArgumentException("Meeting titles can contain at most 500 characters.");
        lock (_gate) _state = _state with { Version = _state.Version + 1,
            Document = (_state.Document ?? throw new InvalidOperationException("Start a meeting first.")) with { Title = title.Trim(), UpdatedAt = DateTimeOffset.UtcNow } };
        Publish();
        await SaveAsync();
    }

    public async Task RenameSpeakerAsync(string id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
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

    private sealed class PausableReader(ChannelReader<byte[]> reader) : ChannelReader<byte[]>
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
