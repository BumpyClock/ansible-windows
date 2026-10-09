using Ansible.Core;

namespace Ansible.Tests;

public sealed class MeetingWordAlignerTests
{
    private const string LongPhrase = "Some people call me nature I am four point five billion years old and I will continue long after you";

    [Fact]
    public void UniqueEndpointsAnchorNumericDisagreementWithoutChangingSpeech()
    {
        var original = new MeetingTurn("Speaker 7", 0, 10, LongPhrase)
        {
            TimingQuality = MeetingTimingQuality.Chunk,
            Source = MeetingAudioSource.Playback,
            OriginalText = "Preserved original wording"
        };
        string[] pieces = ["Some", " people", " call", " me", " nature", " I", " am", " 4", ".5", " billion", " years", " old", " and", " I", " will", " continue", " long", " after", " you"];
        var tokens = pieces.Select((piece, index) => new MeetingTimedToken(piece, 16000 + index * 3200, 17600 + index * 3200)).ToArray();
        var aligned = Assert.Single(MeetingWordAlignment.Align([original], tokens, 10));
        Assert.Equal(1, aligned.StartSeconds);
        Assert.Equal(4.7, aligned.EndSeconds);
        Assert.Equal(MeetingTimingQuality.Aligned, aligned.TimingQuality);
        Assert.Equal(original.Text, aligned.Text);
        Assert.Equal(original.SpeakerId, aligned.SpeakerId);
        Assert.Equal(original.Source, aligned.Source);
        Assert.Equal(original.OriginalText, aligned.OriginalText);
        Assert.Equal(original.IsProvisional, aligned.IsProvisional);
    }

    [Fact]
    public void UniqueEndpointsAnchorOneInteriorSpellingDifference()
    {
        var turn = new MeetingTurn("Speaker", 0, 10, LongPhrase);
        var aligned = Assert.Single(MeetingWordAlignment.Align([turn], Tokens(LongPhrase.Replace("billion", "billon")), 10));
        Assert.Equal(MeetingTimingQuality.Aligned, aligned.TimingQuality);
        Assert.Equal(1, aligned.StartSeconds);
        Assert.Equal(4.9, aligned.EndSeconds);
        Assert.Equal(LongPhrase, aligned.Text);
    }

    [Fact]
    public void UnrelatedInteriorCannotBeHiddenByMatchingEndpoints()
    {
        var turn = new MeetingTurn("Speaker", 0, 10, LongPhrase);
        Assert.Equal(turn, Assert.Single(MeetingWordAlignment.Align([turn],
            Tokens("Some people call me red orange green blue purple yellow white black brown silver gold copper continue long after you"), 10)));
    }

    [Theory]
    [InlineData("Some people call me Some people call me nature I am forty five billion years old and I will continue long after you")]
    [InlineData("Some people call me nature I am forty five billion years old and I will continue long after you continue long after you")]
    public void RepeatedEndpointAnchorsAbstain(string recognized)
    {
        var turn = new MeetingTurn("Speaker", 0, 10, LongPhrase);
        Assert.Equal(turn, Assert.Single(MeetingWordAlignment.Align([turn], Tokens(recognized), 10)));
    }

    [Fact]
    public void ReversedEndpointAnchorsAbstain()
    {
        var turn = new MeetingTurn("Speaker", 0, 10, LongPhrase);
        Assert.Equal(turn, Assert.Single(MeetingWordAlignment.Align([turn],
            Tokens("continue long after you nature I am forty five billion years old and I will Some people call me"), 10)));
    }

    [Fact]
    public void MissingEndpointAnchorAbstainsDespiteMatchingInterior()
    {
        var turn = new MeetingTurn("Speaker", 0, 10, LongPhrase);
        Assert.Equal(turn, Assert.Single(MeetingWordAlignment.Align([turn],
            Tokens(LongPhrase.Replace("people call", "call")), 10)));
    }

