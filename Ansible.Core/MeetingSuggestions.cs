namespace Ansible.Core;

public enum MeetingSuggestionOutcome { Skipped, Opened, Stale, Rejected, Failed }

/// <summary>
/// Owns metadata polling, suggestion episodes, explicit-open confirmation and shutdown.
/// Call every member on the UI thread. The detector must do its native work off-thread.
/// Opening a suggestion selects a notetaker; it never grants consent to capture audio.
/// </summary>
public sealed class MeetingSuggestions(
    IMeetingDetector detector,
    Func<bool> enabled,
    Func<bool> blocked,
    Func<MeetingDetectionCandidate, bool> isActiveSelection,
    Action<string, string> warn,
    TimeProvider? time = null) : IDisposable
{
    private readonly MeetingDetectionTracker _tracker = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly long _origin = (time ?? TimeProvider.System).GetTimestamp();
    private CancellationTokenSource _cancellation = new();
    private Task _monitor = Task.CompletedTask;
    private Task<MeetingSuggestionOutcome> _confirmation = Task.FromResult(MeetingSuggestionOutcome.Skipped);
    private MeetingDetectionCandidate? _observed;
    private bool _enabled;
    private bool _available;
    private bool _started;
    private bool _closing;
    private bool _disposed;
    private bool _unavailableReported;
    private long _observationVersion;

    public MeetingDetectionCandidate? Offer => _tracker.CurrentOffer;
    public bool IsConfirming { get; private set; }
    public event Action? Changed;

    public void Start()
    {
        if (_started || _closing || _disposed) return;
        _started = true;
        _monitor = MonitorAsync(_cancellation.Token);
    }

    public void Refresh()
    {
        if (!_started || _closing || _disposed) return;
        var monitoring = enabled();
        var available = monitoring && !blocked();
        if (monitoring != _enabled || available != _available)
        {
            _enabled = monitoring;
            _available = available;
            InvalidateObservation();
        }
        if (_observed is { } candidate && isActiveSelection(candidate)) _tracker.Dismiss(candidate);
        Changed?.Invoke();
    }

    public void Dismiss()
    {
        if (Offer is not { } candidate) return;
        _tracker.Dismiss(candidate);
        InvalidateObservation();
        Changed?.Invoke();
    }

    public Task<MeetingSuggestionOutcome> ConfirmAsync(Func<MeetingDetectionCandidate, bool> open)
    {
        Refresh();
        if (_closing || _disposed || IsConfirming || Offer is not { } expected)
            return Task.FromResult(MeetingSuggestionOutcome.Skipped);
        IsConfirming = true;
        _confirmation = ConfirmCoreAsync(expected, _observationVersion, _cancellation.Token, open);
        return _confirmation;
    }

    public void BeginShutdown()
    {
        if (_closing || _disposed) return;
        _closing = true;
        _cancellation.Cancel();
        InvalidateObservation();
        Changed?.Invoke();
    }

    /// <summary>Join both producers before disposing or resuming after a failed shutdown.</summary>
    public Task JoinAsync() => Task.WhenAll(_monitor, _confirmation);

    public void ResumeAfterFailedShutdown()
    {
        if (!_closing || _disposed) return;
        if (!_monitor.IsCompleted || !_confirmation.IsCompleted)
            throw new InvalidOperationException("Join meeting suggestions before resuming.");
        _cancellation.Dispose();
        _cancellation = new CancellationTokenSource();
        _closing = false;
        if (_started) _monitor = MonitorAsync(_cancellation.Token);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (!_monitor.IsCompleted || !_confirmation.IsCompleted)
            throw new InvalidOperationException("Stop and join meeting suggestions before disposing.");
        BeginShutdown();
        _disposed = true;
        _cancellation.Dispose();
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        // Publish the owned task before callbacks can synchronously initiate shutdown.
        await Task.Yield();
        try
        {
            while (!token.IsCancellationRequested)
            {
                Refresh();
                if (_enabled)
                {
                    var version = _observationVersion;
                    MeetingDetectionCandidate? candidate;
                    try { candidate = await detector.ReadAsync(token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                    catch (Exception error)
                    {
                        if (token.IsCancellationRequested) break;
                        InvalidateObservation();
                        Changed?.Invoke();
                        if (!_unavailableReported)
                        {
                            _unavailableReported = true;
                            warn("Meeting detection unavailable",
                                $"{error.Message} You can start notetaking from Meetings. Detection will retry.");
                        }
                        await Task.Delay(TimeSpan.FromSeconds(2), _time, token);
                        continue;
                    }
                    if (token.IsCancellationRequested) break;
                    Refresh();
                    if (version == _observationVersion && _enabled)
                    {
                        _unavailableReported = false;
                        _observed = candidate;
                        if (candidate is not null && isActiveSelection(candidate)) _tracker.Dismiss(candidate);
                        // Busy scans can identify an already-active meeting, but cannot prove absence.
                        if (_available) _tracker.Update(candidate, _time.GetElapsedTime(_origin), enabled: true, busy: false);
                        else InvalidateObservation();
                        Changed?.Invoke();
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), _time, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task<MeetingSuggestionOutcome> ConfirmCoreAsync(MeetingDetectionCandidate expected,
        long version, CancellationToken token, Func<MeetingDetectionCandidate, bool> open)
    {
        await Task.Yield();
        try
        {
            Changed?.Invoke();
            token.ThrowIfCancellationRequested();
            var current = await detector.ReadAsync(token);
            token.ThrowIfCancellationRequested();
            Refresh();
            if (version != _observationVersion || !_available || current != expected || Offer != expected)
            {
                InvalidateObservation();
                return MeetingSuggestionOutcome.Stale;
            }
            if (!open(expected))
            {
                InvalidateObservation();
                warn("Meeting changed", "The detected Teams app is no longer available. Select your meeting app from Meetings.");
                return MeetingSuggestionOutcome.Rejected;
            }
            _tracker.Dismiss(expected);
            InvalidateObservation();
            return MeetingSuggestionOutcome.Opened;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return MeetingSuggestionOutcome.Skipped; }
        catch (Exception error)
        {
            InvalidateObservation();
            warn("Could not open notetaker", error.Message);
            return MeetingSuggestionOutcome.Failed;
        }
        finally
        {
            IsConfirming = false;
            Changed?.Invoke();
        }
    }

    private void InvalidateObservation()
    {
        _observationVersion++;
        _tracker.InvalidateObservation();
    }
}
