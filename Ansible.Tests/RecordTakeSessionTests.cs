using System.Diagnostics;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class RecordTakeSessionTests
{
    private static readonly AudioModel Nemotron = new() { Id = AudioModelDefaults.RecognitionModelId, Family = "nemotron_asr", Preview = "live" };
    private readonly long Origin = Stopwatch.GetTimestamp();
    private long At(double seconds) => Origin + (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public async Task NativeMarkersUseTheAudioAnchorAndPreserveSpeech()
    {
        var factory = new TakeFactory();
        var take = new RecordTakeSession(factory, new(1, Origin, true), Nemotron, () => { }, CancellationToken.None);
        await factory.Started.Task;
        take.SetAudioOrigin(At(1));
        factory.Burst!(new(1, At(1.5)));
        var result = new RecognitionResult("click this button", "click this button")
        {
            WordTimings = new(SpeechWordTimingSource.NativeTokenEmission,
                [new(0, 5, 0, 1000), new(6, 4, 16000, 17000), new(11, 6, 32000, 33000)])
        };
        var payload = await take.PrepareAsync(result);
        Assert.Equal("click [ink 1 @0:01] this button", payload.Text);
        Assert.Equal("click this button", result.SpeechText);
        Assert.True(payload.IsRecordTake);
        Assert.True(factory.Capture.IsReleased);
    }

    [Fact]
    public async Task MissingAudioAnchorAbstainsFromWordPlacement()
    {
        var factory = new TakeFactory();
        var take = new RecordTakeSession(factory, new(1, Origin, true), Nemotron, () => { }, CancellationToken.None);
        await factory.Started.Task;
        factory.Burst!(new(1, At(7)));
        var payload = await take.PrepareAsync(new("click", "click")
        {
            WordTimings = new(SpeechWordTimingSource.NativeTokenEmission, [new(0, 5, 0, 1000)])
        });
        Assert.Equal("click\n\nInk timeline\n[ink 1 @0:07]", payload.Text);
        Assert.Contains(take.Warnings, value => value.Contains("no usable word timing"));
    }

    [Theory]
    [InlineData("kroko_asr")]
    [InlineData("vibevoice_asr_streaming")]
    public async Task LiveModelsUseObservedPartialLengthAndLabelApproximation(string family)
    {
        var factory = new TakeFactory();
        var take = new RecordTakeSession(factory, new(1, Origin, true), new() { Id = "other", Family = family, Preview = "live" },
            () => { }, CancellationToken.None);
        await factory.Started.Task;
        take.Update(new("click", false, "click"));
        factory.Burst!(new(1, At(7)));
        var payload = await take.PrepareAsync(new("click this button", "click this button"));
        Assert.Equal("click [ink 1 @0:07, approximate] this button", payload.Text);
    }

    [Fact]
    public async Task SamePositionMarkersStayInChronologicalOrder()
    {
        var factory = new TakeFactory();
        var take = new RecordTakeSession(factory, new(1, Origin, true), new() { Id = "kroko", Family = "kroko_asr" },
            () => { }, CancellationToken.None);
        await factory.Started.Task;
        take.Update(new("click", false, "click"));
        var inkAt = Stopwatch.GetTimestamp();
        factory.Burst!(new(1, inkAt));
        factory.Burst!(new(2, inkAt));
        var text = (await take.PrepareAsync(new("click", "click"))).Text;
        Assert.StartsWith("click [ink 1 @", text);
        Assert.Contains(", approximate] [ink 2 @", text);
        Assert.EndsWith(", approximate]", text);
    }

    [Fact]
    public async Task DelayedInkObservationUsesThePreviewThatExistedAtTheInkTime()
    {
        var factory = new TakeFactory();
        var take = new RecordTakeSession(factory, new(1, Origin, true), new() { Id = "kroko", Family = "kroko_asr" },
            () => { }, CancellationToken.None);
        await factory.Started.Task;
        take.Update(new("click", false, "click"));
        var inkAt = Stopwatch.GetTimestamp();
        take.Update(new("click this button", false, "click this button"));
        factory.Burst!(new(1, inkAt));
        var text = (await take.PrepareAsync(new("click this button", "click this button"))).Text;
        Assert.StartsWith("click [ink 1 @", text);
        Assert.EndsWith(", approximate] this button", text);
    }

    [Fact]
    public async Task FinalOnlyModelUsesTimelineAndKeepsBurstNumbersBeyondPictureCap()
    {
        var factory = new TakeFactory();
        var take = new RecordTakeSession(factory, new(1, Origin, true), new() { Id = "moonshine", Family = "moonshine_asr", Preview = "final-only" },
            () => { }, CancellationToken.None);
        await factory.Started.Task;
        for (var n = 1; n <= 12; n++) factory.Burst!(new(n, At(n * 2)));
        var payload = await take.PrepareAsync(new("look here", "look here"));
        Assert.StartsWith("look here\n\nInk timeline\n[ink 1 @0:02]", payload.Text);
        Assert.Contains("[ink 12 @0:24]", payload.Text);
        Assert.DoesNotContain("approximate", payload.Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VideoLinkSettingOnlyControlsPastedText(bool include)
    {
        var path = Path.Combine(Path.GetTempPath(), "record take", "silent.mp4");
        var factory = new TakeFactory { Capture = new() { Media = new([new("final.png", null, TimeSpan.Zero)], path, [], VideoStartOffset: TimeSpan.FromSeconds(.125)) } };
        var take = new RecordTakeSession(factory, new(1, Origin, include), Nemotron, () => { }, CancellationToken.None);
        var payload = await take.PrepareAsync(new("hello", "hello"));
        Assert.Equal(path, payload.VideoPath);
        Assert.Contains("Frames in order: final screen.", payload.Text);
        Assert.Equal(include, payload.Text.Contains("Video: file:///"));
        if (include) Assert.Contains("video begins @0.125 s on the take clock", payload.Text);
    }

    [Fact]
    public async Task MediaFailureRetainsAuthoritativeTextAndSurfacesWarning()
    {
        var factory = new TakeFactory { Capture = new() { StopError = new IOException("encoder failed") } };
        var take = new RecordTakeSession(factory, new(1, Origin, true), Nemotron, () => { }, CancellationToken.None);
        var payload = await take.PrepareAsync(new("hello", "hello"));
        Assert.Equal("hello", payload.Text);
        Assert.Empty(payload.Frames);
        Assert.Null(payload.VideoPath);
        Assert.Contains(take.Warnings, value => value.Contains("encoder failed"));
        Assert.True(factory.Capture.IsReleased);
    }

    [Fact]
    public async Task StopWaitsForStartupAndDisposesItsCaptureExactlyOnce()
    {
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new TakeFactory { Startup = admitted.Task };
        var take = new RecordTakeSession(factory, new(1, Origin, true), Nemotron, () => { }, CancellationToken.None);
        await factory.Started.Task;
        var stop = take.StopAsync(true);
        Assert.False(stop.IsCompleted);
        admitted.SetResult();
        var result = await stop;
        Assert.True(result.Discarded);
        Assert.Same(stop, take.StopAsync(true));
        Assert.True(factory.Capture.IsReleased);
        Assert.Equal(1, factory.Capture.Stops);
    }

    [Fact]
    public async Task NativeDiscardSuppressesThePreparedBundle()
    {
        var discards = 0;
        var factory = new TakeFactory();
        var take = new RecordTakeSession(factory, new(1, Origin, true), Nemotron, () => discards++, CancellationToken.None);
        await factory.Started.Task;
        factory.Discard!();
        await Assert.ThrowsAsync<OperationCanceledException>(() => take.PrepareAsync(new("hello", "hello")));
        Assert.Equal(1, discards);
        Assert.True(factory.Capture.IsReleased);
    }

    [Fact]
    public async Task DiscardAfterFinishRetractsMediaThatWasAlreadyStopped()
    {
        var factory = new TakeFactory();
        var take = new RecordTakeSession(factory, new(1, Origin, true), Nemotron, () => { }, CancellationToken.None);
        await take.StopAsync(false);
        Assert.False(factory.Capture.Discarded);
        Assert.True((await take.StopAsync(true)).Discarded);
        Assert.True(factory.Capture.Discarded);
    }

    internal sealed class TakeFactory : IRecordTakeFactory
    {
        public TakeCapture Capture { get; init; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Startup { get; init; } = Task.CompletedTask;
        public Action<RecordInkBurst>? Burst { get; private set; }
        public Action? Discard { get; private set; }
        public RecordTakeOptions Snapshot(bool includeVideoLink) => new(1, Stopwatch.GetTimestamp(), includeVideoLink);
        public async Task<IRecordTakeCapture> StartAsync(RecordTakeOptions options, Action<RecordInkBurst> onBurst,
            Action onDiscard, CancellationToken cancellationToken)
        {
            Burst = onBurst;
            Discard = onDiscard;
            Started.TrySetResult();
            await Startup;
            return Capture;
        }
    }

    internal sealed class TakeCapture : IRecordTakeCapture
    {
        public bool IsReleased { get; private set; }
        public bool FailRelease { get; set; }
        public int Stops { get; private set; }
        public bool Discarded { get; private set; }
        public Exception? StopError { get; init; }
        public RecordMediaResult Media { get; init; } = new([], null, []);
        public Task<RecordMediaResult> StopAsync(bool discard)
        {
            Stops++;
            Discarded |= discard;
            return StopError is { } error ? Task.FromException<RecordMediaResult>(error) : Task.FromResult(Media with { Discarded = discard });
        }
        public ValueTask DisposeAsync()
        {
            if (FailRelease) throw new IOException("capture still owns native resources");
            IsReleased = true;
            return ValueTask.CompletedTask;
        }
    }
}
