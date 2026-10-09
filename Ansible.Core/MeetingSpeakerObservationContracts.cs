namespace Ansible.Core;

public enum MeetingSpeakerReadStatus { Readable, Unavailable, Ambiguous }

public sealed record MeetingSpeakerObservation(long SamplePosition, IReadOnlyList<string> SpeakingNames,
    string? SelfName, MeetingSpeakerReadStatus Status);

public interface IMeetingSpeakerObserver : IAsyncDisposable
{
    Task StartAsync(int processId, Func<long> samplePosition, Action<MeetingSpeakerObservation> report,
        CancellationToken token);
    Task StopAsync();
}
