namespace Ansible.Core;

public sealed record CaptureCleanupResult(long BytesRetained, int DeletedTakes, int ProtectedTakes, int FailedTakes);

/// <summary>Serializes private capture storage. Leases protect active writers, visible results and delayed clipboard readers.</summary>
public sealed class CaptureStore(string root, TimeProvider? time = null)
{
    public const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    public static readonly TimeSpan MaximumAge = TimeSpan.FromDays(7);
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, int> _leases = new(StringComparer.OrdinalIgnoreCase);
    public string Root => _root;

    public async Task<CaptureLease> CreateTakeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(_root, $"{_time.GetUtcNow():yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
            await Task.Run(() => Directory.CreateDirectory(path), cancellationToken).ConfigureAwait(false);
            _leases.Add(path, 1);
            return new(this, path);
        }
        finally { _gate.Release(); }
    }

    public async Task<CaptureLease> ProtectAsync(string directory, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(path), _root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only a direct capture directory can be protected.", nameof(directory));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _leases.TryGetValue(path, out var count);
            _leases[path] = count + 1;
            return new(this, path);
        }
        finally { _gate.Release(); }
    }

    public async Task<long> GetSizeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => ReadTakes().Sum(take => take.Bytes), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public Task<CaptureCleanupResult> CleanupAsync(CancellationToken cancellationToken = default) =>
        CleanAsync(false, cancellationToken);
    public Task<CaptureCleanupResult> DeleteAllAsync(CancellationToken cancellationToken = default) =>
        CleanAsync(true, cancellationToken);

    public async Task<bool> DeleteTakeAsync(string directory, CancellationToken cancellationToken = default)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!string.Equals(Path.GetDirectoryName(path), _root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only a direct capture directory can be deleted.", nameof(directory));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_leases.ContainsKey(path)) return false;
            return await Task.Run(() =>
            {
                if (!Directory.Exists(path)) return false;
                Directory.Delete(path, recursive: true);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<CaptureCleanupResult> CleanAsync(bool all, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var takes = ReadTakes().OrderBy(take => take.CreatedAt).ToArray();
                var bytes = takes.Sum(take => take.Bytes);
                var deleted = 0;
                var protectedCount = 0;
                var failed = 0;
                var cutoff = _time.GetUtcNow() - MaximumAge;
                foreach (var take in takes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_leases.ContainsKey(take.Path)) { protectedCount++; continue; }
                    if (!all && take.CreatedAt > cutoff && bytes <= MaximumBytes) continue;
                    try { Directory.Delete(take.Path, recursive: true); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        failed++;
                        continue;
                    }
                    bytes -= take.Bytes;
                    deleted++;
                }
                return new CaptureCleanupResult(bytes, deleted, protectedCount, failed);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private IEnumerable<(string Path, DateTimeOffset CreatedAt, long Bytes)> ReadTakes()
    {
        if (!Directory.Exists(_root)) yield break;
        foreach (var path in Directory.EnumerateDirectories(_root))
        {
            var directory = new DirectoryInfo(path);
            // Never traverse links out of private capture storage during cleanup.
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var bytes = directory.EnumerateFiles("*", options).Sum(file => file.Length);
            yield return (directory.FullName, directory.CreationTimeUtc, bytes);
        }
    }

    internal async ValueTask ReleaseAsync(string path)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var count = _leases[path];
            if (count == 1) _leases.Remove(path);
            else _leases[path] = count - 1;
        }
        finally { _gate.Release(); }
    }
}

public sealed class CaptureLease : IAsyncDisposable
{
    private CaptureStore? _store;
    public string Directory { get; }
    internal CaptureLease(CaptureStore store, string directory) { _store = store; Directory = directory; }
    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _store, null) is { } store
        ? store.ReleaseAsync(Directory) : ValueTask.CompletedTask;
}
