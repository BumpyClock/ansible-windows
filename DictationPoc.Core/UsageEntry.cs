using System.Text.Json.Serialization;

namespace DictationPoc.Core;

public enum UsageSource { Dictation, File }

public sealed record UsageEntry
{
    public required Guid Id { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required string ModelId { get; init; }
    public required UsageSource Source { get; init; }
    public required int Words { get; init; }
    public required double RecordingSeconds { get; init; }
    public required double ElapsedSeconds { get; init; }

    public void Validate()
    {
        if (Id == Guid.Empty || CompletedAt == default || string.IsNullOrWhiteSpace(ModelId) ||
            ModelId.Length > 128 || !Enum.IsDefined(Source) || Words < 0 ||
            !double.IsFinite(RecordingSeconds) || RecordingSeconds < 0 ||
            !double.IsFinite(ElapsedSeconds) || ElapsedSeconds < 0 ||
            Source == UsageSource.File && RecordingSeconds != 0)
        {
            throw new InvalidDataException("A local usage record contains invalid values.");
        }
    }
}

public sealed record UsageDocument
{
    public int SchemaVersion { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    public List<UsageEntry> Entries { get; init; } = [];
}

[JsonSerializable(typeof(UsageDocument))]
internal partial class UsageJsonContext : JsonSerializerContext;
