using Ansible;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class CaptureClipboardProtectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AnsibleClipboardTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RestartProtectsNativeFileDropBeforeFirstTakeOrCleanup()
    {
        var previousStore = new CaptureStore(_root);
        var picture = await CreatePictureAsync(previousStore);
        Directory.SetCreationTimeUtc(Path.GetDirectoryName(picture)!, DateTime.UtcNow.AddDays(-8));
        var restartedStore = new CaptureStore(_root);
        var clipboard = new FakeClipboard { Files = [picture] };
        var protection = new CaptureClipboardProtection(restartedStore, clipboard);

        await protection.RefreshAsync();
        var cleanup = await restartedStore.CleanupAsync();

        Assert.Equal(1, clipboard.Inspections);
        Assert.Equal(1, cleanup.ProtectedTakes);
        Assert.Equal(0, cleanup.DeletedTakes);
        Assert.True(File.Exists(picture));
    }

    [Fact]
    public async Task MultipleDirectTakesAreProtectedButOutsideAndNestedPathsAreIgnored()
    {
        var store = new CaptureStore(_root);
        var first = await CreatePictureAsync(store);
        var second = await CreatePictureAsync(store);
        var ignored = await CreatePictureAsync(store);
        var clipboard = new FakeClipboard
        {
            Files = [first, first, second, Path.Combine(Path.GetDirectoryName(ignored)!, "nested", "image.png"),
                Path.Combine(_root, "image.png"), Path.Combine(_root + "-outside", "take", "image.png"),
                Path.Combine(_root, "..", "outside", "image.png"), "relative/image.png", "bad\0path.png"]
        };
        var protection = new CaptureClipboardProtection(store, clipboard);

        await protection.RefreshAsync();
        var cleanup = await store.DeleteAllAsync();

        Assert.Equal(2, cleanup.ProtectedTakes);
        Assert.Equal(1, cleanup.DeletedTakes);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.False(File.Exists(ignored));
    }

    [Fact]
    public async Task ClipboardOverwriteReleasesAllPreviousLeases()
    {
        var store = new CaptureStore(_root);
        var first = await CreatePictureAsync(store);
        var second = await CreatePictureAsync(store);
        var clipboard = new FakeClipboard { Files = [first, second] };
        var protection = new CaptureClipboardProtection(store, clipboard);
        await protection.RefreshAsync();
        clipboard.Replace([second]);

        await protection.RefreshAsync();
        Assert.Equal(1, (await store.DeleteAllAsync()).DeletedTakes);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
        clipboard.Replace([]); // Native clipboard now has no CF_HDROP, for example after copying text.
        await protection.RefreshAsync();
        Assert.Equal(1, (await store.DeleteAllAsync()).DeletedTakes);
        Assert.False(File.Exists(second));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSequenceDefersCleanupAndRejectsCandidateLeases(bool changeAfterProtection)
    {
        var store = new CaptureStore(_root);
        var oldPicture = await CreatePictureAsync(store);
        var candidate = await CreatePictureAsync(store);
        var clipboard = new FakeClipboard { Files = [oldPicture] };
        var protection = new CaptureClipboardProtection(store, clipboard);
        await protection.RefreshAsync();
        clipboard.Replace([candidate]);
        if (changeAfterProtection)
        {
            clipboard.BeforeSequenceRead = () =>
            {
                // The second sample occurs after candidate leases were acquired.
                if (++clipboard.SequenceReads == 2) clipboard.Sequence++;
            };
        }
        else clipboard.AfterInspection = () => clipboard.Sequence++;
        var cleanupRan = false;

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await protection.RefreshAsync();
            cleanupRan = true;
            await store.DeleteAllAsync();
        });

        Assert.False(cleanupRan);
        Assert.True(File.Exists(candidate));
        Assert.False(await store.DeleteTakeAsync(Path.GetDirectoryName(oldPicture)!));
        Assert.True(await store.DeleteTakeAsync(Path.GetDirectoryName(candidate)!));
        clipboard.AfterInspection = null;
        clipboard.BeforeSequenceRead = null;
        clipboard.Replace([]);
        await protection.RefreshAsync();
        Assert.Equal(1, (await store.DeleteAllAsync()).DeletedTakes);
    }

    [Fact]
    public async Task UnreadableClipboardDefersCleanupWithoutReleasingExistingProtection()
    {
        var store = new CaptureStore(_root);
        var picture = await CreatePictureAsync(store);
        var clipboard = new FakeClipboard { Files = [picture] };
        var protection = new CaptureClipboardProtection(store, clipboard);
        await protection.RefreshAsync();
        clipboard.Failure = new IOException("Clipboard busy.");

        await Assert.ThrowsAsync<IOException>(protection.RefreshAsync);

        Assert.False(await store.DeleteTakeAsync(Path.GetDirectoryName(picture)!));
    }

    [Fact]
    public async Task PublicationOwnsTemporaryLeasesAndItsJoinWaitsForWorkerExit()
    {
        var store = new CaptureStore(_root);
        var first = await CreatePictureAsync(store);
        var second = await CreatePictureAsync(store);
        var protection = new CaptureClipboardProtection(store, new FakeClipboard());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finish = new ManualResetEventSlim();
        var frames = new[] { new RecordFrame(first, null, TimeSpan.Zero), new RecordFrame(second, null, TimeSpan.Zero) };
        var worker = protection.PublishAsync(frames, pictures =>
        {
            Assert.Equal(frames, pictures);
            entered.SetResult();
            if (!finish.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test publication did not finish.");
        });
        var join = Task.WhenAll(Task.CompletedTask, worker);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(worker.IsCompleted);
            Assert.False(join.IsCompleted);
            var cleanup = await store.DeleteAllAsync();
            Assert.Equal(2, cleanup.ProtectedTakes);
            Assert.Equal(0, cleanup.DeletedTakes);
        }
        finally { finish.Set(); }
        await join;

        Assert.Equal(2, (await store.DeleteAllAsync()).DeletedTakes);
    }

    [Fact]
    public async Task PublicationHandsOffToClipboardLeaseBeforeTemporaryLeaseEnds()
    {
        var store = new CaptureStore(_root);
        var picture = await CreatePictureAsync(store);
        var clipboard = new FakeClipboard { Files = [picture] };
        var protection = new CaptureClipboardProtection(store, clipboard);

        await protection.PublishAsync([new(picture, null, TimeSpan.Zero)], frames =>
        {
            var clipboardLease = protection.ProtectPicturesAsync(frames).GetAwaiter().GetResult();
            protection.RememberAsync(clipboardLease).GetAwaiter().GetResult();
        });

        Assert.Equal(1, (await store.DeleteAllAsync()).ProtectedTakes);
        clipboard.Replace([]);
        await protection.RefreshAsync();
        Assert.Equal(1, (await store.DeleteAllAsync()).DeletedTakes);
    }

    [Fact]
    public async Task FailedPublicationReleasesItsTemporaryLeaseWhenJoined()
    {
        var store = new CaptureStore(_root);
        var picture = await CreatePictureAsync(store);
        var protection = new CaptureClipboardProtection(store, new FakeClipboard());

        await Assert.ThrowsAsync<IOException>(() => protection.PublishAsync(
            [new(picture, null, TimeSpan.Zero)], _ => throw new IOException("Publish failed.")));

        Assert.Equal(1, (await store.DeleteAllAsync()).DeletedTakes);
    }

    [Fact]
    public async Task RememberedPublicationIsReinspectedEvenIfPublisherSampledAnOverwrite()
    {
        var store = new CaptureStore(_root);
        var published = await CreatePictureAsync(store);
        var replacement = await CreatePictureAsync(store);
        var clipboard = new FakeClipboard { Files = [replacement] };
        var protection = new CaptureClipboardProtection(store, clipboard);
        var lease = await protection.ProtectPicturesAsync([new(published, null, TimeSpan.Zero)]);
        await protection.RememberAsync(lease);

        await protection.RefreshAsync();
        var cleanup = await store.DeleteAllAsync();

        Assert.Equal(1, cleanup.ProtectedTakes);
        Assert.Equal(1, cleanup.DeletedTakes);
        Assert.False(File.Exists(published));
        Assert.True(File.Exists(replacement));
    }

    private static async Task<string> CreatePictureAsync(CaptureStore store)
    {
        await using var take = await store.CreateTakeAsync();
        var path = Path.Combine(take.Directory, "summary.png");
        await File.WriteAllTextAsync(path, "test picture");
        return path;
    }

    private sealed class FakeClipboard : ICaptureClipboard
    {
        internal uint Sequence = 1;
        internal IReadOnlyList<string> Files = [];
        internal int Inspections;
        internal int SequenceReads;
        internal Action? AfterInspection;
        internal Action? BeforeSequenceRead;
        internal Exception? Failure;

        public uint GetSequenceNumber()
        {
            BeforeSequenceRead?.Invoke();
            return Sequence;
        }

        public IReadOnlyList<string> ReadFileDrop()
        {
            Inspections++;
            if (Failure is { } error) throw error;
            var snapshot = Files;
            AfterInspection?.Invoke();
            return snapshot;
        }

        internal void Replace(IReadOnlyList<string> files)
        {
            Files = files;
            Sequence++;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
