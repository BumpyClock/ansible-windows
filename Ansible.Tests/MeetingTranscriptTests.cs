using Ansible.Core;

namespace Ansible.Tests;

public sealed class MeetingTranscriptTests
{
    [Fact]
    public void SplitLabelAndContinuationDoNotDuplicateSpeech()
    {
        var transcript = new MeetingTranscript();
        var first = transcript.Update("Speaker 0: Hello Spe", 5);
        Assert.Equal("Hello", Assert.Single(first).Text);
        var second = transcript.Update("Speaker 0: Hello Speaker 1: Good", 10);
        Assert.Equal(new[] { "Speaker 0", "Speaker 1" }, second.Select(turn => turn.SpeakerId));
        Assert.Equal(new[] { "Hello", "Good" }, second.Select(turn => turn.Text));
        Assert.Equal(0, second[0].StartSeconds);
        Assert.Equal(5, second[0].EndSeconds);
        Assert.Equal(5, second[1].StartSeconds);
        var final = transcript.Update("Speaker 0: Hello Speaker 1: Good morning", 15, isFinal: true);
        Assert.Equal(2, final.Count);
        Assert.Equal("Good morning", final[1].Text);
        Assert.Equal(15, final[1].EndSeconds);
        Assert.All(final, turn => Assert.False(turn.IsProvisional));
        Assert.Equal("Good", second[1].Text);
    }

    [Fact]
    public void UnlabelledPrefixRemainsUnknownAndDifferentTurnsRemainSeparate()
    {
        var turns = new MeetingTranscript().Update("Unattributed words Speaker 2: First Speaker 3: Reply Speaker 2: Last", 10);
        Assert.Equal(new[] { "Unknown speaker", "Speaker 2", "Speaker 3", "Speaker 2" }, turns.Select(turn => turn.SpeakerId));
        Assert.Equal(new[] { "Unattributed words", "First", "Reply", "Last" }, turns.Select(turn => turn.Text));
    }

    [Theory]
    [InlineData("S")]
    [InlineData("Speaker")]
    [InlineData("Speaker 12")]
    public void IncompleteLabelDoesNotBecomeSpeech(string fragment)
    {
        var transcript = new MeetingTranscript();
        Assert.Empty(transcript.Update(fragment, 5));
        var turns = transcript.Update("Speaker 12: Actual speech", 10);
        Assert.Equal("Speaker 12", Assert.Single(turns).SpeakerId);
        Assert.Equal("Actual speech", turns[0].Text);
    }

    [Fact]
    public void SimilarTextWithoutStrictLabelIsNotAssignedASpeaker()
    {
        var turns = new MeetingTranscript().Update("Speaker x: words speaker 2: words Speaker 3 words", 5, true);
        Assert.Equal("Unknown speaker", Assert.Single(turns).SpeakerId);
        Assert.Equal("Speaker x: words speaker 2: words Speaker 3 words", turns[0].Text);
    }

    [Fact]
    public void CumulativeCorrectionsReplaceEarlierText()
    {
        var transcript = new MeetingTranscript();
        transcript.Update("Speaker 00: Wrong", 5);
        var turns = transcript.Update("Speaker 0: Corrected", 10, true);
        Assert.Equal("Speaker 0", Assert.Single(turns).SpeakerId);
        Assert.Equal("Corrected", turns[0].Text);
        Assert.Equal(0, turns[0].StartSeconds);
        Assert.Equal(10, turns[0].EndSeconds);
    }

    [Fact]
    public void InvalidProcessingTimesAreRejected()
    {
        var transcript = new MeetingTranscript();
        transcript.Update("Speech", 5);
        Assert.Throws<ArgumentOutOfRangeException>(() => transcript.Update("Speech", 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => transcript.Update("Speech", double.NaN));
    }

    [Fact]
    public void FinalSpeechPreservesTrailingWordThatResemblesIncompleteLabel()
    {
        var transcript = new MeetingTranscript();
        Assert.Equal("Please say", Assert.Single(transcript.Update("Speaker 0: Please say Speaker", 5)).Text);
        var final = Assert.Single(transcript.Update("Speaker 0: Please say Speaker", 10, true));
        Assert.Equal("Please say Speaker", final.Text);
        Assert.False(final.IsProvisional);
    }

    [Fact]
    public void FinalUnlabelledFragmentIsPreservedWithoutInventedAttribution()
    {
        var final = Assert.Single(new MeetingTranscript().Update("Speaker 12", 5, true));
        Assert.Equal("Unknown speaker", final.SpeakerId);
        Assert.Equal("Speaker 12", final.Text);
    }

    [Fact]
    public void NativeWhitespaceFormsPreserveStableSpeakerIds()
    {
        var turns = new MeetingTranscript().Update("Speaker\t00 : First\nSpeaker1: Second", 5, true);
        Assert.Equal(new[] { "Speaker 0", "Speaker 1" }, turns.Select(turn => turn.SpeakerId));
        Assert.Equal(new[] { "First", "Second" }, turns.Select(turn => turn.Text));
    }
}
