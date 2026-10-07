using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansible.Core;

public enum ModelInstallState { NotInstalled, Installed, Downloading, Paused, Verifying, ReadyToInstall, Failed }

public sealed record ModelDownloadSnapshot(
    NativeModelEntry Model, bool Supported, ModelInstallState State, string Path,
    long DownloadedBytes = 0, double BytesPerSecond = 0, string? Error = null, bool HasPartial = false,
    bool HasModelFile = false)
{
    public double Progress => Math.Clamp(100.0 * DownloadedBytes / Model.Bytes, 0, 100);
}

public sealed class ModelDownloadManager : IAsyncDisposable
{
    private const long DiskReserve = 64L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly HttpClient _http;
    private readonly Func<string, long> _freeSpace;
    private readonly Dictionary<string, ModelDownloadSnapshot> _models;
    private readonly Dictionary<string, FileStream> _verified = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cancellation;
    private Task? _work;
    private bool _closing;
    private string _directory;

    public ModelDownloadManager(
        IReadOnlyList<NativeModelEntry> catalog, IReadOnlySet<string> supportedFamilies,
        string directory, HttpClient http)
        : this(catalog, supportedFamilies, directory, http,
            path => new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace) { }

    internal ModelDownloadManager(
        IReadOnlyList<NativeModelEntry> catalog, IReadOnlySet<string> supportedFamilies,
        string directory, HttpClient http, Func<string, long> freeSpace)
    {
        NativeModelCatalog.ValidateEntries(catalog);
        _directory = Path.GetFullPath(directory);
        _http = http;
        _freeSpace = freeSpace;
        _models = catalog.ToDictionary(entry => entry.Id, entry => new ModelDownloadSnapshot(
            entry, supportedFamilies.Contains(entry.Family), ModelInstallState.NotInstalled,
            Path.Combine(_directory, entry.Filename)), StringComparer.OrdinalIgnoreCase);
    }

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public event Action? Changed;
    public IReadOnlyList<ModelDownloadSnapshot> Models
    {
        get { lock (_gate) { return Array.AsReadOnly(_models.Values.ToArray()); } }
    }
    public bool IsBusy { get { lock (_gate) { return _work is not null; } } }
    public string DirectoryPath { get { lock (_gate) { return _directory; } } }

    public Task RefreshAsync(CancellationToken token = default) => Start(cancellation =>
    {
        using var directoryLock = LockDirectory();
        foreach (var snapshot in Models)
        {
            cancellation.ThrowIfCancellationRequested();
            var entry = snapshot.Model;
            try
            {
                CheckPaths(snapshot.Path);
                if (File.Exists(snapshot.Path))
                {
                    Update(entry.Id, ModelInstallState.Verifying);
                    using var file = OpenRead(snapshot.Path);
                    NativeModelIntegrity.Verify(file, entry, cancellation);
                    Update(entry.Id, ModelInstallState.Installed, entry.Bytes,
                        hasPartial: File.Exists(snapshot.Path + ".partial") || File.Exists(snapshot.Path + ".partial.json"));
                }
                else if (File.Exists(snapshot.Path + ".partial"))
                {
                    var length = new FileInfo(snapshot.Path + ".partial").Length;
                    _ = ReadResume(snapshot.Path, entry);
                    if (length > entry.Bytes)
                        throw new InvalidDataException("Partial download exceeds its pinned size. Confirm cleanup before retrying.");
                    if (length == entry.Bytes)
                    {
                        VerifyPartial(snapshot, cancellation);
                    }
                    else { Update(entry.Id, ModelInstallState.Paused, length, hasPartial: true); }
                }
                else { Update(entry.Id, ModelInstallState.NotInstalled, hasPartial: File.Exists(snapshot.Path + ".partial.json")); }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Update(entry.Id, File.Exists(snapshot.Path) ? ModelInstallState.Failed : ModelInstallState.Paused,
                    snapshot.DownloadedBytes,
                    error: "Verification paused. Verify the files again before using this model.",
                    hasPartial: File.Exists(snapshot.Path + ".partial"));
                throw;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                Update(entry.Id, ModelInstallState.Failed, error: error.Message,
                    hasPartial: File.Exists(snapshot.Path + ".partial") || File.Exists(snapshot.Path + ".partial.json"));
            }
        }
        return Task.CompletedTask;
    }, token);

    public Task DownloadAsync(string id, CancellationToken token = default) => Start(
        cancellation => DownloadCoreAsync(Get(id), cancellation), token);

