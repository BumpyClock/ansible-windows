using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DictationPoc.Core;

namespace DictationPoc.Tests;

public sealed class ModelDownloadManagerTests : IDisposable
{
    private static readonly byte[] Weights = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LocalVoiceModelTests", Guid.NewGuid().ToString("N"));
    private static NativeModelEntry Entry => new()
    {
        Id = "tiny", Family = "test_asr", Mode = "streaming", Preview = "live", Filename = "tiny.gguf",
        Bytes = Weights.Length, Sha256 = Convert.ToHexString(SHA256.HashData(Weights)),
        Repo = "test/models", Revision = new string('a', 40), RemoteFile = "tiny.gguf"
    };

    [Fact]
    public async Task VerifiedTransferWaitsForExplicitAtomicInstallation()
    {
        using var http = Client(_ => Response(Weights));
        await using var manager = Manager(http);
        var before = manager.Models;
        await manager.DownloadAsync("tiny");
        var ready = manager.Get("tiny");
        Assert.Equal(ModelInstallState.ReadyToInstall, ready.State);
        Assert.True(ready.HasPartial);
        Assert.False(ready.HasModelFile);
        Assert.False(File.Exists(ready.Path));
        Assert.Equal(Weights, File.ReadAllBytes(ready.Path + ".partial"));
        Assert.Equal(ModelInstallState.NotInstalled, before[0].State);
        await manager.InstallAsync("tiny");
        Assert.Equal(ModelInstallState.Installed, manager.Get("tiny").State);
        Assert.Equal(Weights, File.ReadAllBytes(ready.Path));
        Assert.False(File.Exists(ready.Path + ".partial"));
        Assert.False(File.Exists(ready.Path + ".partial.json"));
        await manager.RemoveAsync("tiny");
        Assert.False(File.Exists(ready.Path));
        Assert.Equal(ModelInstallState.NotInstalled, manager.Get("tiny").State);
    }

