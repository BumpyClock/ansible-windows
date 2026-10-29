using Ansible.Core;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansible;

/// <summary>A locally retained transcription and its reported insertion outcome.</summary>
/// <param name="Delivery">
/// Outcome of the shortcut insertion for this dictation, when one reported. Null for dictations started from the
/// in-app button or whose delivery never reported (deferred, cancelled, or still pending).
/// </param>
internal sealed record HomeHistoryItem(
    Guid OperationId,
    DateTimeOffset CompletedAt,
    string Text,
    string ModelId,
    int? Words,
    TimeSpan Elapsed,
    TextDeliveryStatus? Delivery = null);

/// <summary>
/// App-owned bounded history projection. The session owns completion eligibility; this store only owns the
/// local retention policy and notifies views when its snapshot changes.
/// </summary>
internal sealed class HomeDictationHistory : IDisposable, IAsyncDisposable
{
    internal const int MaximumItems = 100;
    internal const int MaximumCharacters = 500_000;

    private readonly object _gate = new();
    private readonly List<HomeHistoryItem> _items = [];
    private readonly DictationSession? _session;
    private readonly string? _path;
    private readonly LocalDiagnosticLog? _diagnostics;
    private readonly Func<Task<HomeHistoryDocument?>> _readDocument;
    private readonly Func<HomeHistoryDocument, Task> _writeDocument;
    private readonly Task _initialization;
    private readonly CoalescingSnapshotWriter<PendingSave>? _writer;
    private long _revision;
    private long? _pendingGeneration;
    private bool _loadFinished;
    private bool _oversizedCurrentResult;
    private bool _disposed;
    private bool _loadFailed;
    private string? _storageError;
    private sealed record PendingSave(HomeHistoryDocument Document, long? DiagnosticGeneration);

    internal HomeDictationHistory(string? path = null, LocalDiagnosticLog? diagnostics = null,
        Func<Task<HomeHistoryDocument?>>? readDocument = null, Func<HomeHistoryDocument, Task>? writeDocument = null)
    {
        _path = path is null ? null : Path.GetFullPath(path);
        _diagnostics = diagnostics;
        _readDocument = readDocument ?? ReadDocumentAsync;
        _writeDocument = writeDocument ?? WriteDocumentAsync;
        _writer = _path is null ? null : new(PersistAsync, (_, error) => WriteCompleted(error));
        _initialization = _path is null ? Task.CompletedTask : Task.Run(LoadAsync);
    }

