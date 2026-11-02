using System.Threading.Channels;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class MeetingCaptureFakeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ReleaseIsVisibleBeforeSynchronousAudioCompletion(bool dispose) => Task.Run(async () =>
    {
        var channel = Channel.CreateUnbounded<byte[]>(new() { AllowSynchronousContinuations = true });
        IMeetingCapture capture = new FakeMeetingCapture(channel);
        var observed = new TaskCompletionSource<(bool HasAudio, bool Released, int Thread)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var read = capture.Audio.WaitToReadAsync().GetAwaiter();
        Assert.False(read.IsCompleted);
        read.UnsafeOnCompleted(() =>
            observed.SetResult((read.GetResult(), capture.IsReleased, Environment.CurrentManagedThreadId)));

        var stoppingThread = Environment.CurrentManagedThreadId;
        if (dispose) await capture.DisposeAsync();
        else await capture.StopAsync();

        var result = await observed.Task.WaitAsync(Timeout);
        Assert.Equal(stoppingThread, result.Thread);
        Assert.False(result.HasAudio);
        Assert.True(result.Released);
        await capture.Audio.Completion.WaitAsync(Timeout);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedResourcesFaultAudioInsteadOfReportingCleanCompletion(bool dispose)
    {
        var capture = new FakeMeetingCapture { BlockRelease = true };
        var read = capture.Audio.WaitToReadAsync().AsTask();

        if (dispose) await capture.DisposeAsync();
        else await capture.StopAsync();

        Assert.False(capture.IsReleased);
        await Assert.ThrowsAsync<InvalidOperationException>(() => read.WaitAsync(Timeout));
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.Audio.Completion.WaitAsync(Timeout));
    }

    [Fact]
    public async Task ConcurrentStopsAndDisposeJoinOneReleaseBarrier()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = 0;
        var capture = new FakeMeetingCapture
        {
            StopBarrier = barrier,
            Stopping = () => Interlocked.Increment(ref stopping)
        };
        var entered = 0;
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stops = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            var stop = capture.StopAsync();
            if (Interlocked.Increment(ref entered) == 32) allEntered.TrySetResult();
            await stop;
        })).ToArray();
        Task? disposal = null;
        try
        {
            await allEntered.Task.WaitAsync(Timeout);
            await capture.StopEntered.Task.WaitAsync(Timeout);
            disposal = capture.DisposeAsync().AsTask();

            Assert.False(disposal.IsCompleted);
            Assert.All(stops, stop => Assert.False(stop.IsCompleted));
            Assert.False(capture.Audio.Completion.IsCompleted);
            Assert.False(capture.IsReleased);
            Assert.Equal(1, capture.StopCalls);
            Assert.Equal(1, Volatile.Read(ref stopping));
        }
        finally
        {
            barrier.TrySetResult();
            await Task.WhenAll(stops).WaitAsync(Timeout);
            if (disposal is not null) await disposal.WaitAsync(Timeout);
        }

        Assert.True(capture.IsReleased);
        await capture.Audio.Completion.WaitAsync(Timeout);
        await capture.StopAsync();
        await capture.DisposeAsync();
        Assert.Equal(1, capture.StopCalls);
        Assert.Equal(1, Volatile.Read(ref stopping));
    }

    [Fact]
    public async Task RetainedReleaseCanBeRetriedWithoutClearingTheOriginalAudioFailure()
    {
        var stopping = 0;
        var capture = new FakeMeetingCapture
        {
            BlockRelease = true,
            Stopping = () => Interlocked.Increment(ref stopping)
        };
        await capture.StopAsync();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => capture.Audio.Completion.WaitAsync(Timeout));
        await capture.StopAsync();
        await capture.DisposeAsync();
        Assert.False(capture.IsReleased);
        Assert.Equal(1, capture.StopCalls);

        capture.BlockRelease = false;
        await capture.DisposeAsync();
        await capture.StopAsync();
        await capture.DisposeAsync();

        Assert.True(capture.IsReleased);
        Assert.Equal(2, capture.StopCalls);
        Assert.Equal(2, Volatile.Read(ref stopping));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => capture.Audio.Completion.WaitAsync(Timeout)));
    }

    [Fact]
    public async Task SharedFactoryPreservesPrebufferedAudioUntilDrainedAfterStop()
    {
        byte[] pcm = [1, 0, 2, 0];
        var source = new FakeMeetingCapture();
        source.Send(pcm);
        var factory = new FakeMeetingCaptureFactory { Create = () => source };
        await using var capture = await factory.StartAsync(new(), CancellationToken.None);
        Assert.False(capture.IsReleased);
        Assert.False(capture.Audio.Completion.IsCompleted);

        await capture.StopAsync();

        Assert.True(capture.IsReleased);
        Assert.False(capture.Audio.Completion.IsCompleted);
        Assert.Equal(pcm, await capture.Audio.ReadAsync());
        await capture.Audio.Completion.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ClockTracksPublishedAudioUntilAProducerSetsAnExplicitPosition()
    {
        await using var capture = new FakeMeetingCapture();
        Assert.Equal(0, capture.ClockSamplePosition);
        await Task.Run(() => capture.Send(320));
        Assert.Equal(320, (await capture.Audio.ReadAsync()).Length);
        Assert.Equal(160, capture.ClockSamplePosition);
        Assert.Equal(0.01, capture.CapturedSeconds);

        const long position = 0x123456789abcdef;
        await Task.Run(() =>
        {
            capture.ClockSamplePosition = position;
            capture.Send(640);
        });

        Assert.Equal(640, (await capture.Audio.ReadAsync()).Length);
        Assert.Equal(position, capture.ClockSamplePosition);
        Assert.Equal(0.03, capture.CapturedSeconds);
        await capture.StopAsync();
        Assert.Equal(position, capture.ClockSamplePosition);
    }
}
