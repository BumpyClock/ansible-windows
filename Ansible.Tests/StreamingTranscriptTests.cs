using Ansible.Core;

namespace Ansible.Tests;

public sealed class StreamingTranscriptTests
{
    [Fact]
    public void SpeechUpdatesExcludePresentationLabels()
    {
        var transcript = new StreamingTranscript();
        var update = transcript.Append(new("Speaker 0: hello", "hello"), true);
        Assert.Equal("Speaker 0: hello", update.Text);
        Assert.Equal("hello", update.SpeechText);
        Assert.Equal("hello world", transcript.Append(new(" Speaker 0: world", "world"), true).SpeechText);
    }

    [Fact]
    public void RawNativeDeltasPreserveTokenSpacingWithoutAddingSpaces()
    {
        var transcript = new StreamingTranscript();
        transcript.Append(new("hel", "hel"), false);
        Assert.Equal("hello world", transcript.Append(new("lo world", "lo world"), false).SpeechText);
    }

    [Fact]
    public void AnnotatedPreviewDoesNotBlockLaterAuthoritativeSpeechMetadata()
    {
        var transcript = new StreamingTranscript();
        Assert.Null(transcript.Append(new("Speaker 0: hello", null), false).SpeechText);
        Assert.Equal("hello", transcript.Append(new("", "hello"), true).SpeechText);
    }
}
