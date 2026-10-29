using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansible.Core;

internal sealed record ModelFileSnapshot(
    NativeModelEntry Model, ModelInstallState State, string Path, long DownloadedBytes = 0,
    double BytesPerSecond = 0, string? Error = null, bool HasPartial = false, bool HasModelFile = false);

internal sealed record ModelInstallResult(Exception? CleanupError = null);
internal enum ModelRemovalScope { InstalledWeights, PartialDownload }

// The manager serializes operations and joins them before disposing this root-bound store.
internal sealed class ModelStore : IDisposable
{
    private const long DiskReserve = 64L * 1024 * 1024;
    internal const string PendingMetadataSuffix = ".partial.json.pending";
    private readonly IModelSource _source;
    private readonly Func<string, long> _freeSpace;
    private readonly LocalDiagnosticLog? _diagnostics;
    private readonly Dictionary<string, ModelFileSnapshot> _models;
    private readonly Dictionary<string, FileStream> _verified = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public ModelStore(IReadOnlyList<NativeModelEntry> catalog, string directory, IModelSource source,
        Func<string, long> freeSpace, LocalDiagnosticLog? diagnostics = null)
    {
        NativeModelCatalog.ValidateEntries(catalog);
        DirectoryPath = Path.GetFullPath(directory);
        _source = source;
        _freeSpace = freeSpace;
        _diagnostics = diagnostics;
        _models = catalog.ToDictionary(entry => entry.Id, entry => new ModelFileSnapshot(
            entry, ModelInstallState.NotInstalled, Path.Combine(DirectoryPath, entry.Filename)),
            StringComparer.OrdinalIgnoreCase);
    }

    public string DirectoryPath { get; }
    public event Action<ModelFileSnapshot>? Changed;

