using Ansible.Core;

namespace Ansible.Tests;

public sealed class CoalescingSnapshotWriterTests
{
    [Fact]
    public async Task BlockedSynchronousWriteDoesNotBlockAdmissionAndCloseJoinsLatest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var saved = new List<int>();
        await using var writer = new CoalescingSnapshotWriter<int>(value =>
        {
            saved.Add(value);
            if (value == 1) { entered.SetResult(); release.Wait(); }
            return Task.CompletedTask;
        }, (_, _) => { });
        Task closing;
        try
        {
            writer.Queue(1, 1);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() =>
            {
                for (var revision = 2; revision <= 1000; revision++) writer.Queue(revision, revision);
                writer.Queue(999, -1);
            }).WaitAsync(TimeSpan.FromSeconds(5));
            writer.Complete();
            writer.Complete();
            Assert.Throws<ObjectDisposedException>(() => writer.Queue(1001, 1001));
            closing = writer.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            Assert.Equal(new[] { 1 }, saved);
        }
        finally { release.Set(); }
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        await writer.FlushAsync(1000).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 1, 1000 }, saved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAttemptIsReportedAndSameRevisionCanBeRetried(bool asynchronous)
    {
        var failure = new InvalidOperationException("Write failed");
        var attempts = 0;
        var results = new List<(long Revision, Exception? Error)>();
        await using var writer = new CoalescingSnapshotWriter<string>(_ =>
        {
            if (Interlocked.Increment(ref attempts) > 1) return Task.CompletedTask;
            if (asynchronous) return Task.FromException(failure);
            throw failure;
        }, (revision, error) => results.Add((revision, error)));
        writer.Queue(4, "snapshot");
        var error = await Assert.ThrowsAsync<IOException>(() => writer.FlushAsync(4).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(failure, error.InnerException);
        writer.Queue(4, "snapshot");
        await writer.FlushAsync(4).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, attempts);
        Assert.Equal(4, results[0].Revision);
        Assert.Same(failure, results[0].Error);
        Assert.Equal((4L, (Exception?)null), results[1]);
        writer.Queue(4, "already saved");
        await writer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrowingCompletionCannotStrandShutdownOrMisreportASuccessfulWrite(bool failedWrite)
    {
        var writeError = new IOException("Write failed");
        var observerError = new InvalidOperationException("Observer failed");
        var saved = false;
        await using var writer = new CoalescingSnapshotWriter<int>(_ =>
        {
            if (failedWrite) return Task.FromException(writeError);
            saved = true;
            return Task.CompletedTask;
        }, (_, _) => throw observerError);
        writer.Queue(1, 1);
        if (failedWrite)
        {
            var failure = await Assert.ThrowsAsync<IOException>(() => writer.FlushAsync(1).WaitAsync(TimeSpan.FromSeconds(5)));
            var aggregate = Assert.IsType<AggregateException>(failure.InnerException);
            Assert.Equal(new Exception[] { writeError, observerError }, aggregate.InnerExceptions);
        }
        else await writer.FlushAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        await writer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(!failedWrite, saved);
    }

    [Fact]
    public async Task CloseJoinsCompletionNotificationAndDoesNotConvertFailureToSuccess()
    {
        var notifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var notifications = 0;
        await using var writer = new CoalescingSnapshotWriter<int>(_ => Task.FromException(new IOException("Failed")), (_, _) =>
        {
            notifying.SetResult();
            release.Wait();
            notifications++;
        });
        Task closing;
        Task flushing;
        try
        {
            writer.Queue(1, 1);
            await notifying.Task.WaitAsync(TimeSpan.FromSeconds(5));
            flushing = writer.FlushAsync(1);
            closing = writer.DisposeAsync().AsTask();
            Assert.False(flushing.IsCompleted);
            Assert.False(closing.IsCompleted);
        }
        finally { release.Set(); }
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => flushing.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<IOException>(() => writer.FlushAsync(1).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task EmptyWriterCanCloseAndRejectsFutureWorkAndFlush()
    {
        await using var writer = new CoalescingSnapshotWriter<int>(_ => Task.CompletedTask, (_, _) => { });
        await writer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await writer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<ObjectDisposedException>(() => writer.Queue(0, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => writer.FlushAsync(0));
    }

    [Fact]
    public async Task NegativeRevisionIsRejectedWithoutInvokingPersistence()
    {
        var writes = 0;
        await using var writer = new CoalescingSnapshotWriter<int>(_ =>
        {
            writes++;
            return Task.CompletedTask;
        }, (_, _) => { });
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Queue(-1, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => writer.FlushAsync(-1));
        await writer.DisposeAsync();
        Assert.Equal(0, writes);
    }
}
