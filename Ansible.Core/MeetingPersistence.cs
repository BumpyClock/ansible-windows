namespace Ansible.Core;

/// <summary>Meeting save diagnostics and failure policy over a latest-snapshot writer.</summary>
internal sealed class MeetingPersistence : IAsyncDisposable
{
    private readonly Func<MeetingDocument, Task> _write;
    private readonly LocalDiagnosticLog? _diagnostics;
    private readonly CoalescingSnapshotWriter<PendingSave> _writer;
    private sealed record PendingSave(MeetingDocument Document, long? DiagnosticGeneration);

    public MeetingPersistence(Func<MeetingDocument, Task> write, Action<Exception> failed,
        LocalDiagnosticLog? diagnostics = null)
    {
        _write = write;
        _diagnostics = diagnostics;
        _writer = new(WriteAsync, (_, error) => { if (error is not null) failed(error); });
    }

    public void Queue(long revision, MeetingDocument document) =>
        _writer.Queue(revision, new(document, _diagnostics?.CurrentGeneration));

    public async Task FlushAsync(long revision)
    {
        try { await _writer.FlushAsync(revision).ConfigureAwait(false); }
        catch (IOException error)
        {
            throw new IOException("The meeting transcript could not be saved. Retry ending or closing the meeting.",
                error.InnerException ?? error);
        }
    }

    private async Task WriteAsync(PendingSave next)
    {
        Exception? failure = null;
        var started = next.DiagnosticGeneration is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        try { await _write(next.Document).ConfigureAwait(false); }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            if (next.DiagnosticGeneration is { } generation)
                _diagnostics!.Write(new StorageDiagnostic(DateTimeOffset.UtcNow, StorageDiagnosticCategory.Meeting,
                    StorageDiagnosticStage.Save, failure is null ? DiagnosticOutcome.Completed : DiagnosticOutcome.Failed,
                    System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, failure?.HResult), generation);
        }
    }

    public ValueTask DisposeAsync() => _writer.DisposeAsync();
}
