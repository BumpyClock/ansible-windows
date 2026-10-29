using System.Security.Cryptography;
using System.Text.Json;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class ModelStoreTests : IDisposable
{
    private static readonly byte[] Weights = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AnsibleModelStoreTests", Guid.NewGuid().ToString("N"));
    private string ModelPath => Path.Combine(_directory, "tiny.gguf");
    private string Partial => ModelPath + ".partial";
    private string Metadata => ModelPath + ".partial.json";
    private string Pending => ModelPath + ".partial.json.pending";
    private static NativeModelEntry Entry => new()
    {
        Id = "tiny", Family = "test_asr", Mode = "streaming", Filename = "tiny.gguf",
        Bytes = Weights.Length, Sha256 = Convert.ToHexString(SHA256.HashData(Weights)),
        Repo = "test/models", Revision = new string('a', 40), RemoteFile = "tiny.gguf"
    };

    [Fact]
    public async Task CleanupFailureReportsCommittedWeightsAndCanBeCleanedWithoutRemovingThem()
    {
        using var store = Store();
        ModelFileSnapshot? last = null;
        store.Changed += snapshot => last = snapshot;
        await store.PrepareAsync("tiny", default);
        using (var blocked = new FileStream(Metadata, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = store.InstallPrepared("tiny", default);
            Assert.IsType<IOException>(result.CleanupError);
            Assert.Equal(ModelInstallState.Installed, last!.State);
            Assert.True(last.HasModelFile);
            Assert.True(last.HasPartial);
            Assert.Contains("Model installed", last.Error);
            Assert.Equal(Weights, File.ReadAllBytes(ModelPath));
            Assert.False(File.Exists(Partial));
        }
        await store.InspectAsync(default);
        Assert.False(File.Exists(Metadata));
        Assert.Equal(Weights, File.ReadAllBytes(ModelPath));
        Assert.Equal(ModelInstallState.Installed, last!.State);
        Assert.Null(last.Error);
    }

    [Fact]
    public async Task CancellationBeforeCommitKeepsPreparedLeaseAndAllowsRetry()
    {
        using var store = Store();
        await store.PrepareAsync("tiny", default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => store.InstallPrepared("tiny", cancellation.Token));
        Assert.False(File.Exists(ModelPath));
        Assert.Throws<IOException>(() => File.WriteAllBytes(Partial, [99]));
        Assert.Null(store.InstallPrepared("tiny", default).CleanupError);
        Assert.Equal(Weights, File.ReadAllBytes(ModelPath));
    }

    [Fact]
    public async Task CancellationAfterCommitDoesNotTurnInstalledWeightsIntoFailure()
    {
        using var store = Store();
        await store.PrepareAsync("tiny", default);
        using var cancellation = new CancellationTokenSource();
        store.Changed += snapshot =>
        {
            if (snapshot.State == ModelInstallState.Installed) { cancellation.Cancel(); }
        };
        var result = store.InstallPrepared("tiny", cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Null(result.CleanupError);
        Assert.Equal(Weights, File.ReadAllBytes(ModelPath));
        Assert.False(File.Exists(Metadata));
    }

    [Fact]
    public async Task FailedRenameRequiresReverificationBeforeRetry()
    {
        var source = new FakeSource();
        using var store = Store(source);
        ModelFileSnapshot? last = null;
        store.Changed += snapshot => last = snapshot;
        await store.PrepareAsync("tiny", default);
        using (var competingReader = new FileStream(Partial, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => store.InstallPrepared("tiny", default));
        Assert.Equal(ModelInstallState.Failed, last!.State);
        Assert.False(File.Exists(ModelPath));
        Assert.Equal(Weights, File.ReadAllBytes(Partial));
        Assert.Throws<InvalidOperationException>(() => store.InstallPrepared("tiny", default));
        await store.InspectAsync(default);
        Assert.Equal(ModelInstallState.ReadyToInstall, last.State);
        store.InstallPrepared("tiny", default);
        Assert.Single(source.Requests);
        Assert.Equal(Weights, File.ReadAllBytes(ModelPath));
    }

    [Fact]
    public async Task IndependentPreparedReadersBlockCommitUntilTheOtherStoreReleasesItsLease()
    {
        using var first = Store();
        var second = Store();
        try
        {
            await first.PrepareAsync("tiny", default);
            await second.InspectAsync(default);
            Assert.Throws<IOException>(() => first.InstallPrepared("tiny", default));
        }
        finally { second.Dispose(); }
        await first.InspectAsync(default);
        first.InstallPrepared("tiny", default);
        Assert.Equal(Weights, File.ReadAllBytes(ModelPath));
    }

    [Fact]
    public async Task IndependentStoresCannotMutateDuringAnOwnedTransfer()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource
        {
            Open = async (_, token) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The blocked source must be cancelled.");
            }
        };
        using var first = Store(source);
        using var second = Store();
        using var cancellation = new CancellationTokenSource();
        var transfer = first.PrepareAsync("tiny", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsAsync<IOException>(() => second.InspectAsync(default));
            Assert.Throws<IOException>(() => second.Remove("tiny", ModelRemovalScope.PartialDownload, default));
            Assert.False(File.Exists(Partial));
        }
        finally { cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
        await second.InspectAsync(default);
    }

    [Fact]
    public async Task FailedMetadataReplacementPreservesPreviousResumeAndPartialBytes()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Partial, Weights[..4]);
        var original = JsonSerializer.Serialize(new ModelResume(Entry.Sha256, Entry.Bytes, Entry.DownloadUri.AbsoluteUri, "\"one\""),
            ModelDownloadJsonContext.Default.ModelResume);
        File.WriteAllText(Metadata, original);
        var source = new FakeSource();
        using var store = Store(source);
        using (var blocked = new FileStream(Metadata, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => store.PrepareAsync("tiny", default));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }
        Assert.Equal(original, File.ReadAllText(Metadata));
        Assert.Equal(Weights[..4], File.ReadAllBytes(Partial));
        Assert.False(File.Exists(Pending));
        await store.PrepareAsync("tiny", default);
        Assert.Equal(new (long, string?)[] { (4, "\"one\""), (4, "\"one\"") }, source.Requests);
        store.InstallPrepared("tiny", default);
        Assert.Equal(Weights, File.ReadAllBytes(ModelPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedMetadataPublicationRemainsVisibleUntilExplicitDiscard(bool hasPartial)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Pending, "interrupted metadata");
        if (hasPartial) { File.WriteAllBytes(Partial, Weights[..4]); }
        var source = new FakeSource();
        using var store = Store(source);
        ModelFileSnapshot? last = null;
        store.Changed += snapshot => last = snapshot;
        await store.InspectAsync(default);
        Assert.True(last!.HasPartial);
        Assert.Equal(hasPartial ? ModelInstallState.Failed : ModelInstallState.NotInstalled, last.State);
        Assert.True(File.Exists(Pending));
        if (hasPartial) { Assert.Equal(Weights[..4], File.ReadAllBytes(Partial)); }
        Assert.Empty(source.Requests);
        store.Remove("tiny", ModelRemovalScope.PartialDownload, default);
        Assert.False(File.Exists(Partial));
        Assert.False(File.Exists(Pending));
        Assert.False(last.HasPartial);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(17)]
    public async Task SourceBytesStillRequirePinnedLengthAndHash(int length)
    {
        var source = new FakeSource { Open = (_, _) => Task.FromResult<Stream>(new MemoryStream(new byte[length])) };
        using var store = Store(source);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PrepareAsync("tiny", default));
        Assert.False(File.Exists(ModelPath));
        Assert.Throws<InvalidOperationException>(() => store.InstallPrepared("tiny", default));
    }

    [Fact]
    public async Task DisposingStoreReleasesPreparedLeaseWithoutDeletingThePartial()
    {
        var store = Store();
        try
        {
            await store.PrepareAsync("tiny", default);
            Assert.Throws<IOException>(() => File.WriteAllBytes(Partial, [99]));
        }
        finally { store.Dispose(); }
        using var writable = new FileStream(Partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(Weights.Length, writable.Length);
        Assert.Throws<ObjectDisposedException>(() => store.InstallPrepared("tiny", default));
    }

    [Fact]
    public void MetadataStagingNameCannotCollideWithAnotherCatalogModel()
    {
        Assert.Throws<InvalidDataException>(() => Store(catalog:
            [Entry, Entry with { Id = "collision", Filename = "tiny.gguf.partial.json.pending" }]));
    }

    private ModelStore Store(FakeSource? source = null, IReadOnlyList<NativeModelEntry>? catalog = null) =>
        new(catalog ?? [Entry], _directory, source ?? new FakeSource(), _ => long.MaxValue);

    private sealed class FakeSource : IModelSource
    {
        public List<(long Offset, string? ETag)> Requests { get; } = [];
        public Func<long, CancellationToken, Task<Stream>> Open { get; init; } =
            (offset, _) => Task.FromResult<Stream>(new MemoryStream(Weights[(int)offset..]));

        public async Task<ModelSourceRead> OpenAsync(NativeModelEntry entry, long offset, string? etag, CancellationToken token)
        {
            Assert.Equal(Entry, entry);
            Requests.Add((offset, etag));
            return new ModelSourceRead(await Open(offset, token), "\"one\"");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, recursive: true); }
    }
}