    internal HomeDictationHistory(DictationSession session, string path, LocalDiagnosticLog? diagnostics = null) : this(path, diagnostics)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _session.DictationCompleted += OnDictationCompleted;
    }

    internal event Action? Changed;

    internal Task InitializeAsync() => _initialization;

    internal string? StorageError
    {
        get { lock (_gate) { return _storageError; } }
    }

    internal bool OversizedCurrentResult
    {
        get { lock (_gate) { return _oversizedCurrentResult; } }
    }

    internal IReadOnlyList<HomeHistoryItem> Snapshot()
    {
        lock (_gate) { return _items.ToArray(); }
    }

    internal bool Contains(Guid operationId)
    {
        lock (_gate) { return _items.Any(item => item.OperationId == operationId); }
    }

    /// <summary>Accepts a completion only once and retains at most the newest bounded entries.</summary>
    internal bool Add(DictationCompletion completion)
    {
        var text = (string.IsNullOrWhiteSpace(completion.Result.SpeechText)
            ? completion.Result.DisplayText
            : completion.Result.SpeechText).Trim();
        if (text.Length == 0) { return false; }

        bool accepted;
        lock (_gate)
        {
            if (_disposed || _items.Any(existing => existing.OperationId == completion.OperationId)) { return false; }
            _oversizedCurrentResult = text.Length > MaximumCharacters;
            if (_oversizedCurrentResult)
            {
                accepted = false;
            }
            else
            {
                _items.Insert(0, new HomeHistoryItem(completion.OperationId, completion.CompletedAt, text,
                    completion.ModelId, completion.Result.SpokenWords, completion.Elapsed));
                Trim();
                QueueSave();
                accepted = true;
            }
        }
        NotifyChanged();
        return accepted;
    }

    /// <summary>
    /// Records the real insertion outcome for a retained dictation. Never synthesizes one. The session raises
    /// <see cref="DictationSession.DictationCompleted"/> before the operation task completes, so a delivery that
    /// awaited that task always finds its row unless retention rejected or the user removed it.
    /// </summary>
    internal bool RecordDelivery(Guid operationId, TextDeliveryStatus status)
    {
        lock (_gate)
        {
            if (_disposed) { return false; }
            var index = _items.FindIndex(existing => existing.OperationId == operationId);
            if (index < 0 || _items[index].Delivery == status) { return false; }
            _items[index] = _items[index] with { Delivery = status };
            QueueSave();
        }
        NotifyChanged();
        return true;
    }

    internal bool Remove(Guid operationId)
    {
        lock (_gate)
        {
            if (_disposed || _items.RemoveAll(existing => existing.OperationId == operationId) == 0) { return false; }
            QueueSave();
        }
        NotifyChanged();
        return true;
    }

    private void OnDictationCompleted(DictationCompletion completion) => Add(completion);

    private void Trim()
    {
        _items.Sort((left, right) => right.CompletedAt.CompareTo(left.CompletedAt));
        var characters = _items.Sum(item => (long)item.Text.Length);
        while (_items.Count > MaximumItems || characters > MaximumCharacters)
        {
            characters -= _items[^1].Text.Length;
            _items.RemoveAt(_items.Count - 1);
        }
    }

    private void QueueSave()
    {
        if (_writer is null) return;
        _revision++;
        _pendingGeneration = _diagnostics?.CurrentGeneration;
        if (_loadFinished && !_loadFailed) QueueSnapshot();
    }

    private void QueueSnapshot() =>
        _writer!.Queue(_revision, new(new(1, _items.ToList()), _pendingGeneration));

    private async Task LoadAsync()
    {
        var generation = _diagnostics?.CurrentGeneration;
        var started = generation is null ? 0 : Stopwatch.GetTimestamp();
        Exception? failure = null;
        try
        {
            var document = await _readDocument().ConfigureAwait(false);
            if (document is null || document.SchemaVersion != 1 || document.Items is null ||
                document.Items.Count > MaximumItems ||
                document.Items.Any(item => item is null || item.OperationId == Guid.Empty ||
                    string.IsNullOrWhiteSpace(item.Text) || item.ModelId is null ||
                    item.CompletedAt == default || item.Elapsed < TimeSpan.Zero || item.Words is < 0 ||
                    (item.Delivery is { } delivery && !Enum.IsDefined(delivery))) ||
                document.Items.Sum(item => (long)item.Text.Length) > MaximumCharacters ||
                document.Items.Select(item => item.OperationId).Distinct().Count() != document.Items.Count)
                throw new InvalidDataException("The saved dictation history has an unsupported or invalid format.");
            lock (_gate)
            {
                var existing = _items.Select(item => item.OperationId).ToHashSet();
                _items.AddRange(document.Items.Where(item => !existing.Contains(item.OperationId)));
                Trim();
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception error)
        {
            failure = error;
            lock (_gate) _loadFailed = true;
            ReportStorageError("Saved history could not be loaded. Its file was left unchanged. New transcriptions stay in memory only.", error);
        }
        finally
        {
            lock (_gate)
            {
                _loadFinished = true;
                if (!_loadFailed && _revision > 0) QueueSnapshot();
                if (_disposed) _writer!.Complete();
            }
            LogStorage(generation, StorageDiagnosticStage.Load, started, failure);
            NotifyChanged();
        }
    }

    private async Task PersistAsync(PendingSave pending)
    {
        var started = pending.DiagnosticGeneration is null ? 0 : Stopwatch.GetTimestamp();
        Exception? failure = null;
        try { await _writeDocument(pending.Document).ConfigureAwait(false); }
        catch (Exception error) { failure = error; throw; }
        finally { LogStorage(pending.DiagnosticGeneration, StorageDiagnosticStage.Save, started, failure); }
    }

    private void WriteCompleted(Exception? error)
    {
        if (error is not null)
        {
            ReportStorageError("History could not be saved. Recent changes may be lost when Ansible closes.", error);
            return;
        }
        bool recovered;
        lock (_gate)
        {
            recovered = _storageError is not null;
            _storageError = null;
        }
        if (recovered) NotifyChanged();
    }

    private async Task<HomeHistoryDocument?> ReadDocumentAsync()
    {
        await using var file = new FileStream(_path!, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaximumCharacters * 6L + MaximumItems * 4096L)
            throw new InvalidDataException("The saved dictation history exceeds its size limit.");
        return await JsonSerializer.DeserializeAsync(file, HomeHistoryJson.Default.HomeHistoryDocument);
    }

    private async Task WriteDocumentAsync(HomeHistoryDocument document)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(_path!)!);
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, document, HomeHistoryJson.Default.HomeHistoryDocument);
                await file.FlushAsync();
            }
            File.Move(temporary, _path!, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void LogStorage(long? generation, StorageDiagnosticStage stage, long started, Exception? error)
    {
        if (generation is { } value)
            _diagnostics!.Write(new StorageDiagnostic(DateTimeOffset.UtcNow, StorageDiagnosticCategory.History,
                stage, error is null ? DiagnosticOutcome.Completed : DiagnosticOutcome.Failed,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, error?.HResult), value);
    }

    private void ReportStorageError(string message, Exception error)
    {
        Debug.WriteLine($"Ansible: dictation history storage failed: {error.Message}");
        lock (_gate) _storageError = $"{message} {error.Message}";
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        if (Changed is not { } observers) return;
        foreach (Action observer in observers.GetInvocationList())
        {
            try { observer(); }
            catch (Exception error) { Trace.TraceError("Ansible: history observer failed. {0}", error); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            // Loading may still need to admit one merged snapshot before completion.
            if (_loadFinished) _writer?.Complete();
        }
        if (_session is not null) { _session.DictationCompleted -= OnDictationCompleted; }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _initialization.ConfigureAwait(false);
        if (_writer is not null) await _writer.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed record HomeHistoryDocument(int SchemaVersion, List<HomeHistoryItem> Items);

[JsonSerializable(typeof(HomeHistoryDocument))]
internal partial class HomeHistoryJson : JsonSerializerContext;
