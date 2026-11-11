namespace Ansible.Core;

public enum MeetingCallPresence
{
    Unknown,
    Active,
    Absent
}

public sealed record MeetingContextObservation(MeetingCallPresence Presence, string? Title = null);

public interface IMeetingContextReader
{
    Task<MeetingContextObservation> ReadAsync(MeetingDetectionCandidate identity, CancellationToken cancellationToken);
}