    // Call only within DictationSession.MaintainModelsAsync; hashing and transfer happen before this short commit.
    public Task InstallAsync(string id, CancellationToken token = default) => Start(cancellation =>
    {
        using var directoryLock = LockDirectory();
        var snapshot = Get(id);
        cancellation.ThrowIfCancellationRequested();
        CheckPaths(snapshot.Path);
        if (snapshot.State != ModelInstallState.ReadyToInstall || !_verified.TryGetValue(id, out var lease))
            throw new InvalidOperationException("Download and verify the pinned weights before installation.");
        if (File.Exists(snapshot.Path))
            throw new IOException("A model file already exists. Confirm removal before installing these weights.");
        lease.Dispose();
        _verified.Remove(id);
        try
        {
            File.Move(snapshot.Path + ".partial", snapshot.Path, overwrite: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Update(id, ModelInstallState.Failed, snapshot.DownloadedBytes, error: error.Message, hasPartial: true);
            throw;
        }
        Update(id, ModelInstallState.Installed, snapshot.Model.Bytes);
        File.Delete(snapshot.Path + ".partial.json");
        return Task.CompletedTask;
    }, token);

    // The session maintenance owner must release native read leases before calling this command.
    public Task RemoveAsync(string id, CancellationToken token = default) => Start(cancellation =>
    {
        using var directoryLock = LockDirectory();
        var snapshot = Get(id);
        cancellation.ThrowIfCancellationRequested();
        CheckPaths(snapshot.Path);
        File.Delete(snapshot.Path);
        Update(id, File.Exists(snapshot.Path + ".partial") ? ModelInstallState.Paused : ModelInstallState.NotInstalled,
            File.Exists(snapshot.Path + ".partial") ? new FileInfo(snapshot.Path + ".partial").Length : 0,
            hasPartial: File.Exists(snapshot.Path + ".partial") || File.Exists(snapshot.Path + ".partial.json"));
        return Task.CompletedTask;
    }, token);

    public Task DiscardPartialAsync(string id, CancellationToken token = default) => Start(cancellation =>
    {
        using var directoryLock = LockDirectory();
        var snapshot = Get(id);
        cancellation.ThrowIfCancellationRequested();
        CheckPaths(snapshot.Path);
        ReleaseVerification(id);
        File.Delete(snapshot.Path + ".partial");
        File.Delete(snapshot.Path + ".partial.json");
        if (File.Exists(snapshot.Path))
        {
            Update(id, snapshot.State == ModelInstallState.Installed ? ModelInstallState.Installed : ModelInstallState.Failed,
                snapshot.State == ModelInstallState.Installed ? snapshot.Model.Bytes : 0,
                error: snapshot.State == ModelInstallState.Installed ? null :
                    snapshot.Error ?? "The complete file remains. Verify installed files before using it.");
        }
        else { Update(id, ModelInstallState.NotInstalled); }
        return Task.CompletedTask;
    }, token);

    public void ChangeDirectory(string directory)
    {
        lock (_gate)
        {
            RequireIdle();
            foreach (var lease in _verified.Values) { lease.Dispose(); }
            _verified.Clear();
            _directory = Path.GetFullPath(directory);
            foreach (var id in _models.Keys)
            {
                var model = _models[id];
                _models[id] = model with
                {
                    Path = Path.Combine(_directory, model.Model.Filename), State = ModelInstallState.NotInstalled,
                    DownloadedBytes = 0, BytesPerSecond = 0, Error = null, HasPartial = false, HasModelFile = false
                };
            }
        }
        Changed?.Invoke();
    }

    public async Task PauseAsync()
    {
        Task? work;
        lock (_gate) { work = _work; _cancellation?.Cancel(); }
        if (work is not null)
        {
            try { await work; }
            catch (OperationCanceledException) { /* The operation publishes Paused before it completes. */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? work;
        lock (_gate) { _closing = true; work = _work; _cancellation?.Cancel(); }
        try
        {
            if (work is not null)
            {
                try { await work; }
                catch (OperationCanceledException) when (_closing) { }
            }
        }
        finally
        {
            foreach (var lease in _verified.Values) { lease.Dispose(); }
            _verified.Clear();
        }
    }

    private Task Start(Func<CancellationToken, Task> action, CancellationToken token)
    {
        lock (_gate)
        {
            RequireIdle();
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var cancellation = _cancellation;
            _work = Task.Run(async () =>
            {
                try { await action(cancellation.Token); }
                finally
                {
                    lock (_gate) { _work = null; _cancellation = null; cancellation.Dispose(); }
                    Changed?.Invoke();
                }
            });
            Changed?.Invoke();
            return _work;
        }
    }

    private async Task DownloadCoreAsync(ModelDownloadSnapshot snapshot, CancellationToken token)
    {
        var entry = snapshot.Model;
        var partial = snapshot.Path + ".partial";
        long downloaded = 0;
        try
        {
            if (!snapshot.Supported)
                throw new NotSupportedException("The compiled native backend does not support this model family.");
            var uri = entry.DownloadUri;
            using var directoryLock = LockDirectory();
            ReleaseVerification(entry.Id);
            CheckPaths(snapshot.Path);
            if (File.Exists(snapshot.Path))
                throw new IOException("A model file already exists. Verify it, or confirm removal before re-downloading.");
            downloaded = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (downloaded > entry.Bytes)
                throw new InvalidDataException("Partial download exceeds its pinned size. Confirm cleanup before retrying.");
            var resume = downloaded > 0 ? ReadResume(snapshot.Path, entry) : new ModelResume(entry.Sha256, entry.Bytes, uri.AbsoluteUri, null);
            if (_freeSpace(_directory) < checked(entry.Bytes - downloaded + DiskReserve))
                throw new IOException("Insufficient free disk space for the remaining weights and 64 MiB of reserve.");
            Update(entry.Id, ModelInstallState.Downloading, downloaded, hasPartial: downloaded > 0);
            if (downloaded < entry.Bytes)
            {
                using var response = await GetResponseAsync(uri, downloaded, resume.ETag, token);
                ValidateResponse(response, entry, downloaded, resume.ETag);
                var tag = response.Headers.ETag is { IsWeak: false } etag ? etag.ToString() : null;
                resume = resume with { ETag = tag ?? resume.ETag };
                WriteResume(snapshot.Path, resume);
                await using var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (output.Length != downloaded)
                    throw new IOException("Partial download changed before it could be resumed.");
                output.Position = downloaded;
                await using var input = await response.Content.ReadAsStreamAsync(token);
                var buffer = new byte[128 * 1024];
                var started = Stopwatch.GetTimestamp();
                var initial = downloaded;
                var reported = started;
                while (true)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(30));
                    int count;
                    try { count = await input.ReadAsync(buffer, deadline.Token); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    { throw new TimeoutException("The model download stalled for 30 seconds. Retry to resume."); }
                    if (count == 0) { break; }
                    if (count > entry.Bytes - downloaded)
                        throw new InvalidDataException("The download exceeds the pinned byte length.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    downloaded += count;
                    var now = Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(reported, now).TotalMilliseconds >= 150 || downloaded == entry.Bytes)
                    {
                        var elapsed = Stopwatch.GetElapsedTime(started, now).TotalSeconds;
                        Update(entry.Id, ModelInstallState.Downloading, downloaded, (downloaded - initial) / Math.Max(elapsed, 0.001),
                            hasPartial: true);
                        reported = now;
                    }
                }
                await output.FlushAsync(token);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            VerifyPartial(snapshot, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Update(entry.Id, ModelInstallState.Paused, downloaded, hasPartial: File.Exists(partial));
            throw;
        }
        catch (Exception error)
        {
            Update(entry.Id, ModelInstallState.Failed, downloaded, error: error.Message,
                hasPartial: File.Exists(partial) || File.Exists(snapshot.Path + ".partial.json"));
            throw;
        }
    }

    private void VerifyPartial(ModelDownloadSnapshot snapshot, CancellationToken token)
    {
        ReleaseVerification(snapshot.Model.Id);
        Update(snapshot.Model.Id, ModelInstallState.Verifying, snapshot.Model.Bytes, hasPartial: true);
        var file = OpenRead(snapshot.Path + ".partial");
        try
        {
            NativeModelIntegrity.Verify(file, snapshot.Model, token);
            token.ThrowIfCancellationRequested();
            _verified.Add(snapshot.Model.Id, file);
            Update(snapshot.Model.Id, ModelInstallState.ReadyToInstall, snapshot.Model.Bytes, hasPartial: true);
        }
        catch { file.Dispose(); throw; }
    }

    private void ReleaseVerification(string id)
    {
        if (_verified.Remove(id, out var lease)) { lease.Dispose(); }
    }

    private async Task<HttpResponseMessage> GetResponseAsync(Uri uri, long offset, string? etag, CancellationToken token)
    {
        for (var redirects = 0; redirects <= 8; redirects++)
        {
            ValidateDownloadUri(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                if (etag is not null) { request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(etag)); }
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TimeoutException("The model server did not respond within 30 seconds. Retry to resume."); }
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) { throw new InvalidDataException("The download redirect has no destination."); }
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new InvalidDataException("The model download exceeded eight redirects.");
    }

    internal static void ValidateDownloadUri(Uri uri)
    {
        var host = uri.IdnHost;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 ||
            !(host == "huggingface.co" || host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase) ||
              host == "hf.co" || host.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Model downloads require HTTPS on an approved Hugging Face host.");
    }

    private static void ValidateResponse(HttpResponseMessage response, NativeModelEntry entry, long offset, string? etag)
    {
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentEncoding.Any(value => value != "identity"))
            throw new InvalidDataException("Encoded model responses cannot be verified or resumed.");
        if (offset > 0)
        {
            var range = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || range is null || range.Unit != "bytes" ||
                range.From != offset || range.To != entry.Bytes - 1 || range.Length != entry.Bytes)
                throw new InvalidDataException("The server did not honor the exact resume range. Keep the partial file or confirm cleanup.");
            if (etag is not null && response.Headers.ETag?.ToString() != etag)
                throw new InvalidDataException("The server validator changed. Confirm partial cleanup before restarting.");
        }
        else if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidDataException("The server returned an unexpected response for a new download.");
        if (response.Content.Headers.ContentLength is { } length && length != entry.Bytes - offset)
            throw new InvalidDataException("The server response length does not match the pinned model.");
    }

    private static ModelResume ReadResume(string path, NativeModelEntry entry)
    {
        var metadataPath = path + ".partial.json";
        if (!File.Exists(metadataPath) || new FileInfo(metadataPath).Length > 16 * 1024)
            throw new InvalidDataException("Partial download has no valid resume metadata. Confirm cleanup before retrying.");
        var resume = JsonSerializer.Deserialize(File.ReadAllText(metadataPath), ModelDownloadJsonContext.Default.ModelResume);
        if (resume is null || resume.Bytes != entry.Bytes || resume.Sha256 != entry.Sha256 ||
            resume.Url != entry.DownloadUri.AbsoluteUri ||
            resume.ETag is not null && (!EntityTagHeaderValue.TryParse(resume.ETag, out var tag) || tag.IsWeak))
            throw new InvalidDataException("Partial download metadata does not match the pinned catalog. Confirm cleanup before retrying.");
        return resume;
    }

    private static void WriteResume(string path, ModelResume resume) =>
        File.WriteAllText(path + ".partial.json", JsonSerializer.Serialize(resume, ModelDownloadJsonContext.Default.ModelResume));

    private FileStream LockDirectory()
    {
        var existing = _directory;
        while (!Directory.Exists(existing)) { existing = Path.GetDirectoryName(existing) ?? throw new IOException("No model directory root exists."); }
        NativeModelIntegrity.RejectLinks(existing);
        Directory.CreateDirectory(_directory);
        NativeModelIntegrity.RejectLinks(_directory);
        var path = Path.Combine(_directory, ".localvoice-models.lock");
        if (File.Exists(path)) { NativeModelIntegrity.RejectLinks(path); }
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static void CheckPaths(string path)
    {
        NativeModelIntegrity.RejectLinks(Path.GetDirectoryName(path)!);
        foreach (var candidate in new[] { path, path + ".partial", path + ".partial.json" })
            if (File.Exists(candidate)) { NativeModelIntegrity.RejectLinks(candidate); }
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        128 * 1024, FileOptions.SequentialScan);
    public ModelDownloadSnapshot Get(string id)
    {
        lock (_gate) { return _models.TryGetValue(id, out var model) ? model : throw new ArgumentException("Choose a pinned catalog model.", nameof(id)); }
    }
    private void RequireIdle()
    {
        ObjectDisposedException.ThrowIf(_closing, this);
        if (_work is not null) { throw new InvalidOperationException("Pause or finish the current model operation first."); }
    }
    private void Update(string id, ModelInstallState state, long bytes = 0, double rate = 0, string? error = null,
        bool hasPartial = false)
    {
        lock (_gate) { _models[id] = _models[id] with
            { State = state, DownloadedBytes = bytes, BytesPerSecond = rate, Error = error, HasPartial = hasPartial,
              HasModelFile = File.Exists(_models[id].Path) }; }
        Changed?.Invoke();
    }
}

internal sealed record ModelResume(string Sha256, long Bytes, string Url, string? ETag);

[JsonSourceGenerationOptions(AllowDuplicateProperties = false)]
[JsonSerializable(typeof(ModelResume))]
internal partial class ModelDownloadJsonContext : JsonSerializerContext;
