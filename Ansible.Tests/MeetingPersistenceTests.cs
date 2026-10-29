using Ansible.Core;

namespace Ansible.Tests;

public sealed class MeetingPersistenceTests
{
    [Fact]
    public async Task ThrowingFailureObserverDoesNotStrandFlushOrShutdown()
    {
        await using var writer = new MeetingPersistence(_ => throw new IOException("Disk unavailable"),
            _ => throw new InvalidOperationException("Observer failed"));
        writer.Queue(1, new());
        var error = await Assert.ThrowsAsync<IOException>(() => writer.FlushAsync(1).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<AggregateException>(error.InnerException);
        await writer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SlowDiskKeepsOnlyLatestPendingSnapshotAndFlushJoinsIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new List<string>();
        await using var writer = new MeetingPersistence(async document =>
        {
            written.Add(document.Title);
            if (document.Title == "First") { entered.SetResult(); await release.Task; }
        }, error => throw new InvalidOperationException("Unexpected storage failure.", error));
        writer.Queue(1, new() { Title = "First" });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var revision = 2; revision <= 1000; revision++)
            writer.Queue(revision, new() { Title = revision.ToString() });
        writer.Queue(999, new() { Title = "Stale edit" });
        var flush = writer.FlushAsync(1000);
        Assert.False(flush.IsCompleted);
        Assert.Equal(new[] { "First" }, written);
        release.SetResult();
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "First", "1000" }, written);
    }

    [Fact]
    public async Task FailedWriteIsObservableAndLatestSnapshotCanBeRetried()
    {
        var attempts = 0;
        var failures = new List<Exception>();
        MeetingDocument? saved = null;
        await using var writer = new MeetingPersistence(document =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("Disk unavailable");
            saved = document;
            return Task.CompletedTask;
        }, failures.Add);
        var meeting = new MeetingDocument { Title = "Recover me" };
        writer.Queue(1, meeting);
        await Assert.ThrowsAsync<IOException>(() => writer.FlushAsync(1).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(failures);
        writer.Queue(1, meeting);
        await writer.FlushAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(meeting, saved);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CloseDrainsLatestSnapshotRejectsNewWorkAndCanBeJoinedRepeatedly()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<string>();
        await using var writer = new MeetingPersistence(async document =>
        {
            writes.Add(document.Title);
            if (writes.Count == 1) { entered.SetResult(); await release.Task; }
        }, _ => { });
        Task closing;
        Task secondClose;
        try
        {
            writer.Queue(1, new() { Title = "First" });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            writer.Queue(2, new() { Title = "Skipped" });
            writer.Queue(3, new() { Title = "Latest" });
            closing = writer.DisposeAsync().AsTask();
            secondClose = writer.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            Assert.False(secondClose.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => writer.Queue(4, new()));
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(closing, secondClose).WaitAsync(TimeSpan.FromSeconds(5));
        await writer.FlushAsync(2).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "First", "Latest" }, writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewerSuccessfulSnapshotSatisfiesSupersededFlushEvenAfterEarlierFailure(bool failFirst)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<string>();
        var failures = new List<Exception>();
        await using var writer = new MeetingPersistence(async document =>
        {
            writes.Add(document.Title);
            if (document.Title == "First")
            {
                entered.SetResult();
                await release.Task;
                if (failFirst) throw new IOException("First failed");
            }
        }, failures.Add);
        Task flush;
        try
        {
            writer.Queue(1, new() { Title = "First" });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            writer.Queue(2, new() { Title = "Skipped" });
            flush = writer.FlushAsync(2);
            writer.Queue(3, new() { Title = "Latest" });
            Assert.False(flush.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "First", "Latest" }, writes);
        Assert.Equal(failFirst ? 1 : 0, failures.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task FlushRejectsRevisionsOutsideSubmittedRange(long revision)
    {
        await using var writer = new MeetingPersistence(_ => Task.CompletedTask, _ => { });
        writer.Queue(1, new());
        await writer.FlushAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            writer.FlushAsync(revision).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ConcurrentQueueAndCloseEitherRejectsOrDrainsTheAcceptedSnapshot()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writes = new List<string>();
            await using var writer = new MeetingPersistence(document =>
            {
                writes.Add(document.Title);
                return Task.CompletedTask;
            }, _ => { });
            var admission = Task.Run(async () =>
            {
                await start.Task;
                try { writer.Queue(1, new() { Title = "Accepted" }); return true; }
                catch (ObjectDisposedException) { return false; }
            });
            var closing = Task.Run(async () => { await start.Task; await writer.DisposeAsync(); });
            start.SetResult();
            var accepted = await admission.WaitAsync(TimeSpan.FromSeconds(5));
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(accepted ? new[] { "Accepted" } : Array.Empty<string>(), writes);
        }
    }
}
