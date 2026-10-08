using System.Text;

namespace Ansible.Core;

/// <summary>Recognition output exactly as the native backend returned it, before any family-specific reading.</summary>
public sealed record ModelOutput(string Text, IReadOnlyList<string> Segments, IReadOnlyList<string> SpeakerTurns)
{
    public bool IsEmpty => Text.Length == 0 && Segments.Count == 0 && SpeakerTurns.Count == 0;
}

/// <summary>How a native family encodes spoken content in its output.</summary>
public enum SpeechLayout
{
    /// <summary>No reading rule exists, so spoken content is unknown and never inserted.</summary>
    Unknown,
    /// <summary>The whole text is spoken content.</summary>
    Plain,
    /// <summary>Spoken content is prefixed by <c>Speaker N:</c> diarization labels inside the text.</summary>
    SpeakerLabelled,
    /// <summary>Spoken content is only authoritative in segment or speaker-turn metadata.</summary>
    Annotated
}

/// <summary>
/// The single place that knows how each native family formats its output. Everything downstream, including
/// shortcut insertion and word counts, sees only the <see cref="RecognitionResult"/> produced here.
/// Add a family by giving it a layout; add a new cleanup by adding a layout.
/// </summary>
public static class SpeechExtraction
{
    public static SpeechLayout LayoutOf(string? family) => family switch
    {
        "moonshine_asr" or "qwen3_asr" or "nemotron_asr" => SpeechLayout.Plain,
        "vibevoice_asr_streaming" => SpeechLayout.SpeakerLabelled,
        "vibevoice_asr" => SpeechLayout.Annotated,
        _ => SpeechLayout.Unknown
    };

    /// <summary>True when the family's output can be read as spoken content for insertion and counting.</summary>
    public static bool IsSupported(string? family) => LayoutOf(family) != SpeechLayout.Unknown;

    public static RecognitionResult ToResult(string? family, ModelOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var speech = LayoutOf(family) switch
        {
            SpeechLayout.Plain => output.Text,
            SpeechLayout.SpeakerLabelled => StripSpeakerLabels(output.Text),
            SpeechLayout.Annotated => Join(output.Segments) ?? Join(output.SpeakerTurns),
            _ => null
        };
        return new RecognitionResult(output.Text, speech);
    }

    /// <summary>
    /// Removes <c>Speaker N:</c> labels and keeps every other character. The label grammar matches the upstream
    /// audio.cpp VibeVoice decoder: literal <c>Speaker</c>, optional whitespace, digits, optional whitespace, colon.
    /// Spoken phrases such as "speaker 0 is" survive because they lack the colon or the capitalized literal.
    /// </summary>
    internal static string StripSpeakerLabels(string text)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            if (TryReadLabel(text, index, out var labelEnd))
            {
                index = labelEnd;
                while (index < text.Length && char.IsWhiteSpace(text[index])) { index++; }
                continue;
            }
            builder.Append(text[index++]);
        }
        return builder.ToString().Trim();
    }

    private static bool TryReadLabel(string text, int start, out int end)
    {
        const string prefix = "Speaker";
        end = start;
        if (string.CompareOrdinal(text, start, prefix, 0, prefix.Length) != 0) { return false; }
        var position = start + prefix.Length;
        while (position < text.Length && char.IsWhiteSpace(text[position])) { position++; }
        var digits = position;
        while (position < text.Length && char.IsAsciiDigit(text[position])) { position++; }
        if (position == digits) { return false; }
        while (position < text.Length && char.IsWhiteSpace(text[position])) { position++; }
        if (position >= text.Length || text[position] != ':') { return false; }
        end = position + 1;
        return true;
    }

    private static string? Join(IReadOnlyList<string> pieces)
    {
        if (pieces.Count == 0 || pieces.Any(string.IsNullOrWhiteSpace)) { return null; }
        return string.Join(" ", pieces.Select(piece => piece.Trim()));
    }
}