    [Fact]
    public void EndpointSpanLengthMustRemainCloseToOriginalTurn()
    {
        var turn = new MeetingTurn("Speaker", 0, 10, LongPhrase);
        Assert.Equal(turn, Assert.Single(MeetingWordAlignment.Align([turn],
            Tokens(LongPhrase.Replace("nature", "nature red orange green blue purple yellow white")), 10)));
    }

    [Fact]
    public void PreviouslyAlignedTurnReservesItsAudioForOtherSpeakers()
    {
        var known = new MeetingTurn("First", 0.5, 1.5, "Known speech") { TimingQuality = MeetingTimingQuality.Aligned };
        var pending = new MeetingTurn("Second", 0, 10, "Hello") { TimingQuality = MeetingTimingQuality.Chunk };
        var result = MeetingWordAlignment.Align([known, pending], [new("Hello", 16000, 17600)], 10);
        Assert.Equal(known, result[0]);
        Assert.Equal(pending, result[1]);
    }

    [Fact]
    public void AdjacentTurnCanAlignWithoutReusingKnownAudio()
    {
        var known = new MeetingTurn("First", 0.5, 1, "Known speech") { TimingQuality = MeetingTimingQuality.Aligned };
        var pending = new MeetingTurn("Second", 0, 10, "Hello") { TimingQuality = MeetingTimingQuality.Chunk };
        var result = MeetingWordAlignment.Align([known, pending], [new("Hello", 16000, 17600)], 10);
        Assert.Equal(known, result[0]);
        Assert.Equal(1, result[1].StartSeconds);
        Assert.Equal(1.1, result[1].EndSeconds);
        Assert.Equal(MeetingTimingQuality.Aligned, result[1].TimingQuality);
    }

