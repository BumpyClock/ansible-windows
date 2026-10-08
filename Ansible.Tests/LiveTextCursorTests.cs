using Ansible.Core;

namespace Ansible.Tests;

public sealed class LiveTextCursorTests
{
    [Fact]
    public void InsertsNewTextImmediatelyAndDoesNotRepeatItAtFinish()
    {
        var cursor = new LiveTextCursor();
        Assert.Equal("hello", cursor.Pending("hello"));
        cursor.ConfirmSent("hello");
        Assert.Equal(" world", cursor.Pending("hello world"));
        cursor.ConfirmSent("hello world");
        Assert.Equal("", cursor.Pending("hello world"));
        Assert.Equal("!", cursor.Pending("hello world!"));
    }

    [Fact]
    public void CoalescedSnapshotsDoNotLoseRecognizedWords()
    {
        var cursor = new LiveTextCursor();
        cursor.ConfirmSent("one");
        Assert.Equal(" two three four", cursor.Pending("one two three four"));
    }

    [Fact]
    public void StopsOnRevisionsRatherThanRewritingUserText()
    {
        var cursor = new LiveTextCursor();
        cursor.ConfirmSent("first words");
        Assert.Throws<InvalidDataException>(() => cursor.Pending("different words"));
        Assert.Equal("first words", cursor.SentText);
    }

    [Fact]
    public void PreservesUnicodeAndWhitespaceAcrossUpdates()
    {
        var cursor = new LiveTextCursor();
        cursor.ConfirmSent("hello");
        Assert.Equal(" 👋\nمرحبا", cursor.Pending("hello 👋\nمرحبا"));
    }

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
