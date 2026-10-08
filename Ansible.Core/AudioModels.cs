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

    /// <summary>
    /// True when this model's native family carries authoritative spoken text in its plain display text,
    /// rather than annotated segment/speaker metadata that may omit speech content. Shared with transcript
    /// normalization so capability and normalization agree on what counts as authoritative speech.
    /// </summary>
    public bool HasPlainSpeechText => SpeechContent.IsPlainSpeechFamily(Family);

    /// <summary>Accepts microphone input and returns authoritative speech for final shortcut insertion.</summary>
    public bool CanInsertDictation => HasPlainSpeechText && Mode == "streaming";

    public override string ToString() => Id;
}

/// <summary>
/// Classifies which native families expose authoritative spoken text as plain display text. This is the single
/// source of truth shared by transcript normalization and shortcut capability, so both stay in agreement.
/// </summary>
public static class SpeechContent
{
    public static bool IsPlainSpeechFamily(string? family) =>
        family is "moonshine_asr" or "qwen3_asr" or "nemotron_asr";
}

public sealed record TranscriptUpdate(string Text, bool IsFinal, string? SpeechText = null);
