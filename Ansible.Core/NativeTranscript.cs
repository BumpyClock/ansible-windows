namespace Ansible.Core;

internal static class NativeTranscript
{
    public static RecognitionResult Normalize(
        string family, string displayText, IReadOnlyList<string> segments, IReadOnlyList<string> speakerTurns)
    {
        var speech = Join(segments) ?? Join(speakerTurns);
        if (speech is null && SpeechContent.IsPlainSpeechFamily(family))
            speech = displayText;
        // Annotated families without authoritative speech metadata have unknown spoken content.
        return new RecognitionResult(displayText, speech);
    }

    private static string? Join(IReadOnlyList<string> pieces)
    {
        if (pieces.Count == 0 || pieces.Any(string.IsNullOrWhiteSpace))
            return null;
        return string.Join(" ", pieces.Select(piece => piece.Trim()));
    }
}
