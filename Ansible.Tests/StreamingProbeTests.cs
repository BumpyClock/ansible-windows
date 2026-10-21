using System.Threading.Channels;
using Ansible.Core;
using Ansible.Probe;

namespace Ansible.Tests;

public sealed class StreamingProbeTests
{
    private static readonly AudioModel Model = new() { Id = "test", Mode = "streaming" };
    private static readonly RecognitionResult Speech = new("actual speech", "actual speech");
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" \t", false)]
    [InlineData("final speech", true)]
    public async Task EmptyUnknownAndFinalUpdatesNeverBecomeSpeechPreviews(string? speech, bool final)
    {
        var clock = new ManualClock();
        var engine = new FakeEngine(async (audio, progress, token) =>
        {
            progress.Report(new TranscriptUpdate("nonempty display text", final, speech));
            await ConsumeAsync(audio, token);
            progress.Report(new TranscriptUpdate("nonempty display text", final, speech));
            return Speech;
        });

        var (_, measurement) = await MeasureAsync(engine, clock, Pcm(3840));

        Assert.Null(measurement.FirstSpeechPreview);
        Assert.Equal(0, measurement.PreviewsBeforeEndOfInput);
    }

    [Fact]
    public async Task FirstSpeechPreviewUsesStreamStartAndOnlyLiveSpeechCountsBeforeInputEnd()
    {
        var clock = new ManualClock();
        var engine = new FakeEngine(async (audio, progress, token) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(5));
            progress.Report(new TranscriptUpdate("one", false, "one"));
            clock.Advance(TimeSpan.FromMilliseconds(5));
            progress.Report(new TranscriptUpdate("two", false, "two"));
            await ConsumeAsync(audio, token);
            clock.Advance(TimeSpan.FromMilliseconds(7));
            progress.Report(new TranscriptUpdate("late preview", false, "late preview"));
            progress.Report(new TranscriptUpdate("final speech", true, "final speech"));
            return Speech;
        });

        var (_, measurement) = await MeasureAsync(engine, clock, Pcm(3840));

        Assert.Equal(TimeSpan.FromMilliseconds(5), measurement.FirstSpeechPreview);
        Assert.Equal(2, measurement.PreviewsBeforeEndOfInput);
        Assert.Equal(TimeSpan.FromMilliseconds(120), measurement.EndOfInput);
        Assert.Equal(TimeSpan.FromMilliseconds(7), measurement.FinalResultLatency);
        Assert.Equal(TimeSpan.FromMilliseconds(7), measurement.SourceDurationToFinal);
        Assert.Equal(TimeSpan.FromMilliseconds(127), measurement.FullElapsed);
    }

    [Fact]
    public async Task FirstNonFinalSpeechAfterInputEndIsTimedButNotCountedAsLive()
    {
        var clock = new ManualClock();
        var engine = new FakeEngine(async (audio, progress, token) =>
        {
            await ConsumeAsync(audio, token);
            clock.Advance(TimeSpan.FromMilliseconds(15));
            progress.Report(new TranscriptUpdate("late speech", false, "late speech"));
            return Speech;
        });

        var (_, measurement) = await MeasureAsync(engine, clock, Pcm(3840));

        Assert.Equal(TimeSpan.FromMilliseconds(135), measurement.FirstSpeechPreview);
        Assert.Equal(0, measurement.PreviewsBeforeEndOfInput);
        Assert.True(measurement.FirstSpeechPreview > measurement.EndOfInput);
    }

    [Theory]
    [InlineData(640, 20)]
    [InlineData(1280, 40)]
    [InlineData(2560, 80)]
    [InlineData(3200, 100)]
    public async Task InputEndsAtLastPacketAcceptanceWithoutATrailingDelay(int bytes, int endMilliseconds)
    {
        var clock = new ManualClock();
        var pcm = Pcm(bytes);
        byte[]? received = null;
        var engine = new FakeEngine(async (audio, _, token) =>
        {
            received = await ConsumeAsync(audio, token);
            clock.Advance(TimeSpan.FromMilliseconds(25));
            return Speech;
        });

        var (transcript, measurement) = await MeasureAsync(engine, clock, pcm);

        Assert.Same(Speech, transcript);
        Assert.Equal(pcm, received);
        Assert.Equal(TimeSpan.FromMilliseconds(endMilliseconds), measurement.EndOfInput);
        Assert.Equal(TimeSpan.FromMilliseconds(25), measurement.FinalResultLatency);
        Assert.Equal(TimeSpan.FromMilliseconds(25), measurement.SourceDurationToFinal);
        Assert.Equal(TimeSpan.FromMilliseconds(endMilliseconds + 25), measurement.FullElapsed);
        Assert.Equal((bytes + 1279) / 1280, clock.Delays.Count);
        Assert.Equal(endMilliseconds, clock.Delays.Sum(delay => delay.TotalMilliseconds));
        Assert.Equal(TimeSpan.FromSeconds(Math.Min(bytes, 1280) / 32000.0), clock.Delays[0]);
        Assert.Equal(1, engine.JoinedStreams);
    }

    [Theory]
    [InlineData(60, 480)]
    [InlineData(300, 660)]
    public async Task BackpressureDoesNotShiftPacketDeadlinesOrHideCaptureBacklog(
        int blockedMilliseconds, int endMilliseconds)
    {
        var clock = new ManualClock();
        var releaseConsumer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pcm = Pcm(1280 * 12);
        byte[]? received = null;
        var engine = new FakeEngine(async (audio, _, token) =>
        {
            await releaseConsumer.Task.WaitAsync(token);
            received = await ConsumeAsync(audio, token);
            clock.Advance(TimeSpan.FromMilliseconds(25));
            return Speech;
        });
        var measuring = MeasureAsync(engine, clock, pcm);
        try
        {
            // Eight channel slots are full; the ninth packet is waiting for acceptance at 360 ms.
            Assert.False(measuring.IsCompleted);
            Assert.Equal(TimeSpan.FromMilliseconds(360), clock.Elapsed());
            clock.Advance(TimeSpan.FromMilliseconds(blockedMilliseconds));
            releaseConsumer.SetResult();
            var (_, measurement) = await measuring;

            Assert.Equal(pcm, received);
            Assert.Equal(TimeSpan.FromMilliseconds(endMilliseconds), measurement.EndOfInput);
            Assert.Equal(TimeSpan.FromMilliseconds(25), measurement.FinalResultLatency);
            Assert.Equal(TimeSpan.FromMilliseconds(endMilliseconds + 25 - 480), measurement.SourceDurationToFinal);
            var expectedDelays = Enumerable.Repeat(40d, 9);
            if (blockedMilliseconds == 60) { expectedDelays = expectedDelays.Concat([20d, 40]); }
            Assert.Equal(expectedDelays, clock.Delays.Select(delay => delay.TotalMilliseconds));
        }
        finally { releaseConsumer.TrySetResult(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EarlyCompletionOrFailureCancelsPendingInputAndJoinsRecognition(
        bool failRecognition, bool blockedWrite)
    {
        var clock = new ManualClock();
        var result = new TaskCompletionSource<RecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("recognition failed");
        var engine = new FakeEngine((_, _, _) => result.Task);
        Func<TimeSpan, CancellationToken, Task> delay = blockedWrite
            ? clock.DelayAsync : (_, token) => Task.Delay(Timeout.Infinite, token);
        var measuring = MeasureAsync(engine, clock, Pcm(1280 * 12), delay);
        Assert.False(measuring.IsCompleted);
        if (failRecognition) { result.SetException(failure); }
        else { result.SetResult(Speech); }

        if (failRecognition)
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => measuring));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => measuring);

        Assert.Equal(1, engine.JoinedStreams);
        Assert.True(Assert.Single(engine.Tokens).IsCancellationRequested);
        using var timeout = new CancellationTokenSource(TestTimeout);
        var buffered = await ConsumeAsync(Assert.Single(engine.Inputs), timeout.Token);
        Assert.Equal(blockedWrite ? 1280 * 8 : 0, buffered.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProducerFailureWaitsForRecognitionCleanupAndRetainsCleanupFailure(bool failCleanup)
    {
        var clock = new ManualClock();
        var producerFailure = new IOException("input failed");
        var cleanupFailure = new IOException("recognition cleanup failed");
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakeEngine(async (audio, _, token) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                return Speech;
            }
            finally
            {
                while (audio.TryRead(out var packet)) { }
                cleanupStarted.SetResult();
                await releaseCleanup.Task;
                if (failCleanup) { throw cleanupFailure; }
            }
        });
        var measuring = MeasureAsync(engine, clock, Pcm(2560),
            (_, _) => Task.FromException(producerFailure));
        try
        {
            await cleanupStarted.Task.WaitAsync(TestTimeout);
            Assert.False(measuring.IsCompleted);
            Assert.Equal(0, engine.JoinedStreams);
            Assert.True(Assert.Single(engine.Tokens).IsCancellationRequested);
            await Assert.Single(engine.Inputs).Completion.WaitAsync(TestTimeout);
        }
        finally { releaseCleanup.TrySetResult(); }

        if (failCleanup)
        {
            var error = await Assert.ThrowsAsync<AggregateException>(() => measuring);
            Assert.Equal([producerFailure, cleanupFailure], error.InnerExceptions);
        }
        else
            Assert.Same(producerFailure, await Assert.ThrowsAsync<IOException>(() => measuring));
        Assert.Equal(1, engine.JoinedStreams);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public async Task NonemptyDisplayWithoutAuthoritativeFinalSpeechIsRejected(string? speech)
    {
        var clock = new ManualClock();
        var engine = new FakeEngine(async (audio, _, token) =>
        {
            await ConsumeAsync(audio, token);
            return new RecognitionResult("presentation label", speech);
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => MeasureAsync(engine, clock, Pcm(1280)));

        Assert.Equal(1, engine.JoinedStreams);
        Assert.True(Assert.Single(engine.Tokens).IsCancellationRequested);
    }

    [Fact]
    public async Task RepeatedAttemptsReuseEngineButNotInputCancellationOrPreviewState()
    {
        var clock = new ManualClock();
        var attempts = 0;
        var engine = new FakeEngine(async (audio, progress, token) =>
        {
            if (++attempts == 1)
            {
                clock.Advance(TimeSpan.FromMilliseconds(5));
                progress.Report(new TranscriptUpdate("first attempt", false, "first attempt"));
            }
            await ConsumeAsync(audio, token);
            clock.Advance(TimeSpan.FromMilliseconds(15));
            return Speech;
        });
        var (_, first) = await MeasureAsync(engine, clock, Pcm(3840));
        clock = new ManualClock();
        var (_, second) = await MeasureAsync(engine, clock, Pcm(3840));

        Assert.Equal(TimeSpan.FromMilliseconds(5), first.FirstSpeechPreview);
        Assert.Equal(1, first.PreviewsBeforeEndOfInput);
        Assert.Null(second.FirstSpeechPreview);
        Assert.Equal(0, second.PreviewsBeforeEndOfInput);
        Assert.Equal(TimeSpan.FromMilliseconds(120), second.EndOfInput);
        Assert.Equal(TimeSpan.FromMilliseconds(135), second.FullElapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(15), second.SourceDurationToFinal);
        Assert.Equal(2, engine.JoinedStreams);
        Assert.NotSame(engine.Inputs[0], engine.Inputs[1]);
        Assert.NotEqual(engine.Tokens[0], engine.Tokens[1]);
        Assert.All(engine.Tokens, token => Assert.True(token.IsCancellationRequested));
    }

    [Fact]
    public void SummariesRetainMissingValuesAndMedianMinMaxWithoutPercentiles()
    {
        Assert.Equal("latency: none (n=0)", StreamingProbe.FormatSummary("latency", [], "ms"));
        Assert.Equal($"latency: median={7d:F1}, min={7d:F1}, max={7d:F1} ms (n=1)",
            StreamingProbe.FormatSummary("latency", [7], "ms"));
        Assert.Equal($"latency: median={5d:F1}, min={1d:F1}, max={9d:F1} ms (n=3)",
            StreamingProbe.FormatSummary("latency", [9, 1, 5], "ms"));
        Assert.Equal($"latency: median={4d:F1}, min={1d:F1}, max={9d:F1} ms (n=4)",
            StreamingProbe.FormatSummary("latency", [9, 1, 5, 3], "ms"));
    }

    private static Task<(RecognitionResult Transcript, StreamMeasurement Measurement)> MeasureAsync(
        FakeEngine engine, ManualClock clock, byte[] pcm,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        StreamingProbe.MeasureAsync(engine, Model, pcm, new RecognitionOptions(),
            clock.Elapsed, delay ?? clock.DelayAsync).WaitAsync(TestTimeout);

    private static byte[] Pcm(int length) =>
        Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();

    private static async Task<byte[]> ConsumeAsync(ChannelReader<byte[]> audio, CancellationToken token)
    {
        var received = new List<byte>();
        await foreach (var packet in audio.ReadAllAsync(token))
            received.AddRange(packet);
        return received.ToArray();
    }

    private sealed class ManualClock
    {
        private long _ticks;
        internal List<TimeSpan> Delays { get; } = [];
        internal TimeSpan Elapsed() => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
        internal Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Delays.Add(duration);
            Advance(duration);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEngine(
        Func<ChannelReader<byte[]>, IProgress<TranscriptUpdate>, CancellationToken, Task<RecognitionResult>> stream)
        : IRecognitionEngine
    {
        public string ModelsDirectory => "models";
        public string Version => "managed test";
        internal List<ChannelReader<byte[]>> Inputs { get; } = [];
        internal List<CancellationToken> Tokens { get; } = [];
        internal int JoinedStreams;
        public async Task<RecognitionResult> StreamAsync(AudioModel model, ChannelReader<byte[]> audio,
            RecognitionOptions options, IProgress<TranscriptUpdate>? progress, CancellationToken token,
            Action? onReady = null)
        {
            Inputs.Add(audio);
            Tokens.Add(token);
            try { return await stream(audio, progress!, token); }
            finally { Interlocked.Increment(ref JoinedStreams); }
        }
        public Task<IReadOnlyList<AudioModel>> ConnectAsync(CancellationToken token) => throw new NotSupportedException();
        public Task PrepareAsync(AudioModel model, RecognitionOptions options, CancellationToken token) => throw new NotSupportedException();
        public Task UnloadAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<RecognitionResult> TranscribeAsync(AudioModel model, WaveAudio audio, RecognitionOptions options,
            IProgress<TranscriptUpdate>? progress, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