    [Fact]
    public async Task ShaFailureNeverPromotesWeights()
    {
        using var http = Client(_ => Response(new byte[Weights.Length]));
        await using var manager = Manager(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.DownloadAsync("tiny"));
        var snapshot = manager.Get("tiny");
        Assert.Equal(ModelInstallState.Failed, snapshot.State);
        Assert.Contains("SHA-256", snapshot.Error);
        Assert.True(snapshot.HasPartial);
        Assert.False(File.Exists(snapshot.Path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InstallAsync("tiny"));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(17)]
    public async Task TruncatedOrOversizedBodyCannotBecomeInstalled(int length)
    {
        using var http = Client(_ =>
        {
            var response = Response(Enumerable.Repeat((byte)1, length).ToArray());
            response.Content.Headers.ContentLength = Weights.Length;
            return response;
        });
        await using var manager = Manager(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.DownloadAsync("tiny"));
        Assert.Equal(ModelInstallState.Failed, manager.Get("tiny").State);
        Assert.False(File.Exists(manager.Get("tiny").Path));
    }

    [Fact]
    public async Task PauseAndResumeUseExactRangeAndStrongValidator()
    {
        var stream = new PausingStream(Weights[..4]);
        var requests = 0;
        using var http = Client(request =>
        {
            requests++;
            if (requests == 1)
                return PausingResponse(stream);
            Assert.Equal(4, Assert.Single(request.Headers.Range!.Ranges).From);
            Assert.Equal("\"revision-one\"", request.Headers.IfRange!.EntityTag!.ToString());
            return ResumedResponse();
        });
        await using var manager = Manager(http);
        var transfer = manager.DownloadAsync("tiny");
        await stream.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.PauseAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
        Assert.Equal(ModelInstallState.Paused, manager.Get("tiny").State);
        Assert.Equal(4, manager.Get("tiny").DownloadedBytes);
        Assert.Equal(Weights[..4], File.ReadAllBytes(manager.Get("tiny").Path + ".partial"));
        await manager.DownloadAsync("tiny");
        Assert.Equal(ModelInstallState.ReadyToInstall, manager.Get("tiny").State);
        await manager.InstallAsync("tiny");
        Assert.Equal(Weights, File.ReadAllBytes(manager.Get("tiny").Path));
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData("etag")]
    [InlineData("status")]
    [InlineData("range")]
    [InlineData("length")]
    public async Task InconsistentResumeResponsesPreserveThePartial(string fault)
    {
        var stream = new PausingStream(Weights[..4]);
        var requests = 0;
        using var http = Client(_ =>
        {
            if (++requests == 1) { return PausingResponse(stream); }
            var response = ResumedResponse();
            switch (fault)
            {
                case "etag": response.Headers.ETag = new EntityTagHeaderValue("\"changed\""); break;
                case "status": response.StatusCode = HttpStatusCode.OK; break;
                case "range": response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 15, 16); break;
                case "length": response.Content.Headers.ContentLength = 11; break;
            }
            return response;
        });
        await using var manager = Manager(http);
        var first = manager.DownloadAsync("tiny");
        await stream.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.PauseAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.DownloadAsync("tiny"));
        Assert.Equal(Weights[..4], File.ReadAllBytes(manager.Get("tiny").Path + ".partial"));
        Assert.False(File.Exists(manager.Get("tiny").Path));
    }

    [Fact]
    public async Task DiskAdmissionPrecedesAnyNetworkRequest()
    {
        var requests = 0;
        using var http = Client(_ => { requests++; return Response(Weights); });
        await using var manager = Manager(http, freeSpace: _ => 64L * 1024 * 1024 + Weights.Length - 1);
        await Assert.ThrowsAsync<IOException>(() => manager.DownloadAsync("tiny"));
        Assert.Equal(0, requests);
        Assert.Contains("disk space", manager.Get("tiny").Error);
        Assert.False(File.Exists(manager.Get("tiny").Path + ".partial"));
    }

    [Fact]
    public async Task UnsupportedFamilyIsListedButCannotDownload()
    {
        using var http = Client(_ => throw new InvalidOperationException("Unexpected HTTP"));
        await using var manager = new ModelDownloadManager([Entry], new HashSet<string>(), _directory, http);
        Assert.False(Assert.Single(manager.Models).Supported);
        await Assert.ThrowsAsync<NotSupportedException>(() => manager.DownloadAsync("tiny"));
    }

    [Fact]
    public async Task DiscardPartialKeepsAnInstalledCompleteFileAndSnapshot()
    {
        using var http = Client(_ => Response(Weights));
        await using var manager = Manager(http);
        await manager.DownloadAsync("tiny");
        await manager.InstallAsync("tiny");
        var path = manager.Get("tiny").Path;
        File.WriteAllBytes(path + ".partial", [99]);
        File.WriteAllText(path + ".partial.json", "obsolete metadata");
        await manager.RefreshAsync();
        Assert.True(manager.Get("tiny").HasPartial);
        await manager.DiscardPartialAsync("tiny");
        Assert.Equal(ModelInstallState.Installed, manager.Get("tiny").State);
        Assert.True(manager.Get("tiny").HasModelFile);
        Assert.False(manager.Get("tiny").HasPartial);
        Assert.Equal(Weights, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".partial"));
        Assert.False(File.Exists(path + ".partial.json"));
    }

    [Fact]
    public async Task DiscardReadyWeightsReleasesTheirReadLease()
    {
        using var http = Client(_ => Response(Weights));
        await using var manager = Manager(http);
        await manager.DownloadAsync("tiny");
        var path = manager.Get("tiny").Path;
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await File.WriteAllBytesAsync(path + ".partial", [99]);
        });
        await manager.DiscardPartialAsync("tiny");
        Assert.Equal(ModelInstallState.NotInstalled, manager.Get("tiny").State);
        Assert.False(File.Exists(path + ".partial"));
    }

    [Fact]
    public async Task RestartVerifiesACompletePartialWithoutNetwork()
    {
        using var http = Client(_ => Response(Weights));
        await using (var first = Manager(http)) { await first.DownloadAsync("tiny"); }
        using var offline = Client(_ => throw new InvalidOperationException("Unexpected HTTP"));
        await using var second = Manager(offline);
        await second.RefreshAsync();
        Assert.Equal(ModelInstallState.ReadyToInstall, second.Get("tiny").State);
        await second.InstallAsync("tiny");
        Assert.Equal(Weights, File.ReadAllBytes(second.Get("tiny").Path));
    }

    [Fact]
    public async Task DisposeCancelsAndJoinsTheOwnedTransfer()
    {
        var stream = new PausingStream(Weights[..4]);
        using var http = Client(_ => PausingResponse(stream));
        var manager = Manager(http);
        var transfer = manager.DownloadAsync("tiny");
        await stream.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
        Assert.False(manager.IsBusy);
        Assert.Equal(Weights[..4], File.ReadAllBytes(manager.Get("tiny").Path + ".partial"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.DownloadAsync("tiny"));
    }

    [Fact]
    public async Task RedirectToUnapprovedHostCannotTransferData()
    {
        var requests = 0;
        using var http = Client(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.Found)
                { Headers = { Location = new Uri("https://example.com/weights") } };
        });
        await using var manager = Manager(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.DownloadAsync("tiny"));
        Assert.Equal(1, requests);
        Assert.False(File.Exists(manager.Get("tiny").Path));
    }

    [Theory]
    [InlineData("..\\outside.gguf")]
    [InlineData("C:\\outside.gguf")]
    [InlineData(".localvoice-models.lock")]
    [InlineData("NUL.gguf")]
    public void ConstructorRejectsNonCatalogChildren(string filename)
    {
        using var http = Client(_ => Response(Weights));
        Assert.Throws<InvalidDataException>(() => new ModelDownloadManager(
            [Entry with { Filename = filename }], new HashSet<string> { "test_asr" }, _directory, http));
    }

    [Fact]
    public void ConstructorRejectsAuxiliaryFilenameCollisions()
    {
        using var http = Client(_ => Response(Weights));
        Assert.Throws<InvalidDataException>(() => new ModelDownloadManager(
            [Entry, Entry with { Id = "second", Filename = "tiny.gguf.partial" }],
            new HashSet<string> { "test_asr" }, _directory, http));
    }

    [Fact]
    public async Task ChangingDirectoryDoesNotMoveOrDeleteWeights()
    {
        using var http = Client(_ => Response(Weights));
        await using var manager = Manager(http);
        await manager.DownloadAsync("tiny");
        await manager.InstallAsync("tiny");
        var previous = manager.Get("tiny").Path;
        manager.ChangeDirectory(Path.Combine(_directory, "other"));
        Assert.Equal(ModelInstallState.NotInstalled, manager.Get("tiny").State);
        Assert.Equal(Weights, File.ReadAllBytes(previous));
        Assert.False(File.Exists(manager.Get("tiny").Path));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.RemoveAsync("unknown"));
    }

    [Fact]
    public async Task WrongLengthExistingFileIsVisibleAsFailedNotInstalled()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Combine(_directory, Entry.Filename), [1]);
        using var http = Client(_ => throw new InvalidOperationException("Unexpected HTTP"));
        await using var manager = Manager(http);
        await manager.RefreshAsync();
        Assert.Equal(ModelInstallState.Failed, manager.Get("tiny").State);
        Assert.True(manager.Get("tiny").HasModelFile);
        Assert.Contains("size", manager.Get("tiny").Error);
    }

    [Fact]
    public async Task RemoveCannotBypassAResidentReadLease()
    {
        using var http = Client(_ => Response(Weights));
        await using var manager = Manager(http);
        await manager.DownloadAsync("tiny");
        await manager.InstallAsync("tiny");
        var path = manager.Get("tiny").Path;
        using (var resident = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<IOException>(() => manager.RemoveAsync("tiny"));
            Assert.Equal(Weights, File.ReadAllBytes(path));
        }
        await manager.RemoveAsync("tiny");
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DownloadNeverOverwritesAnExistingCompleteFile()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, Entry.Filename);
        File.WriteAllBytes(path, [99]);
        using var http = Client(_ => throw new InvalidOperationException("Unexpected HTTP"));
        await using var manager = Manager(http);
        await Assert.ThrowsAsync<IOException>(() => manager.DownloadAsync("tiny"));
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(path));
        Assert.True(manager.Get("tiny").HasModelFile);
        Assert.Equal(ModelInstallState.Failed, manager.Get("tiny").State);
    }

    [Fact]
    public async Task MalformedResumeMetadataFailsBeforeHttpOrMutation()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, Entry.Filename);
        File.WriteAllBytes(path + ".partial", Weights[..4]);
        File.WriteAllText(path + ".partial.json", """{"Bytes":16,"Sha256":"wrong","Url":"https://huggingface.co"}""");
        using var http = Client(_ => throw new InvalidOperationException("Unexpected HTTP"));
        await using var manager = Manager(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.DownloadAsync("tiny"));
        Assert.Equal(Weights[..4], File.ReadAllBytes(path + ".partial"));
        Assert.True(manager.Get("tiny").HasPartial);
    }

    [Fact]
    public async Task ParallelManagerCommandsAreRejectedUntilPauseJoins()
    {
        var stream = new PausingStream(Weights[..4]);
        using var http = Client(_ => PausingResponse(stream));
        await using var manager = Manager(http);
        var transfer = manager.DownloadAsync("tiny");
        await stream.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RefreshAsync());
        Assert.Throws<InvalidOperationException>(() => manager.ChangeDirectory(Path.Combine(_directory, "other")));
        await manager.PauseAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
        await manager.DiscardPartialAsync("tiny");
        Assert.False(manager.Get("tiny").HasPartial);
    }

    private ModelDownloadManager Manager(HttpClient http, Func<string, long>? freeSpace = null) =>
        new([Entry], new HashSet<string> { "test_asr" }, _directory, http, freeSpace ?? (_ => long.MaxValue));

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(new FakeHttp(response)) { Timeout = Timeout.InfiniteTimeSpan };

    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes),
        Headers = { ETag = new EntityTagHeaderValue("\"revision-one\"") }
    };

    private static HttpResponseMessage PausingResponse(Stream stream)
    {
        var response = Response([]);
        response.Content = new StreamContent(stream);
        response.Content.Headers.ContentLength = Weights.Length;
        return response;
    }

    private static HttpResponseMessage ResumedResponse()
    {
        var response = Response(Weights[4..]);
        response.StatusCode = HttpStatusCode.PartialContent;
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(4, 15, 16);
        return response;
    }

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }

    private sealed class PausingStream(byte[] prefix) : Stream
    {
        private bool _delivered;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_delivered)
            {
                prefix.CopyTo(buffer);
                _delivered = true;
                return prefix.Length;
            }
            Blocked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, recursive: true); }
    }
}
