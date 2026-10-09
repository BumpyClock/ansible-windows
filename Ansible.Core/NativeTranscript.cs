namespace Ansible.Core;

internal static class NativeTranscript
{
    public static RecognitionResult Normalize(
        string family, string displayText, IReadOnlyList<string> segments, IReadOnlyList<string> speakerTurns)
    {
        var speech = Join(segments) ?? Join(speakerTurns);
        if (speech is null && segments.Count == 0 && speakerTurns.Count == 0 &&
            family == "vibevoice_asr_streaming")
            speech = ReadSpeakerAttributedSpeech(displayText);
        if (speech is null && SpeechContent.IsPlainSpeechFamily(family))
            speech = displayText;
        // Annotated families without authoritative speech metadata have unknown spoken content.
        return new RecognitionResult(displayText, speech);
    }

    // audio.cpp's streaming finalize omits speaker turns; its display text uses these labels.
    private static string? ReadSpeakerAttributedSpeech(string text)
    {
        text = text.Trim();
        if (!TryReadSpeakerLabel(text, 0, out var speechStart))
            return null;
        List<string> pieces = [];
        while (true)
        {
            var next = speechStart;
            var nextSpeechStart = 0;
            while ((next = text.IndexOf("Speaker", next, StringComparison.Ordinal)) >= 0)
            {
                if (TryReadSpeakerLabel(text, next, out nextSpeechStart))
                    break;
                next += "Speaker".Length;
            }
            var piece = text[speechStart..(next < 0 ? text.Length : next)].Trim();
            if (piece.Length == 0)
                return null;
            pieces.Add(piece);
            if (next < 0)
                return string.Join(" ", pieces);
            speechStart = nextSpeechStart;
        }
    }

    private static bool TryReadSpeakerLabel(string text, int start, out int end)
    {
        end = start;
        if (!text.AsSpan(start).StartsWith("Speaker", StringComparison.Ordinal))
            return false;
        var index = start + "Speaker".Length;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        var digitsStart = index;
        while (index < text.Length && text[index] is >= '0' and <= '9')
            index++;
        if (index == digitsStart)
            return false;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        if (index >= text.Length || text[index] != ':')
            return false;
        end = index + 1;
        return true;
    }

    private static string? Join(IReadOnlyList<string> pieces)
    {
        if (pieces.Count == 0 || pieces.Any(string.IsNullOrWhiteSpace))
            return null;
        return string.Join(" ", pieces.Select(piece => piece.Trim()));
    }
}
