using Ansible.Core;

namespace Ansible.Tests;

public sealed class CaptureStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AnsibleCaptureTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CleanupDeletesExpiredTakesButPreservesActiveWriters()
    {
        var store = new CaptureStore(_root);
        var lease = await store.CreateTakeAsync();
        await File.WriteAllTextAsync(Path.Combine(lease.Directory, "video.mp4"), "1234");
        Directory.SetCreationTimeUtc(lease.Directory, DateTime.UtcNow.AddDays(-8));
        var result = await store.CleanupAsync();
        Assert.Equal(4, result.BytesRetained);
        Assert.Equal(1, result.ProtectedTakes);
        Assert.Equal(0, result.DeletedTakes);
        await lease.DisposeAsync();
        Assert.Equal(1, (await store.CleanupAsync()).DeletedTakes);
        Assert.False(Directory.Exists(lease.Directory));
    }

    [Fact]
    public async Task IndependentResultAndClipboardLeasesProtectUntilBothReleased()
    {
        var store = new CaptureStore(_root);
        var result = await store.CreateTakeAsync();
        await File.WriteAllTextAsync(Path.Combine(result.Directory, "image.png"), "123");
        var clipboard = await store.ProtectAsync(result.Directory);
        await result.DisposeAsync();
        Assert.Equal(1, (await store.DeleteAllAsync()).ProtectedTakes);
        Assert.True(Directory.Exists(clipboard.Directory));
        await clipboard.DisposeAsync();
        await clipboard.DisposeAsync();
        Assert.Equal(1, (await store.DeleteAllAsync()).DeletedTakes);
        Assert.Equal(0, await store.GetSizeAsync());
    }

    [Fact]
    public async Task FreshUnprotectedTakeSurvivesNormalCleanup()
    {
        var store = new CaptureStore(_root);
        await using (var lease = await store.CreateTakeAsync())
            await File.WriteAllTextAsync(Path.Combine(lease.Directory, "video.mp4"), "12345");
        Assert.Equal(0, (await store.CleanupAsync()).DeletedTakes);
        Assert.Equal(5, await store.GetSizeAsync());
        Assert.Equal(1, (await store.DeleteAllAsync()).DeletedTakes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedTakeDoesNotPreventDeletingLaterTakes(bool deleteAll)
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new CaptureStore(_root);
        var locked = await store.CreateTakeAsync();
        var lockedFile = Path.Combine(locked.Directory, "take.mp4");
        await File.WriteAllTextAsync(lockedFile, "1234");
        Directory.SetCreationTimeUtc(locked.Directory, DateTime.UtcNow.AddDays(-9));
        await locked.DisposeAsync();
        var later = await store.CreateTakeAsync();
        await File.WriteAllTextAsync(Path.Combine(later.Directory, "take.mp4"), "56789");
        Directory.SetCreationTimeUtc(later.Directory, DateTime.UtcNow.AddDays(-8));
        await later.DisposeAsync();

        using (var reader = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = deleteAll ? await store.DeleteAllAsync() : await store.CleanupAsync();
            Assert.Equal(1, result.FailedTakes);
            Assert.Equal(1, result.DeletedTakes);
            Assert.Equal(0, result.ProtectedTakes);
            Assert.Equal(4, result.BytesRetained);
            Assert.True(File.Exists(lockedFile));
            Assert.False(Directory.Exists(later.Directory));
        }
        var retried = deleteAll ? await store.DeleteAllAsync() : await store.CleanupAsync();
        Assert.Equal(0, retried.FailedTakes);
        Assert.Equal(1, retried.DeletedTakes);
        Assert.Equal(0, retried.BytesRetained);
    }

    [Fact]
    public async Task DiscardDeletesOnlyAnUnleasedDirectTake()
    {
        var store = new CaptureStore(_root);
        var lease = await store.CreateTakeAsync();
        Assert.False(await store.DeleteTakeAsync(lease.Directory));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteTakeAsync(_root));
        await lease.DisposeAsync();
        Assert.True(await store.DeleteTakeAsync(lease.Directory));
        Assert.False(Directory.Exists(lease.Directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
