namespace Ansible.Core;

/// <summary>Debounces meeting suggestions. This tracker never starts audio capture.</summary>
public sealed class MeetingDetectionTracker
{
    private static readonly TimeSpan StablePeriod = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RearmPeriod = TimeSpan.FromSeconds(10);
    private readonly Dictionary<MeetingDetectionCandidate, TimeSpan?> _dismissed = [];
    private MeetingDetectionCandidate? _candidate;
    private TimeSpan _candidateSince;
    private TimeSpan? _lastUpdate;

    public MeetingDetectionCandidate? CurrentOffer { get; private set; }

    /// <summary>A failed observation cannot confirm presence, stability, or absence.</summary>
    public void InvalidateObservation()
    {
        CurrentOffer = null;
        _candidate = null;
        foreach (var identity in _dismissed.Keys.ToArray()) _dismissed[identity] = null;
    }

    /// <summary>Supply only successful observations; failures are not absence.</summary>
    public MeetingDetectionCandidate? Update(MeetingDetectionCandidate? candidate, TimeSpan now, bool enabled, bool busy)
    {
        if (now < TimeSpan.Zero || _lastUpdate is { } previous && now < previous)
            throw new ArgumentOutOfRangeException(nameof(now), "Observation time must be nonnegative and monotonic.");
        _lastUpdate = now;
        CurrentOffer = null;
        if (!enabled)
        {
            // Disabled monitoring supplies no evidence of continued absence.
            InvalidateObservation();
            return null;
        }

        foreach (var identity in _dismissed.Keys.ToArray())
        {
            if (identity == candidate) _dismissed[identity] = null;
            else if (_dismissed[identity] is not { } absentSince) _dismissed[identity] = now;
            else if (now - absentSince >= RearmPeriod) _dismissed.Remove(identity);
        }
        if (busy || candidate is null)
        {
            _candidate = null;
            return null;
        }
        if (_candidate != candidate)
        {
            _candidate = candidate;
            _candidateSince = now;
        }
        if (!_dismissed.ContainsKey(candidate) && now - _candidateSince >= StablePeriod)
            CurrentOffer = candidate;
        return CurrentOffer;
    }

    public void Dismiss(MeetingDetectionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        _dismissed[candidate] = null;
        if (CurrentOffer == candidate) CurrentOffer = null;
    }
}
