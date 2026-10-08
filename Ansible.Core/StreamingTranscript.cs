using System.Text;

namespace Ansible.Core;

internal sealed class StreamingTranscript
{
    private readonly StringBuilder _display = new();
    private readonly StringBuilder _speech = new();

    public TranscriptUpdate Append(RecognitionResult delta, bool separateSpeech)
    {
        _display.Append(delta.DisplayText);
        if (delta.SpeechText is not null)
        {
            if (separateSpeech && _speech.Length > 0 && delta.SpeechText.Length > 0 &&
                !char.IsWhiteSpace(_speech[^1]) && !char.IsWhiteSpace(delta.SpeechText[0]))
                _speech.Append(' ');
            _speech.Append(delta.SpeechText);
        }
        return new(_display.ToString(), false, _speech.Length > 0 ? _speech.ToString() : null);
    }
}
