using System.Threading.Channels;

namespace Ansible.Core;

/// <summary>The outcome of one attempt to insert pending speech into the destination field.</summary>
public enum TextDeliveryStatus
{
    /// <summary>The pending text was delivered to the target. The caller may confirm the cursor.</summary>
    Sent,
    /// <summary>
    /// Nothing was sent because a transient physical modifier is still held. The caller keeps the pending
    /// text and retries once the modifier is released; this delays typing rather than disabling it.
    /// </summary>
    Deferred,
    /// <summary>
    /// The target or focus is no longer valid, or a send only partially completed. The caller stops this
    /// delivery and never retries a partially delivered send.
    /// </summary>
    Rejected
}

public readonly record struct TextDeliveryOutcome(TextDeliveryStatus Status, string Message)
{
    public static TextDeliveryOutcome Sent(string message) => new(TextDeliveryStatus.Sent, message);
    public static TextDeliveryOutcome Deferred(string message) => new(TextDeliveryStatus.Deferred, message);
    public static TextDeliveryOutcome Rejected(string message) => new(TextDeliveryStatus.Rejected, message);
}

/// <summary>
/// Sole serialized consumer for one dictation's live recognition. Producers publish the latest cumulative
/// speech (<see cref="Report"/>), end the stream (<see cref="Complete"/>), or ask for re-evaluation of a
/// modifier deferral (<see cref="SignalRelease"/>); data and wake are separate so a signal cannot evict speech.
/// Typing is delayed, never disabled: a held chord or a transient physical modifier defers and is retried; only
/// focus/target loss or a partial send stops it, and a partial send is never retried.
/// </summary>
public sealed class LiveTextDelivery
{
    private const int DeferredRetryMs = 40;

    private readonly Func<string, CancellationToken, Task<TextDeliveryOutcome>> _insert;
    private readonly Func<bool> _isCurrent;
    private readonly Func<bool> _typingDeferred;
    private readonly Action<NoticeKind, string, string> _notify;

    public LiveTextDelivery(
        Func<string, CancellationToken, Task<TextDeliveryOutcome>> insert,
        Func<bool> isCurrent,
        Func<bool> typingDeferred,
        Action<NoticeKind, string, string> notify)
    {
        _insert = insert ?? throw new ArgumentNullException(nameof(insert));
        _isCurrent = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent));
        _typingDeferred = typingDeferred ?? throw new ArgumentNullException(nameof(typingDeferred));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
    }

    private readonly object _sync = new();
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    private string? _latestSpeech;
    private bool _completed;

    public LiveTextCursor Cursor { get; } = new();
    public bool Stopped { get; private set; }
    public string? Error { get; private set; }
    public string? RecognizedSpeech { get { lock (_sync) { return _latestSpeech; } } }

    /// <summary>Publishes the latest cumulative speech; final or speech-less updates only wake the consumer.</summary>
    public void Report(TranscriptUpdate update)
    {
        if (!update.IsFinal && update.SpeechText is not null)
            lock (_sync) { _latestSpeech = update.SpeechText; }
        Wake();
    }

    /// <summary>Marks the producer finished so the consumer stops after flushing the latest partial.</summary>
    public void Complete()
    {
        lock (_sync) { _completed = true; }
        Wake();
    }

    /// <summary>Wakes the consumer to re-evaluate a modifier deferral and flush retained speech on release.</summary>
    public void SignalRelease() => Wake();

    private void Wake() => _wake.Writer.TryWrite(0);

    /// <summary>Pumps live partials until the producer completes or the token cancels; both return cleanly for join.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                if (Stopped) { break; }
                var deferred = !_typingDeferred()
                    && await FlushPendingAsync("Insertion stopped", cancellationToken).ConfigureAwait(false);
                lock (_sync) { if (_completed) { break; } }
                await WaitForWorkAsync(retry: deferred, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { Stop("Live insertion stopped", error.Message, notify: true); }
    }

    /// <summary>
    /// Flushes the authoritative final speech after the producer completed, through this same consumer. A held
    /// chord or transient modifier defers and retries; a null final speech records "no authoritative text"
    /// without a notice so the owner can compose the closing message.
    /// </summary>
    public async Task InsertFinalAsync(string? finalSpeech, CancellationToken cancellationToken)
    {
        if (Stopped) { return; }
        if (finalSpeech is null)
        {
            Stop(null, "The model did not return authoritative speech text.", notify: false);
            return;
        }
        lock (_sync) { _latestSpeech = finalSpeech; }
        while (!Stopped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_isCurrent()) { return; }
            if (_typingDeferred()
                || await FlushPendingAsync("Insertion stopped", cancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(DeferredRetryMs, cancellationToken).ConfigureAwait(false);
                continue;
            }
            return;
        }
    }

    // Waits for a wake signal; on retry also wakes after a bounded delay so a modifier released with no later
    // transcript or release signal still resumes typing. Honors cancellation for close.
    private async Task WaitForWorkAsync(bool retry, CancellationToken cancellationToken)
    {
        using var timeout = retry ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        timeout?.CancelAfter(DeferredRetryMs);
        try { await _wake.Reader.WaitToReadAsync(timeout?.Token ?? cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (retry && !cancellationToken.IsCancellationRequested) { }
        while (_wake.Reader.TryRead(out _)) { }
    }

    // Returns true only when the insert was deferred by a transient held modifier and should be retried.
    private async Task<bool> FlushPendingAsync(string title, CancellationToken cancellationToken)
    {
        string? recognized;
        lock (_sync) { recognized = _latestSpeech; }
        if (Stopped || recognized is null || !_isCurrent()) { return false; }
        string pending;
        try { pending = Cursor.Pending(recognized); }
        catch (Exception error) { Stop(title, error.Message, notify: _isCurrent()); return false; }
        if (string.IsNullOrWhiteSpace(pending)) { return false; }

        TextDeliveryOutcome outcome;
        try { outcome = await _insert(pending, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { Stop(title, error.Message, notify: _isCurrent()); return false; }

        switch (outcome.Status)
        {
            case TextDeliveryStatus.Sent:
                Cursor.ConfirmSent(recognized);
                return false;
            case TextDeliveryStatus.Deferred:
                return true;
            default:
                Stop(title, outcome.Message, notify: _isCurrent());
                return false;
        }
    }

    private void Stop(string? title, string? message, bool notify)
    {
        if (Stopped) { return; }
        Stopped = true;
        Error = message;
        if (notify && title is not null && !string.IsNullOrEmpty(message))
            _notify(NoticeKind.Warning, title, message);
    }
}
