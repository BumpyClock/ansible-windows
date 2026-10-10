using System.Text.Json.Serialization;

namespace Ansible.Core;

public enum MeetingStatus { Recording, Paused, Completed, Interrupted, Starting, Finishing, Failed }

public enum MeetingTimingQuality { Unknown, Chunk, Aligned }
public enum MeetingAudioSource { Mixed, Microphone, Playback }

public sealed record MeetingTurn(string SpeakerId, double StartSeconds, double EndSeconds, string Text, bool IsProvisional = false)
{
    public MeetingTimingQuality TimingQuality { get; init; }
    public string? OriginalText { get; init; }
    public MeetingAudioSource Source { get; init; }
}

public sealed record MeetingDocument
{
    public int SchemaVersion { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; init; } = "New meeting";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public MeetingStatus Status { get; init; } = MeetingStatus.Recording;
    public IReadOnlyList<MeetingTurn> Turns { get; init; } = [];
    public IReadOnlyDictionary<string, string> SpeakerNames { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> AutoSpeakerNames
    {
        get;
        // Source-generated deserialization supplies null for this field in older saved meetings.
        init => field = value ?? new Dictionary<string, string>();
    } = new Dictionary<string, string>();

    public string GetSpeakerName(string speakerId) => SpeakerNames.TryGetValue(speakerId, out var name) ? name
        : AutoSpeakerNames.TryGetValue(speakerId, out var automaticName) ? automaticName : speakerId;

    internal void Validate()
    {
        if (SchemaVersion != 1 || Id == Guid.Empty || string.IsNullOrWhiteSpace(Title) || Title.Length > 500 ||
            CreatedAt == default || UpdatedAt < CreatedAt || !Enum.IsDefined(Status) || Turns is null || SpeakerNames is null || AutoSpeakerNames is null)
            throw new InvalidDataException("The meeting transcript has an unsupported or invalid format.");
        foreach (var turn in Turns)
        {
            if (turn is null || string.IsNullOrWhiteSpace(turn.SpeakerId) || turn.Text is null ||
                !double.IsFinite(turn.StartSeconds) || !double.IsFinite(turn.EndSeconds) ||
                turn.StartSeconds < 0 || turn.EndSeconds < turn.StartSeconds || turn.EndSeconds > TimeSpan.MaxValue.TotalSeconds ||
                !Enum.IsDefined(turn.TimingQuality) || !Enum.IsDefined(turn.Source))
                throw new InvalidDataException("The meeting transcript contains an invalid speaker turn.");
        }
        foreach (var speaker in SpeakerNames)
            if (string.IsNullOrWhiteSpace(speaker.Key) || string.IsNullOrWhiteSpace(speaker.Value))
                throw new InvalidDataException("The meeting transcript contains an invalid speaker name.");
        foreach (var speaker in AutoSpeakerNames)
            if (string.IsNullOrWhiteSpace(speaker.Key) || string.IsNullOrWhiteSpace(speaker.Value))
                throw new InvalidDataException("The meeting transcript contains an invalid automatic speaker name.");
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(MeetingDocument))]
internal partial class MeetingJsonContext : JsonSerializerContext;
