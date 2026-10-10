namespace Ansible.Core;

public sealed record MeetingDetectionCandidate(int ProcessId, DateTime ProcessStartedUtc);

public interface IMeetingDetector
{
    Task<MeetingDetectionCandidate?> ReadAsync(CancellationToken cancellationToken);
}

public sealed class MeetingDetectionUnavailableException(string message, Exception? cause = null)
    : IOException(message, cause);