    private static MeetingTimedToken[] Tokens(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select((word, index) => new MeetingTimedToken(index == 0 ? word : " " + word,
            16000 + index * 3200, 17600 + index * 3200)).ToArray();

    [Fact]
    public void PaddingPunctuationOutsideClipDoesNotInvalidateEarlierSpeechAnchors()
    {
        var turn = new MeetingTurn("Speaker 0", 0, 3, "hello") { TimingQuality = MeetingTimingQuality.Chunk };
        var result = Assert.Single(MeetingWordAlignment.Align([turn],
            [new("hello", 16000, 17280), new(".", 48001, 49281)], 3));
        Assert.Equal(MeetingTimingQuality.Aligned, result.TimingQuality);
        Assert.Equal(1, result.StartSeconds);
        Assert.Equal(1.08, result.EndSeconds);
        var invalid = Assert.Single(MeetingWordAlignment.Align([turn],
            [new("hello", 16000, 17280), new("extra", 48001, 49281)], 3));
        Assert.Equal(turn, invalid);
    }
    [Fact]
    public void UniqueCompletePhraseUsesActualTokenBoundsAndPreservesSpeechMetadata()
    {
        var original = new MeetingTurn("speaker-7", 0, 10, "Hello, WORLD!", true)
        { TimingQuality = MeetingTimingQuality.Chunk, Source = MeetingAudioSource.Playback };
        var aligned = Assert.Single(MeetingWordAlignment.Align([original],
            [new("Hello", 32000, 35200), new(" world", 48000, 52800)], 10));
        Assert.Equal(2, aligned.StartSeconds);
        Assert.Equal(3.3, aligned.EndSeconds);
        Assert.Equal(MeetingTimingQuality.Aligned, aligned.TimingQuality);
        Assert.Equal("Hello, WORLD!", aligned.Text);
        Assert.Equal("speaker-7", aligned.SpeakerId);
        Assert.True(aligned.IsProvisional);
        Assert.Equal(MeetingAudioSource.Playback, aligned.Source);
    }

    [Fact]
    public void SubwordFragmentsUseTheirActualOuterBounds()
    {
        var turn = new MeetingTurn("speaker", 0, 10, "Goodbye everyone") { TimingQuality = MeetingTimingQuality.Chunk };
        var aligned = Assert.Single(MeetingWordAlignment.Align([turn],
            [new("Good", 16000, 17600), new("bye", 19200, 20800), new(" every", 32000, 33600), new("one", 35200, 36800)], 10));
        Assert.Equal(1, aligned.StartSeconds);
        Assert.Equal(2.3, aligned.EndSeconds);
        Assert.Equal(MeetingTimingQuality.Aligned, aligned.TimingQuality);
    }

    [Fact]
    public void MissingWordDoesNotInventAnInterval()
    {
        var turn = new MeetingTurn("speaker", 0, 10, "Finish the report") { TimingQuality = MeetingTimingQuality.Chunk };
        Assert.Equal(turn, Assert.Single(MeetingWordAlignment.Align([turn],
            [new("Finish", 16000, 17600), new(" report", 32000, 33600)], 10)));
    }

    [Fact]
    public void RepeatedWordWithoutUniqueContextAbstains()
    {
        var turn = new MeetingTurn("speaker", 0, 10, "Yes") { TimingQuality = MeetingTimingQuality.Chunk };
        Assert.Equal(turn, Assert.Single(MeetingWordAlignment.Align([turn],
            [new("Yes", 16000, 17600), new(" yes", 48000, 49600)], 10)));
    }

    [Fact]
    public void RepeatedPhraseAbstainsEvenWhenEachWordMatches()
    {
        var turn = new MeetingTurn("speaker", 0, 10, "Thank you") { TimingQuality = MeetingTimingQuality.Chunk };
        Assert.Equal(turn, Assert.Single(MeetingWordAlignment.Align([turn],
            [new("Thank", 16000, 17600), new(" you", 32000, 33600), new(" thank", 48000, 49600), new(" you", 64000, 65600)], 10)));
    }

    [Fact]
    public void TwoSpeakersCannotReuseTheSameRecognizedWords()
    {
        MeetingTurn[] turns = [new("a", 0, 10, "Agree"), new("b", 0, 10, "Agree")];
        Assert.Equal(turns, MeetingWordAlignment.Align(turns, [new("Agree", 16000, 17600)], 10));
    }

    [Fact]
    public void ConflictingTranscriptOrderDoesNotReorderSpeakerTurns()
    {
        MeetingTurn[] turns = [new("a", 0, 10, "Hello"), new("b", 0, 10, "Goodbye")];
        Assert.Equal(turns, MeetingWordAlignment.Align(turns,
            [new("Goodbye", 16000, 17600), new(" hello", 48000, 49600)], 10));
    }

    [Fact]
    public void OneTokenCoveringTwoWordsDoesNotInventIndividualWordBounds()
    {
        var turn = new MeetingTurn("speaker", 0, 10, "Hello world");
        Assert.Equal(turn, Assert.Single(MeetingWordAlignment.Align([turn], [new("Hello world", 16000, 17600)], 10)));
    }

    [Theory]
    [InlineData(-1, 17600)]
    [InlineData(16000, 16000)]
    [InlineData(16000, 160001)]
    public void InvalidNativeSampleBoundsAbstain(long start, long end)
    {
        var turn = new MeetingTurn("speaker", 0, 10, "Hello");
        Assert.Equal(turn, Assert.Single(MeetingWordAlignment.Align([turn], [new("Hello", start, end)], 10)));
    }

    [Fact]
    public void OutOfOrderNativeAnchorsAbstain()
    {
        var turn = new MeetingTurn("speaker", 0, 10, "Hello world");
        Assert.Equal(turn, Assert.Single(MeetingWordAlignment.Align([turn],
            [new("Hello", 48000, 49600), new(" world", 16000, 17600)], 10)));
    }

    [Fact]
    public void ExistingAlignedTimingIsPreserved()
    {
        var turn = new MeetingTurn("speaker", 5, 6, "Hello") { TimingQuality = MeetingTimingQuality.Aligned };
        Assert.Equal(turn, Assert.Single(MeetingWordAlignment.Align([turn], [new("Hello", 16000, 17600)], 10)));
    }

    [Fact]
    public void CancellationDoesNotReturnPartialCorrections()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => MeetingWordAlignment.Align([], [], 10, cancellation.Token));
    }

    [Theory]
    [InlineData("4.5", "four point five")]
    [InlineData("22,500", "twenty two thousand five hundred")]
    [InlineData("0.005", "zero point zero zero five")]
    [InlineData("999999999", "nine hundred ninety nine million nine hundred ninety nine thousand nine hundred ninety nine")]
    public void CompleteEnglishNumeralsAlignWithoutChangingOriginalText(string text, string spoken)
    {
        var turn = new MeetingTurn("original-speaker", 0, 10, text, true)
        { Source = MeetingAudioSource.Playback, OriginalText = "retained original", TimingQuality = MeetingTimingQuality.Chunk };
        var result = Assert.Single(MeetingWordAlignment.Align([turn], Tokens(spoken), 10));
        Assert.Equal(MeetingTimingQuality.Aligned, result.TimingQuality);
        Assert.Equal(1, result.StartSeconds);
        Assert.Equal(turn with { StartSeconds = result.StartSeconds, EndSeconds = result.EndSeconds, TimingQuality = MeetingTimingQuality.Aligned }, result);
    }

    [Fact]
    public void PublicSampleNumeralComparisonUsesWholeRecognizedPhrase()
    {
        const string text = "Some call me nature. Others call me Mother Nature. I've been here for over 4.5 billion years. 22,500 times longer than you.";
        var turn = new MeetingTurn("speaker", 0, 10, text);
        var tokens = Tokens("Some call me nature Others call me Mother Nature I've been here for over four point five billion years twenty two thousand five hundred times longer than you");
        var aligned = Assert.Single(MeetingWordAlignment.Align([turn], tokens, 10));
        Assert.Equal(MeetingTimingQuality.Aligned, aligned.TimingQuality);
        Assert.Equal(text, aligned.Text);
        Assert.Equal(tokens[^1].EndSample / 16000d, aligned.EndSeconds);
    }

    [Theory]
    [InlineData("04.5", "four point five")]
    [InlineData("case4.5", "case four point five")]
    [InlineData("4.5build", "four point five build")]
    [InlineData("-4.5", "four point five")]
    [InlineData("+4.5", "four point five")]
    [InlineData("4th", "four th")]
    [InlineData("4.1234567", "four point one two three four five six seven")]
    [InlineData("1,00", "one hundred")]
    [InlineData("4.5", "four five")]
    [InlineData("22,500", "twenty two five hundred")]
    public void IdentifiersInvalidNumbersAndIncompleteNumeralsAbstain(string text, string spoken)
    {
        var turn = new MeetingTurn("speaker", 0, 10, text);
        Assert.Equal(turn, Assert.Single(MeetingWordAlignment.Align([turn], Tokens(spoken), 10)));
    }

    [Fact]
    public void EndpointFallbackCannotMaskMissingNumericWord()
    {
        var turn = new MeetingTurn("speaker", 0, 10, "Some people call me nature I am 4.5 billion years old and I will continue long after you");
        Assert.Equal(turn, Assert.Single(MeetingWordAlignment.Align([turn],
            Tokens("Some people call me nature I am four five billion years old and I will continue long after you"), 10)));
    }

    [Fact]
    public void AmbiguousRawNumericPhraseIsNotRescuedByUniqueEnglishExpansion()
    {
        var turn = new MeetingTurn("speaker", 0, 10, "4.5");
        MeetingTimedToken[] tokens = [new("4", 16000, 17600), new(".", 17600, 18000), new("5", 18000, 19200),
            new(" 4", 32000, 33600), new(".", 33600, 34000), new("5", 34000, 35200),
            new(" four", 48000, 49600), new(" point", 51200, 52800), new(" five", 54400, 56000)];
        Assert.Equal(turn, Assert.Single(MeetingWordAlignment.Align([turn], tokens, 10)));
    }
}
