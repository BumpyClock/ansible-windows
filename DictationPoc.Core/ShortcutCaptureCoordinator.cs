namespace DictationPoc.Core;

/// <summary>
/// Serializes the one admitted shortcut interaction that owns a target inspection, replacing scattered
/// capture flags with a single identity-based owner. The invariants it enforces:
///
///  * One admitted press owns one target inspection: a new press is admitted only when no inspection is in
///    flight and no delivery is active, so two inspections can never race.
///  * A push-to-talk release marks the pending capture stale, so a later press cannot start recording from an
///    inspection whose key was already released.
///  * Only the owning capture can start delivery or clear ownership, so a release or a rejected/stale callback
///    cannot clear another press's capture.
///
/// All members are safe to call from the owning UI thread; the lock guards against the inspection continuation
/// observing a torn state.
/// </summary>
public sealed class ShortcutCaptureCoordinator
{
    private readonly object _sync = new();
    private Capture? _active;

    public sealed class Capture
    {
        internal Capture(bool pushToTalk) => PushToTalk = pushToTalk;
        public bool PushToTalk { get; }
        internal bool Released { get; set; }
    }

    /// <summary>True while an admitted capture still owns an in-flight target inspection.</summary>
    public bool HasPendingCapture
    {
        get { lock (_sync) { return _active is not null; } }
    }

    /// <summary>
    /// Admits a press to own a new target inspection, or returns null to reject it when an inspection is
    /// already pending or a delivery is active.
    /// </summary>
    public Capture? TryBeginCapture(bool pushToTalk, bool deliveryActive)
    {
        lock (_sync)
        {
            if (_active is not null || deliveryActive) { return null; }
            _active = new Capture(pushToTalk);
            return _active;
        }
    }

    /// <summary>
    /// Records a shortcut release. A push-to-talk release marks the active capture stale so its pending
    /// inspection cannot start recording; a toggle-mode release leaves the capture able to start.
    /// </summary>
    public void NoteReleased()
    {
        lock (_sync)
        {
            if (_active is { PushToTalk: true } active) { active.Released = true; }
        }
    }

    /// <summary>True when <paramref name="capture"/> is still the active, non-stale owner.</summary>
    public bool IsCurrent(Capture capture)
    {
        lock (_sync) { return ReferenceEquals(_active, capture) && !capture.Released; }
    }

    /// <summary>
    /// Transfers ownership from the capture to its delivery pump. Succeeds only when the capture is still the
    /// active, non-stale owner; afterward a new press may be admitted.
    /// </summary>
    public bool TryStartDelivery(Capture capture)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_active, capture) || capture.Released) { return false; }
            _active = null;
            return true;
        }
    }

    /// <summary>
    /// Clears ownership for a completed capture that did not start delivery, but only when it is still the
    /// active owner, so a stale or rejected callback cannot clear a different press's capture.
    /// </summary>
    public void Abandon(Capture capture)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_active, capture)) { _active = null; }
        }
    }
}
