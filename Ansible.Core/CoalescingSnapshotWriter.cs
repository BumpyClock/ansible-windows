namespace Ansible.Core;

/// <summary>Serializes full snapshots, retaining only the latest pending revision. Flush confirms saving; disposal joins attempts.</summary>
public sealed class CoalescingSnapshotWriter<T> : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<T, Task> _persist;
    private readonly Action<long, Exception?> _completed;
    private readonly Task _worker;
    private TaskCompletionSource _changed = NewCompletion();
    private Snapshot? _pending;
    private long _newest = -1;
    private long _writing = -1;
    private long _saved = -1;
    private long _attempted = -1;
    private Exception? _error;
    private bool _stopping;

    private sealed record Snapshot(long Revision, T Value);

    /// <param name="persist">Persists a complete snapshot before returning.</param>
    /// <param name="completed">Runs on the worker after each attempt. Must not synchronously wait for this writer.</param>
    public CoalescingSnapshotWriter(Func<T, Task> persist, Action<long, Exception?> completed)
    {
        ArgumentNullException.ThrowIfNull(persist);
        ArgumentNullException.ThrowIfNull(completed);
        _persist = persist;
        _completed = completed;
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Accepts a nonnegative revision and a snapshot the caller will no longer mutate.</summary>
    public void Queue(long revision, T snapshot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (revision < _newest || revision <= _saved || revision == _writing) return;
            _newest = revision;
            _error = null;
            var wake = _pending is null;
            _pending = new(revision, snapshot);
            if (wake) SignalChange();
        }
    }

    /// <summary>Waits for a successful save at or beyond a revision within the submitted range.</summary>
    public async Task FlushAsync(long revision)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (revision > _newest)
                    throw new ArgumentOutOfRangeException(nameof(revision), "The revision has not been submitted.");
                if (_saved >= revision) return;
                if (_attempted >= revision && _error is { } error && _pending is null)
                    throw new IOException("The snapshot could not be saved.", error);
                changed = _changed.Task;
            }
            await changed.ConfigureAwait(false);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            SignalChange();
        }
    }

    public ValueTask DisposeAsync()
    {
        Complete();
        return new(_worker);
    }

    private async Task RunAsync()
    {
        while (true)
        {
            Snapshot? next;
            Task? ready = null;
            lock (_gate)
            {
                next = _pending;
                if (next is null)
                {
                    if (_stopping) return;
                    ready = _changed.Task;
                }
                else
                {
                    _pending = null;
                    _writing = next.Revision;
                    _error = null;
                }
            }
            if (next is null)
            {
                await ready!.ConfigureAwait(false);
                continue;
            }

            Exception? failure = null;
            try { await _persist(next.Value).ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            try { _completed(next.Revision, failure); }
            catch (Exception notificationError)
            {
                System.Diagnostics.Trace.TraceError("Ansible: snapshot completion observer failed. {0}", notificationError);
                if (failure is not null) failure = new AggregateException(failure, notificationError);
            }
            lock (_gate)
            {
                _writing = -1;
                _attempted = next.Revision;
                _error = failure;
                if (failure is null) _saved = next.Revision;
                SignalChange();
            }
        }
    }

    private void SignalChange()
    {
        var changed = _changed;
        _changed = NewCompletion();
        changed.TrySetResult();
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
