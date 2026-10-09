using System.Threading.Channels;

namespace Ansible.Core;

public enum MeetingPlaybackSource { MeetingApp, SystemAudio }

public sealed record MeetingCaptureRequest(MeetingPlaybackSource PlaybackSource, int? ProcessId = null)
{
    public void Validate()
    {
        if (!Enum.IsDefined(PlaybackSource)) throw new ArgumentOutOfRangeException(nameof(PlaybackSource));
        if (PlaybackSource == MeetingPlaybackSource.MeetingApp && ProcessId is not > 0)
            throw new ArgumentException("Select a meeting application to capture its audio.", nameof(ProcessId));
    }
}

public interface IMeetingCaptureFactory
{
    Task<IAudioCapture> StartAsync(MeetingCaptureRequest request, CancellationToken cancellationToken);
}
