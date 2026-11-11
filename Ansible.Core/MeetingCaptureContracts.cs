using System.Threading.Channels;

namespace Ansible.Core;

/// <summary>
/// Meeting capture always records the microphone plus every active output device.
/// <paramref name="ProcessId"/> only identifies a Teams process for speaker naming; it does not limit capture.
/// </summary>
public sealed record MeetingCaptureRequest(int? ProcessId = null)
{
    public void Validate()
    {
        if (ProcessId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(ProcessId), "A meeting process ID must be positive.");
    }
}

public interface IMeetingCaptureFactory
{
    /// <summary>
    /// Returns a recording capture, or throws after releasing every device resource.
    /// Throws <see cref="MeetingCaptureOwnershipException"/> when cleanup could not release them.
    /// </summary>
    Task<IMeetingCapture> StartAsync(MeetingCaptureRequest request, CancellationToken cancellationToken);
}

public sealed record MeetingAudioActivity(long StartSample, int Samples, double MicrophoneRms, double PlaybackRms);

/// <summary>One time-aligned 16 kHz PCM16 capture frame. Each channel is retained separately so speaker attribution
/// never has to infer its source from a mixed waveform.</summary>
public sealed record MeetingAudioPacket(byte[] Microphone, byte[] Playback)
{
    public int Samples
    {
        get
        {
            if (Microphone is null || Playback is null || Microphone.Length != Playback.Length || Microphone.Length == 0 ||
                Microphone.Length % 2 != 0)
                throw new InvalidDataException("Meeting audio packets require matching, complete PCM16 channels.");
            return Microphone.Length / 2;
        }
    }
}

public interface IMeetingAudioSourceMetadata
{
    long ClockSamplePosition { get; }
    event Action<MeetingAudioActivity>? Activity;
}

/// <summary>
/// One recording with time-aligned microphone and output channels. Positions are 16 kHz samples relative to this capture's origin.
/// <see cref="IMeetingAudioSourceMetadata.Activity"/> and <see cref="IMeetingCaptureDiagnostics.HealthChanged"/> run on the capture thread.
/// </summary>
public interface IMeetingCapture : IAsyncDisposable, IMeetingAudioSourceMetadata, IMeetingCaptureDiagnostics
{
    ChannelReader<MeetingAudioPacket> Audio { get; }
    double CapturedSeconds { get; }
    bool IsReleased { get; }
    event Action<double>? LevelChanged;
    Task StopAsync();
}

public sealed class MeetingCaptureOwnershipException(string message, IMeetingCapture capture, Exception cause)
    : Exception(message, cause)
{
    public IMeetingCapture Capture { get; } = capture;
}
