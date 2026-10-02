using System.Threading.Channels;
using DictationPoc.Core;

namespace DictationPoc.Tests;

public sealed class DictationSessionTests
{
    [Fact]
    public async Task CloseJoinsLateConnectionAndNeverPublishesReady()
    {
        var connect = new TaskCompletionSource<IReadOnlyList<AudioModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakeEngine { Connecting = _ => connect.Task };
        var session = Create(engine);
        var initialization = session.InitializeAsync();
        await engine.ConnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var close = session.CloseAsync();
        Assert.False(close.IsCompleted);
        Assert.Equal(DictationPhase.Closing, session.State.Phase);
        connect.SetResult([Model]);
        Assert.Equal(SessionOutcomeKind.Cancelled, (await initialization).Kind);
        await close;
        Assert.Equal(DictationPhase.Closed, session.State.Phase);
        Assert.Equal(1, engine.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StartDictationAsync());
    }

    [Fact]
    public async Task CancelledReadinessCannotOpenAMicrophone()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakeEngine
        {
            Streaming = async (_, _, token, callback) =>
            {
                await ready.Task;
                callback?.Invoke();
                token.ThrowIfCancellationRequested();
                return new RecognitionResult("", "");
            }
        };
        var capture = new FakeCaptureFactory();
        var session = Create(engine, capture);
        await session.InitializeAsync();
        var recording = session.StartDictationAsync();
        await engine.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await session.CancelAsync();
        ready.SetResult();
        Assert.Equal(SessionOutcomeKind.Cancelled, (await recording).Kind);
        Assert.Equal(0, capture.Starts);
        Assert.Equal(DictationPhase.Ready, session.State.Phase);
        await session.CloseAsync();
    }

    [Fact]
    public async Task CancelAfterReadinessBeforeFactoryReturnReleasesCapture()
    {
        var source = new FakeCapture();
        var opening = new TaskCompletionSource<IAudioCapture>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeCaptureFactory { Starting = _ => opening.Task };
        var engine = new FakeEngine();
        var session = Create(engine, factory);
        await session.InitializeAsync();
        var recording = session.StartDictationAsync();
        await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await session.CancelAsync();
        opening.SetResult(source);
        Assert.Equal(SessionOutcomeKind.Cancelled, (await recording).Kind);
        Assert.True(source.IsReleased);
        Assert.Equal(1, source.Disposals);
        Assert.Equal(0, engine.FinishedStreams);
        await session.CloseAsync();
    }

    [Fact]
    public async Task OneOperationOwnsCaptureAndCountsAuthoritativeSpeechOnce()
    {
        var usage = new FakeUsageStore();
        var source = new FakeCapture { Seconds = 10 };
        var factory = new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(source) };
        var engine = new FakeEngine { Result = new("Speaker 0: hello world", "hello world") };
        var session = Create(engine, factory, usage);
        await session.InitializeAsync();
        var recording = session.StartDictationAsync();
        await WaitForStateAsync(session, DictationPhase.Recording);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartDictationAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync("other"));
        await source.Packets.Writer.WriteAsync([0, 0, 0, 0]);
        await session.FinishAsync();
        var outcome = await recording;
        Assert.Equal(SessionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal("Speaker 0: hello world", session.State.Transcript);
        Assert.Equal(2, Assert.Single(usage.Document.Entries).Words);
        Assert.Equal(10, usage.Document.Entries[0].RecordingSeconds);
        Assert.Equal(1, source.Disposals);
        Assert.Equal(1, factory.Starts);
        await session.CloseAsync();
    }

    [Fact]
    public async Task FailedCaptureReleaseBlocksNewWorkAndCloseRetriesOwnership()
    {
        var source = new FakeCapture { FailDisposal = true };
        var factory = new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(source) };
        var session = Create(new FakeEngine(), factory);
        await session.InitializeAsync();
        var recording = session.StartDictationAsync();
        await WaitForStateAsync(session, DictationPhase.Recording);
        await session.CancelAsync();
        Assert.Equal(SessionOutcomeKind.Failed, (await recording).Kind);
        Assert.Equal(DictationPhase.RecoveryRequired, session.State.Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartDictationAsync());
        await Assert.ThrowsAsync<IOException>(() => session.CloseAsync());
        Assert.Equal(DictationPhase.Closing, session.State.Phase);
        source.FailDisposal = false;
        await session.CloseAsync();
        Assert.True(source.IsReleased);
        Assert.Equal(DictationPhase.Closed, session.State.Phase);
    }

    [Fact]
    public async Task StartupFailureRetainsAnUnreleasedCapture()
    {
        var source = new FakeCapture { FailDisposal = true };
        var factory = new FakeCaptureFactory
        {
            Starting = _ => Task.FromException<IAudioCapture>(
                new CaptureOwnershipException("startup cleanup failed", source, new IOException("driver busy")))
        };
        var session = Create(new FakeEngine(), factory);
        await session.InitializeAsync();
        Assert.Equal(SessionOutcomeKind.Failed, (await session.StartDictationAsync()).Kind);
        Assert.Equal(DictationPhase.RecoveryRequired, session.State.Phase);
        source.FailDisposal = false;
        await session.CloseAsync();
        Assert.True(source.IsReleased);
    }

    [Fact]
    public async Task InvalidReplayInputIsRejectedBeforeNativePreparation()
    {
        var engine = new FakeEngine();
        var input = new FakeInput
        {
            Replay = _ => Task.FromException<byte[]>(new InvalidDataException("invalid PCM input"))
        };
        var session = Create(engine, input: input);
        await session.InitializeAsync();
        Assert.Equal(SessionOutcomeKind.Failed, (await session.ReplayAsync("bad.wav")).Kind);
        Assert.Equal(0, engine.StreamStarts);
        await session.CloseAsync();
    }

    [Fact]
    public async Task StoreFailureDoesNotTurnACompletedTranscriptIntoFailure()
    {
        var usage = new FakeUsageStore { FailRecording = true };
        var session = Create(new FakeEngine(), usage: usage);
        await session.InitializeAsync();
        var outcome = await session.TranscribeFileAsync("sample.wav");
        Assert.Equal(SessionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal("hello world", session.State.Transcript);
        Assert.Equal(NoticeKind.Warning, session.State.Notice.Kind);
        Assert.Empty(usage.Document.Entries);
        await session.CloseAsync();
    }

    [Fact]
    public async Task CloseAlsoJoinsPreferencePersistence()
    {
        var saving = new TaskCompletionSource<UsageDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var usage = new FakeUsageStore { ChangingPreference = (_, _) => saving.Task };
        var session = Create(new FakeEngine(), usage: usage);
        await session.InitializeAsync();
        var change = session.SetUsageEnabledAsync(false);
        await usage.PreferenceEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var close = session.CloseAsync();
        Assert.False(close.IsCompleted);
        saving.SetResult(new UsageDocument { Enabled = false });
        Assert.Equal(SessionOutcomeKind.Cancelled, (await change).Kind);
        await close;
        Assert.Equal(DictationPhase.Closed, session.State.Phase);
    }

    [Fact]
    public async Task RepeatedCloseUsesOneLifetimeTask()
    {
        var session = Create(new FakeEngine());
        await session.InitializeAsync();
        var first = session.CloseAsync();
        var second = session.CloseAsync();
        Assert.Same(first, second);
        await first;
    }

    [Fact]
    public async Task DeadlineIsDistinctFromUserCancellation()
    {
        var clock = new ManualTime();
        var engine = new FakeEngine
        {
            Streaming = async (_, _, token, _) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new("", "");
            }
        };
        var session = new DictationSession(_ => engine, new FakeCaptureFactory(), new FakeInput(),
            new FakeUsageStore(), "models", clock, TimeSpan.FromMinutes(1));
        await session.InitializeAsync();
        var recording = session.StartDictationAsync();
        await engine.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(SessionOutcomeKind.TimedOut, (await recording).Kind);
        Assert.Equal(DictationPhase.Ready, session.State.Phase);
        await session.CloseAsync();
    }

    [Fact]
    public async Task LateTranscriptCallbacksCannotChangeACompletedOperation()
    {
        var engine = new FakeEngine();
        var source = new FakeCapture();
        var session = Create(engine, new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(source) });
        await session.InitializeAsync();
        var recording = session.StartDictationAsync();
        await WaitForStateAsync(session, DictationPhase.Recording);
        await session.FinishAsync();
        await recording;
        engine.LastProgress!.Report(new TranscriptUpdate("stale text", true));
        Assert.Equal("hello world", session.State.Transcript);
        await session.CloseAsync();
    }

    [Fact]
    public async Task DisabledCollectionDoesNotEmitCompletedSessionMetadata()
    {
        var usage = new FakeUsageStore();
        var session = Create(new FakeEngine(), usage: usage);
        await session.InitializeAsync();
        await session.SetUsageEnabledAsync(false);
        Assert.Equal(SessionOutcomeKind.Completed, (await session.TranscribeFileAsync("sample.wav")).Kind);
        Assert.Empty(usage.Document.Entries);
        await session.CloseAsync();
    }

    private static readonly AudioModel Model = new() { Id = "test", Family = "test", Mode = "streaming" };

    private static DictationSession Create(
        FakeEngine engine, FakeCaptureFactory? capture = null,
        FakeUsageStore? usage = null, FakeInput? input = null) =>
        new(_ => engine, capture ?? new FakeCaptureFactory(), input ?? new FakeInput(),
            usage ?? new FakeUsageStore(), "models");

    private static Task WaitForStateAsync(DictationSession session, DictationPhase phase)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(SessionSnapshot state) { if (state.Phase == phase) { completion.TrySetResult(); } }
        session.StateChanged += Changed;
        if (session.State.Phase == phase) { completion.TrySetResult(); }
        return WaitAsync();
        async Task WaitAsync()
        {
            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
            finally { session.StateChanged -= Changed; }
        }
    }

    private sealed class FakeEngine : IRecognitionEngine
    {
        public string ModelsDirectory => "models";
        public string Version => "test";
        public RecognitionResult Result { get; init; } = new("hello world", "hello world");
        public Func<CancellationToken, Task<IReadOnlyList<AudioModel>>>? Connecting { get; init; }
        public Func<AudioModel, ChannelReader<byte[]>, CancellationToken, Action?, Task<RecognitionResult>>? Streaming { get; init; }
        public TaskCompletionSource ConnectEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StreamEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposals;
        public int StreamStarts;
        public int FinishedStreams;
        public IProgress<TranscriptUpdate>? LastProgress;
        public Task<IReadOnlyList<AudioModel>> ConnectAsync(CancellationToken token)
        {
            ConnectEntered.TrySetResult();
            return Connecting?.Invoke(token) ?? Task.FromResult<IReadOnlyList<AudioModel>>([Model]);
        }
        public Task<RecognitionResult> TranscribeAsync(
            AudioModel model, WaveAudio audio, string? language, IProgress<TranscriptUpdate>? progress, CancellationToken token) =>
            Task.FromResult(Result);
        public async Task<RecognitionResult> StreamAsync(
            AudioModel model, ChannelReader<byte[]> audio, string? language, IProgress<TranscriptUpdate>? progress,
            CancellationToken token, Action? onReady = null)
        {
            StreamStarts++;
            LastProgress = progress;
            StreamEntered.TrySetResult();
            if (Streaming is not null) { return await Streaming(model, audio, token, onReady); }
            onReady?.Invoke();
            await foreach (var _ in audio.ReadAllAsync(token)) { }
            token.ThrowIfCancellationRequested();
            FinishedStreams++;
            return Result;
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class FakeCaptureFactory : IAudioCaptureFactory
    {
        public int Starts;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<CancellationToken, Task<IAudioCapture>>? Starting { get; init; }
        public Task<IAudioCapture> StartAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Starts++;
            Entered.TrySetResult();
            return Starting?.Invoke(token) ?? Task.FromResult<IAudioCapture>(new FakeCapture());
        }
    }

    private sealed class FakeCapture : IAudioCapture
    {
        public Channel<byte[]> Packets { get; } = Channel.CreateUnbounded<byte[]>();
        public ChannelReader<byte[]> Audio => Packets.Reader;
        public double Seconds { get; init; }
        public double CapturedSeconds => Seconds;
        public bool IsReleased { get; private set; }
        public bool FailDisposal { get; set; }
        public int Disposals;
        public event Action<double>? LevelChanged { add { } remove { } }
        public Task StopAsync() { Packets.Writer.TryComplete(); return Task.CompletedTask; }
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            await StopAsync();
            if (FailDisposal) { throw new IOException("driver still owns capture"); }
            IsReleased = true;
        }
    }

    private sealed class FakeInput : IAudioInputReader
    {
        public Func<CancellationToken, Task<byte[]>>? Replay { get; init; }
        public Task<WaveAudio> ReadRecordingAsync(string path, CancellationToken token) =>
            Task.FromResult(new WaveAudio([0f], 16000, 1));
        public Task<byte[]> ReadReplayAsync(string path, CancellationToken token) =>
            Replay?.Invoke(token) ?? Task.FromResult<byte[]>([0, 0]);
    }

    private sealed class FakeUsageStore : IUsageStore
    {
        public string Path => "usage.json";
        public UsageDocument Document { get; private set; } = new();
        public bool FailRecording { get; init; }
        public Func<bool, CancellationToken, Task<UsageDocument>>? ChangingPreference { get; init; }
        public TaskCompletionSource PreferenceEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<UsageDocument> LoadAsync(CancellationToken token = default) => Task.FromResult(Document);
        public Task<UsageDocument> RecordAsync(UsageEntry entry, CancellationToken token = default)
        {
            if (FailRecording) { throw new IOException("unwritable usage"); }
            Document = Document with { Entries = Document.Entries.Append(entry).ToArray() };
            return Task.FromResult(Document);
        }
        public Task<UsageDocument> SetEnabledAsync(bool enabled, CancellationToken token = default)
        {
            PreferenceEntered.TrySetResult();
            return ChangingPreference?.Invoke(enabled, token) ?? Task.FromResult(Document = Document with { Enabled = enabled });
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        private readonly List<ManualTimer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            _ticks += amount.Ticks;
            foreach (var timer in _timers.ToArray()) { timer.Fire(_ticks); }
        }
        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state, TimeSpan due, TimeSpan period) : ITimer
        {
            private long _next = due == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + due.Ticks;
            private TimeSpan _period = period;
            private bool _disposed;
            public bool Change(TimeSpan next, TimeSpan repeat)
            {
                if (_disposed) { return false; }
                _next = next == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + next.Ticks;
                _period = repeat;
                return true;
            }
            public void Fire(long now)
            {
                if (_disposed || now < _next) { return; }
                _next = _period == Timeout.InfiniteTimeSpan ? long.MaxValue : now + _period.Ticks;
                callback(state);
            }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
