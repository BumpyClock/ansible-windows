using Ansible.Core;

namespace Ansible;

/// <summary>A session-only history item. Transcript text is never persisted.</summary>
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
/// in-memory retention policy and notifies views when its snapshot changes.
/// </summary>
internal sealed class HomeDictationHistory : IDisposable
{
    internal const int MaximumItems = 100;
    internal const int MaximumCharacters = 500_000;

    private readonly object _gate = new();
    private readonly List<HomeHistoryItem> _items = [];
    private readonly DictationSession? _session;
    private bool _oversizedCurrentResult;
    private bool _disposed;

    internal HomeDictationHistory() { }

    internal HomeDictationHistory(DictationSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _session.DictationCompleted += OnDictationCompleted;
    }

    internal event Action? Changed;

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
                while (_items.Count > MaximumItems || _items.Sum(existing => existing.Text.Length) > MaximumCharacters)
                    _items.RemoveAt(_items.Count - 1);
                accepted = true;
            }
        }
        Changed?.Invoke();
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
        }
        Changed?.Invoke();
        return true;
    }

    internal bool Remove(Guid operationId)
    {
        lock (_gate)
        {
            if (_disposed || _items.RemoveAll(existing => existing.OperationId == operationId) == 0) { return false; }
        }
        Changed?.Invoke();
        return true;
    }

    private void OnDictationCompleted(DictationCompletion completion) => Add(completion);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
        }
        if (_session is not null) { _session.DictationCompleted -= OnDictationCompleted; }
    }
}