    public Task InspectAsync(CancellationToken token)
    {
        using var directoryLock = LockDirectory();
        foreach (var snapshot in _models.Values.ToArray())
        {
            token.ThrowIfCancellationRequested();
            var entry = snapshot.Model;
            try
            {
                ReleaseVerification(entry.Id);
                CheckPaths(snapshot.Path);
                if (File.Exists(snapshot.Path))
                {
                    Update(entry.Id, ModelInstallState.Verifying);
                    using var file = OpenRead(snapshot.Path);
                    NativeModelIntegrity.Verify(file, entry, token);
                    if (File.Exists(snapshot.Path + ".partial"))
                        Update(entry.Id, ModelInstallState.Installed, entry.Bytes);
                    else
                        CompleteInstallation(entry.Id);
                }
                else if (File.Exists(snapshot.Path + ".partial"))
                {
                    var length = new FileInfo(snapshot.Path + ".partial").Length;
                    _ = ReadResume(snapshot.Path, entry);
                    if (length > entry.Bytes)
                        throw new InvalidDataException("Partial download exceeds its pinned size. Confirm cleanup before retrying.");
                    if (length == entry.Bytes) { VerifyPartial(snapshot, token); }
                    else { Update(entry.Id, ModelInstallState.Paused, length); }
                }
                else { Update(entry.Id, ModelInstallState.NotInstalled); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Update(entry.Id, File.Exists(snapshot.Path) ? ModelInstallState.Failed : ModelInstallState.Paused,
                    snapshot.DownloadedBytes, error: "Verification paused. Verify the files again before using this model.");
                throw;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                Update(entry.Id, ModelInstallState.Failed, error: error.Message);
            }
        }
        return Task.CompletedTask;
    }

    public async Task PrepareAsync(string id, CancellationToken token)
    {
        var snapshot = Get(id);
        var entry = snapshot.Model;
        var partial = snapshot.Path + ".partial";
        long downloaded = 0;
        long? resumeOffset = null;
        int? httpStatus = null;
        var generation = _diagnostics?.CurrentGeneration;
        var started = generation is null ? 0 : Stopwatch.GetTimestamp();
        var attempt = generation is null ? Guid.Empty : Guid.NewGuid();
        var transferComplete = false;
        Exception? transferFailure = null;
        try
        {
            var uri = entry.DownloadUri;
            using var directoryLock = LockDirectory();
            token.ThrowIfCancellationRequested();
            ReleaseVerification(id);
            CheckPaths(snapshot.Path);
            if (File.Exists(snapshot.Path))
                throw new IOException("A model file already exists. Verify it, or confirm removal before re-downloading.");
            downloaded = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            resumeOffset = downloaded;
            if (downloaded > entry.Bytes)
                throw new InvalidDataException("Partial download exceeds its pinned size. Confirm cleanup before retrying.");
            var resume = downloaded > 0 ? ReadResume(snapshot.Path, entry) : new ModelResume(entry.Sha256, entry.Bytes, uri.AbsoluteUri, null);
            if (_freeSpace(DirectoryPath) < checked(entry.Bytes - downloaded + DiskReserve))
                throw new IOException("Insufficient free disk space for the remaining weights and 64 MiB of reserve.");
            Update(id, ModelInstallState.Downloading, downloaded);
            if (downloaded < entry.Bytes)
            {
                await using var response = await _source.OpenAsync(entry, downloaded, resume.ETag, token);
                httpStatus = response.HttpStatus;
                resume = resume with { ETag = response.ETag ?? resume.ETag };
                WriteResume(snapshot.Path, resume);
                await using var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (output.Length != downloaded)
                    throw new IOException("Partial download changed before it could be resumed.");
                output.Position = downloaded;
                var buffer = new byte[128 * 1024];
                var transferStarted = Stopwatch.GetTimestamp();
                var initial = downloaded;
                var reported = transferStarted;
                while (true)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(30));
                    int count;
                    try { count = await response.Content.ReadAsync(buffer, deadline.Token); }
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
                        var elapsed = Stopwatch.GetElapsedTime(transferStarted, now).TotalSeconds;
                        Update(id, ModelInstallState.Downloading, downloaded, (downloaded - initial) / Math.Max(elapsed, 0.001));
                        reported = now;
                    }
                }
                await output.FlushAsync(token);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            LogDownload(generation, attempt, DownloadDiagnosticStage.Transfer, entry, started, null,
                resumeOffset, downloaded, httpStatus);
            transferComplete = true;
            VerifyPartial(snapshot, token, attempt, generation);
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested)
        {
            transferFailure = error;
            Update(id, ModelInstallState.Paused, downloaded);
            throw;
        }
        catch (Exception error)
        {
            transferFailure = error;
            Update(id, ModelInstallState.Failed, downloaded, error: error.Message);
            throw;
        }
        finally
        {
            if (!transferComplete)
                LogDownload(generation, attempt, DownloadDiagnosticStage.Transfer, entry, started, transferFailure,
                    resumeOffset, downloaded, httpStatus);
        }
    }

    public ModelInstallResult InstallPrepared(string id, CancellationToken token)
    {
        var generation = _diagnostics?.CurrentGeneration;
        var started = generation is null ? 0 : Stopwatch.GetTimestamp();
        var attempt = generation is null ? Guid.Empty : Guid.NewGuid();
        NativeModelEntry? model = null;
        Exception? failure = null;
        try
        {
            using var directoryLock = LockDirectory();
            var snapshot = Get(id);
            model = snapshot.Model;
            token.ThrowIfCancellationRequested();
            CheckPaths(snapshot.Path);
            if (!_verified.ContainsKey(id))
                throw new InvalidOperationException("Download and verify the pinned weights before installation.");
            if (File.Exists(snapshot.Path))
                throw new IOException("A model file already exists. Confirm removal before installing these weights.");
            ReleaseVerification(id);
            try { File.Move(snapshot.Path + ".partial", snapshot.Path, overwrite: false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Update(id, ModelInstallState.Failed, snapshot.DownloadedBytes, error: error.Message);
                throw;
            }
            // Rename is the commit point. Cancellation or sidecar cleanup cannot undo installed weights.
            return CompleteInstallation(id);
        }
        catch (Exception error) { failure = error; throw; }
        finally { LogDownload(generation, attempt, DownloadDiagnosticStage.Install, model, started, failure); }
    }

    public void Remove(string id, ModelRemovalScope scope, CancellationToken token)
    {
        using var directoryLock = LockDirectory();
        var snapshot = Get(id);
        token.ThrowIfCancellationRequested();
        CheckPaths(snapshot.Path);
        if (scope == ModelRemovalScope.InstalledWeights)
        {
            File.Delete(snapshot.Path);
            var length = File.Exists(snapshot.Path + ".partial") ? new FileInfo(snapshot.Path + ".partial").Length : 0;
            Update(id, File.Exists(snapshot.Path + ".partial") ? ModelInstallState.Paused : ModelInstallState.NotInstalled, length);
            return;
        }
        ReleaseVerification(id);
        File.Delete(snapshot.Path + ".partial");
        File.Delete(snapshot.Path + ".partial.json");
        File.Delete(snapshot.Path + PendingMetadataSuffix);
        if (File.Exists(snapshot.Path))
        {
            Update(id, snapshot.State == ModelInstallState.Installed ? ModelInstallState.Installed : ModelInstallState.Failed,
                snapshot.State == ModelInstallState.Installed ? snapshot.Model.Bytes : 0,
                error: snapshot.State == ModelInstallState.Installed ? null :
                    snapshot.Error ?? "The complete file remains. Verify installed files before using it.");
        }
        else { Update(id, ModelInstallState.NotInstalled); }
    }

    private ModelInstallResult CompleteInstallation(string id)
    {
        var snapshot = Get(id);
        Exception? cleanupError = null;
        try
        {
            File.Delete(snapshot.Path + ".partial.json");
            File.Delete(snapshot.Path + PendingMetadataSuffix);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { cleanupError = error; }
        Update(id, ModelInstallState.Installed, snapshot.Model.Bytes, error: cleanupError is null ? null :
            $"Model installed, but resume data could not be removed. Use Verify installed files to retry cleanup. {cleanupError.Message}");
        return new ModelInstallResult(cleanupError);
    }

    private void VerifyPartial(ModelFileSnapshot snapshot, CancellationToken token,
        Guid? parentAttempt = null, long? parentGeneration = null)
    {
        var generation = parentAttempt is null ? _diagnostics?.CurrentGeneration : parentGeneration;
        var started = generation is null ? 0 : Stopwatch.GetTimestamp();
        var attempt = parentAttempt ?? (generation is null ? Guid.Empty : Guid.NewGuid());
        Exception? failure = null;
        try
        {
            ReleaseVerification(snapshot.Model.Id);
            Update(snapshot.Model.Id, ModelInstallState.Verifying, snapshot.Model.Bytes);
            var file = OpenRead(snapshot.Path + ".partial");
            try
            {
                NativeModelIntegrity.Verify(file, snapshot.Model, token);
                token.ThrowIfCancellationRequested();
                _verified.Add(snapshot.Model.Id, file);
            }
            catch { file.Dispose(); throw; }
            Update(snapshot.Model.Id, ModelInstallState.ReadyToInstall, snapshot.Model.Bytes);
        }
        catch (Exception error) { failure = error; throw; }
        finally { LogDownload(generation, attempt, DownloadDiagnosticStage.Verify, snapshot.Model, started, failure); }
    }

    private void LogDownload(long? generation, Guid attempt, DownloadDiagnosticStage stage, NativeModelEntry? model,
        long started, Exception? error, long? resumeOffset = null, long? downloaded = null, int? httpStatus = null)
    {
        if (generation is not { } value) return;
        var outcome = error switch
        {
            null => DownloadDiagnosticOutcome.Completed,
            OperationCanceledException => DownloadDiagnosticOutcome.Cancelled,
            TimeoutException => DownloadDiagnosticOutcome.TimedOut,
            _ => DownloadDiagnosticOutcome.Failed
        };
        _diagnostics!.Write(new DownloadDiagnostic(attempt, DateTimeOffset.UtcNow, stage, outcome,
            NativeDiagnosticContext.SafeFamily(model?.Family), Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            resumeOffset, downloaded, httpStatus ?? (int?)(error as HttpRequestException)?.StatusCode, error?.HResult)
        { ModelId = model is null ? null : NativeDiagnosticContext.SafeModelId(model.Id) }, value);
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

    private static void WriteResume(string path, ModelResume resume)
    {
        var pending = path + PendingMetadataSuffix;
        try
        {
            using (var file = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, resume, ModelDownloadJsonContext.Default.ModelResume);
                file.Flush(flushToDisk: true);
            }
            File.Move(pending, path + ".partial.json", overwrite: true);
        }
        finally { if (File.Exists(pending)) { File.Delete(pending); } }
    }

    private FileStream LockDirectory()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var existing = DirectoryPath;
        while (!Directory.Exists(existing)) { existing = Path.GetDirectoryName(existing) ?? throw new IOException("No model directory root exists."); }
        NativeModelIntegrity.RejectLinks(existing);
        Directory.CreateDirectory(DirectoryPath);
        NativeModelIntegrity.RejectLinks(DirectoryPath);
        var path = Path.Combine(DirectoryPath, ".localvoice-models.lock");
        if (File.Exists(path)) { NativeModelIntegrity.RejectLinks(path); }
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static void CheckPaths(string path)
    {
        NativeModelIntegrity.RejectLinks(Path.GetDirectoryName(path)!);
        foreach (var candidate in new[] { path, path + ".partial", path + ".partial.json", path + PendingMetadataSuffix })
            if (File.Exists(candidate)) { NativeModelIntegrity.RejectLinks(candidate); }
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        128 * 1024, FileOptions.SequentialScan);

    private ModelFileSnapshot Get(string id) => _models.TryGetValue(id, out var model)
        ? model : throw new ArgumentException("Choose a pinned catalog model.", nameof(id));

    private void Update(string id, ModelInstallState state, long bytes = 0, double rate = 0, string? error = null)
    {
        var snapshot = _models[id];
        snapshot = snapshot with
        {
            State = state, DownloadedBytes = bytes, BytesPerSecond = rate, Error = error,
            HasModelFile = File.Exists(snapshot.Path),
            HasPartial = File.Exists(snapshot.Path + ".partial") || File.Exists(snapshot.Path + ".partial.json") ||
                File.Exists(snapshot.Path + PendingMetadataSuffix)
        };
        _models[id] = snapshot;
        Changed?.Invoke(snapshot);
    }

    private void ReleaseVerification(string id)
    {
        if (_verified.Remove(id, out var lease)) { lease.Dispose(); }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var lease in _verified.Values) { lease.Dispose(); }
        _verified.Clear();
    }
}

internal sealed record ModelResume(string Sha256, long Bytes, string Url, string? ETag);

[JsonSourceGenerationOptions(AllowDuplicateProperties = false)]
[JsonSerializable(typeof(ModelResume))]
internal partial class ModelDownloadJsonContext : JsonSerializerContext;
