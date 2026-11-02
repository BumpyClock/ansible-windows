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
    private readonly object _gate = new();
    private readonly IModelSource _source;
    private readonly LocalDiagnosticLog? _diagnostics;
    private readonly Func<string, long> _freeSpace;
    private readonly Dictionary<string, ModelDownloadSnapshot> _models;
    private ModelStore _store;
    private CancellationTokenSource? _cancellation;
    private Task? _work;
    private bool _closing;

    public ModelDownloadManager(
        IReadOnlyList<NativeModelEntry> catalog, IReadOnlySet<string> supportedFamilies,
        string directory, HttpClient http, LocalDiagnosticLog? diagnostics = null)
        : this(catalog, supportedFamilies, directory, http,
            path => new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace, diagnostics) { }

    internal ModelDownloadManager(
        IReadOnlyList<NativeModelEntry> catalog, IReadOnlySet<string> supportedFamilies,
        string directory, HttpClient http, Func<string, long> freeSpace, LocalDiagnosticLog? diagnostics = null)
    {
        _source = new HttpModelSource(http);
        _diagnostics = diagnostics;
        _freeSpace = freeSpace;
        _store = new ModelStore(catalog, directory, _source, _freeSpace, _diagnostics);
        _store.Changed += StoreChanged;
        _models = catalog.ToDictionary(entry => entry.Id, entry => new ModelDownloadSnapshot(
            entry, supportedFamilies.Contains(entry.Family), ModelInstallState.NotInstalled,
            Path.Combine(_store.DirectoryPath, entry.Filename)), StringComparer.OrdinalIgnoreCase);
    }

    public static HttpClient CreateHttpClient() => HttpModelSource.CreateHttpClient();

    public event Action? Changed;
    public IReadOnlyList<ModelDownloadSnapshot> Models
    {
        get { lock (_gate) { return Array.AsReadOnly(_models.Values.ToArray()); } }
    }
    public bool IsBusy { get { lock (_gate) { return _work is not null; } } }
    public string DirectoryPath { get { lock (_gate) { return _store.DirectoryPath; } } }

    public Task RefreshAsync(CancellationToken token = default) => Start(cancellation => _store.InspectAsync(cancellation), token);

    public Task DownloadAsync(string id, CancellationToken token = default) => Start(cancellation =>
    {
        var snapshot = Get(id);
        if (!snapshot.Supported)
        {
            var error = new NotSupportedException("The compiled native backend does not support this model family.");
            lock (_gate) { _models[id] = snapshot with { State = ModelInstallState.Failed, Error = error.Message }; }
            Changed?.Invoke();
            throw error;
        }
        return _store.PrepareAsync(id, cancellation);
    }, token);

    // Call only within DictationSession.MaintainModelsAsync; hashing and transfer happen before this short commit.
    public Task InstallAsync(string id, CancellationToken token = default) => Start(cancellation =>
    {
        _store.InstallPrepared(id, cancellation);
        return Task.CompletedTask;
    }, token);

    // The session maintenance owner must release native read leases before calling this command.
    public Task RemoveAsync(string id, CancellationToken token = default) => Start(cancellation =>
    {
        _store.Remove(id, ModelRemovalScope.InstalledWeights, cancellation);
        return Task.CompletedTask;
    }, token);

    public Task DiscardPartialAsync(string id, CancellationToken token = default) => Start(cancellation =>
    {
        _store.Remove(id, ModelRemovalScope.PartialDownload, cancellation);
        return Task.CompletedTask;
    }, token);

    public void ChangeDirectory(string directory)
    {
        lock (_gate)
        {
            RequireIdle();
            var replacement = new ModelStore(_models.Values.Select(value => value.Model).ToArray(),
                directory, _source, _freeSpace, _diagnostics);
            _store.Changed -= StoreChanged;
            _store.Dispose();
            _store = replacement;
            _store.Changed += StoreChanged;
            foreach (var id in _models.Keys)
            {
                var model = _models[id];
                _models[id] = model with
                {
                    Path = Path.Combine(_store.DirectoryPath, model.Model.Filename), State = ModelInstallState.NotInstalled,
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
            _store.Changed -= StoreChanged;
            _store.Dispose();
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

    public ModelDownloadSnapshot Get(string id)
    {
        lock (_gate) { return _models.TryGetValue(id, out var model) ? model : throw new ArgumentException("Choose a pinned catalog model.", nameof(id)); }
    }
    private void RequireIdle()
    {
        ObjectDisposedException.ThrowIf(_closing, this);
        if (_work is not null) { throw new InvalidOperationException("Pause or finish the current model operation first."); }
    }
    private void StoreChanged(ModelFileSnapshot snapshot)
    {
        lock (_gate)
        {
            _models[snapshot.Model.Id] = _models[snapshot.Model.Id] with
            {
                State = snapshot.State, DownloadedBytes = snapshot.DownloadedBytes, BytesPerSecond = snapshot.BytesPerSecond,
                Error = snapshot.Error, HasPartial = snapshot.HasPartial, HasModelFile = snapshot.HasModelFile
            };
        }
        Changed?.Invoke();
    }
}
