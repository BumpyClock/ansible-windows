using Ansible.Core;

namespace Ansible.Tests;

public sealed class StreamingTranscriptTests
{
    private static ModelOutput Delta(string text) => new(text, [], []);

    [Fact]
    public void LivePreviewSpeechExcludesLabelsAcrossDeltas()
    {
        var transcript = new StreamingTranscript("vibevoice_asr_streaming");
        var update = transcript.Append(Delta("Speaker 0: hello"));
        Assert.Equal("Speaker 0: hello", update.Text);
        Assert.False(update.IsFinal);
        Assert.Equal("hello", update.SpeechText);
        var next = transcript.Append(Delta(" Speaker 1: world"));
        Assert.Equal("Speaker 0: hello Speaker 1: world", next.Text);
        Assert.Equal("hello world", next.SpeechText);
    }

    [Fact]
    public void LabelSplitAcrossDeltasIsStillRemoved()
    {
        var transcript = new StreamingTranscript("vibevoice_asr_streaming");
        Assert.Equal("Speak", transcript.Append(Delta("Speak")).SpeechText);
        Assert.Equal("hello", transcript.Append(Delta("er 0: hello")).SpeechText);
    }

    [Fact]
    public void PlainDeltasPreserveNativeTokenSpacing()
    {
        var transcript = new StreamingTranscript("nemotron_asr");
        transcript.Append(Delta("hel"));
        Assert.Equal("hello world", transcript.Append(Delta("lo world")).SpeechText);
    }

    [Fact]
    public void AnnotatedPreviewSpeechStaysUnknownUntilMetadataArrives()
    {
        var transcript = new StreamingTranscript("vibevoice_asr");
        Assert.Null(transcript.Append(Delta("Speaker 0: hello")).SpeechText);
        Assert.Equal("hello", transcript.Append(new("", ["hello"], [])).SpeechText);
    }
}
