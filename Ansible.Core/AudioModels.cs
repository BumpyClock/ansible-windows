using System.Text.Json.Serialization;

namespace Ansible.Core;

public sealed record AudioModel
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("family")]
    public string? Family { get; init; }

    [JsonPropertyName("task")]
    public string? Task { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    public string? ModelPath { get; init; }
    public string? Preview { get; init; }
    public string? DisplayName { get; init; }
    public bool SupportsCustomDictionary { get; init; }

    /// <summary>Accepts microphone input and returns readable speech for final shortcut insertion.</summary>
    public bool CanInsertDictation => SpeechExtraction.IsSupported(Family) && Mode == "streaming";

    public override string ToString() => Id;
}

public sealed record TranscriptUpdate(string Text, bool IsFinal, string? SpeechText = null);
