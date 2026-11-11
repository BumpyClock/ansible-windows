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
        var labels = MeetingSpeakerLabels.Find(text);
        if (labels.Count == 0 || labels[0].Index != 0)
            return null;
        List<string> pieces = [];
        for (var index = 0; index < labels.Count; index++)
        {
            var start = labels[index].Index + labels[index].Length;
            var end = index + 1 < labels.Count ? labels[index + 1].Index : text.Length;
            var piece = text[start..end].Trim();
            if (piece.Length == 0)
                return null;
            pieces.Add(piece);
        }
        return string.Join(" ", pieces);
    }

    private static string? Join(IReadOnlyList<string> pieces)
    {
        if (pieces.Count == 0 || pieces.Any(string.IsNullOrWhiteSpace))
            return null;
        return string.Join(" ", pieces.Select(piece => piece.Trim()));
    }
}
