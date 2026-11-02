namespace Ansible.Core;

/// <summary>One owned writer. New snapshots replace pending work without delaying the current disk write.</summary>
internal sealed class MeetingPersistence : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _ready = new(0, 1);
    private readonly Func<MeetingDocument, Task> _write;
    private readonly Action<Exception> _failed;
    private readonly Task _worker;
    private (long Revision, MeetingDocument Document)? _pending;
    private TaskCompletionSource _changed = NewCompletion();
    private long _newest = -1;
    private long _writing = -1;
    private long _saved = -1;
    private long _attempted = -1;
    private Exception? _error;
    private bool _stopping;

    public MeetingPersistence(Func<MeetingDocument, Task> write, Action<Exception> failed)
    {
        _write = write;
        _failed = failed;
        _worker = Task.Run(RunAsync);
    }

    public void Queue(long revision, MeetingDocument document)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (revision < _newest || revision <= _saved || revision == _writing) return;
            _newest = revision;
            _error = null;
            var wake = _pending is null;
            _pending = (revision, document);
            if (wake && _ready.CurrentCount == 0) _ready.Release();
        }
    }

    public async Task FlushAsync(long revision)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_saved >= revision) return;
                if (_attempted >= revision && _error is { } error && _pending is null)
                    throw new IOException("The meeting transcript could not be saved. Retry ending or closing the meeting.", error);
                changed = _changed.Task;
            }
            await changed.ConfigureAwait(false);
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            await _ready.WaitAsync().ConfigureAwait(false);
            (long Revision, MeetingDocument Document) next;
            lock (_gate)
            {
                if (_pending is not { } pending)
                {
                    if (_stopping) return;
                    continue;
                }
                next = pending;
                _writing = next.Revision;
                _error = null;
                _pending = null;
            }
            Exception? failure = null;
            try { await _write(next.Document).ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            if (failure is not null)
            {
                try { _failed(failure); }
                catch (Exception notificationError) { failure = new AggregateException(failure, notificationError); }
            }
            lock (_gate)
            {
                _writing = -1;
                _attempted = next.Revision;
                _error = failure;
                if (failure is null) _saved = next.Revision;
                var changed = _changed;
                _changed = NewCompletion();
                changed.TrySetResult();
                if (_stopping && _pending is null) return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            if (_ready.CurrentCount == 0) _ready.Release();
        }
        await _worker.ConfigureAwait(false);
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
