using System.Text;

namespace Ansible.Core;

/// <summary>
/// Accumulates native stream deltas and re-reads the whole output after each one, so a diarization label split
/// across two deltas is still removed from the live speech preview.
/// </summary>
internal sealed class StreamingTranscript(string? family)
{
    private readonly StringBuilder _text = new();
    private readonly List<string> _segments = [];
    private readonly List<string> _speakerTurns = [];

    public TranscriptUpdate Append(ModelOutput delta)
    {
        _text.Append(delta.Text);
        _segments.AddRange(delta.Segments);
        _speakerTurns.AddRange(delta.SpeakerTurns);
        var result = SpeechExtraction.ToResult(family, new ModelOutput(_text.ToString(), _segments, _speakerTurns));
        return new TranscriptUpdate(result.DisplayText, false, result.SpeechText);
    }
}
