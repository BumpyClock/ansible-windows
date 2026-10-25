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
}
