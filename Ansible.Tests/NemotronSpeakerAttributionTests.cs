using System.Collections;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class NemotronSpeakerAttributionTests
{
    [Fact]
    public void GroupsSubwordsBeforeAttributionAndPreservesPunctuation()
    {
        var update = NemotronSpeakerAttribution.Create(
            [new("Hel", 0, 4000), new("lo", 4000, 8000), new(" world", 8000, 14000), new("!", 14000, 16000)],
            [new("speaker_0", 0, 6000), new("speaker_1", 6000, 16000)], 16000, true);

        Assert.Equal("Hello world!", update.Text);
        Assert.Equal(update.Text, update.SpeechText);
        Assert.True(update.IsFinal);
        Assert.Equal(16000L, update.ProcessedSamples);
        // The separate "!" token joins the word but, as punctuation, does not extend its timing.
        Assert.Equal([Turn("Speaker 0", 0, .5, "Hello"), Turn("Speaker 1", .5, .875, "world!")], update.SpeakerTurns);
    }

    [Fact]
    public void DelayedActivitiesAndRevisedCumulativeTokensReplaceTheSnapshot()
    {
        var early = NemotronSpeakerAttribution.Create([new("Hel", 0, 8000)], [], 16000, false);
        MeetingTimedToken[] tokens = [new("Hello", 0, 8000), new(" there.", 8000, 16000)];
        var later = NemotronSpeakerAttribution.Create(tokens, [new("speaker_7", 0, 16000)], 16000, false);
        var final = NemotronSpeakerAttribution.Create(tokens, [new("speaker_3", 0, 16000)], 16000, true);

        Assert.Equal([Turn("Unknown speaker", 0, .5, "Hel", true)], early.SpeakerTurns);
        Assert.Equal([Turn("Speaker 7", 0, 1, "Hello there.", true)], later.SpeakerTurns);
        Assert.Equal([Turn("Speaker 3", 0, 1, "Hello there.")], final.SpeakerTurns);
        Assert.Equal("Hello there.", final.SpeechText);
        Assert.True(final.IsFinal);
    }

    [Theory]
    [InlineData(12000, "Speaker 0")]
    [InlineData(8000, "Unknown speaker")]
    [InlineData(4000, "Speaker 1")]
    public void BoundaryRequiresStrictMajorityOfWholeWord(long boundary, string expected)
    {
        var update = NemotronSpeakerAttribution.Create([new("word", 0, 16000)],
            [new("speaker_0", 0, boundary), new("speaker_1", boundary, 16000)], 16000, true);
        Assert.Equal([Turn(expected, 0, 1, "word")], update.SpeakerTurns);
    }

    [Theory]
    [InlineData(0, 8000, "Speaker 0")]
    [InlineData(4000, 12000, "Speaker 0")]
    [InlineData(0, 12000, "Speaker 0")]
    [InlineData(15000, 16000, "Speaker 0")]
    [InlineData(16000, 32000, "Unknown speaker")]
    public void SilenceInsideTheWordDoesNotVote(long start, long end, string expected)
    {
        var update = NemotronSpeakerAttribution.Create([new("word", 0, 16000)],
            [new("speaker_0", start, end)], 32000, true);
        Assert.Equal([Turn(expected, 0, 1, "word")], update.SpeakerTurns);
    }

    [Fact]
    public void SilenceDoesNotRescueATieOrAMinorityOfDiarizedSpeech()
    {
        var tie = NemotronSpeakerAttribution.Create([new("word", 0, 16000)],
            [new("speaker_0", 0, 2000), new("speaker_1", 10000, 12000)], 16000, true);
        Assert.Equal([Turn("Unknown speaker", 0, 1, "word")], tie.SpeakerTurns);
        var minority = NemotronSpeakerAttribution.Create([new("word", 0, 16000)],
            [new("speaker_0", 0, 2000), new("speaker_1", 10000, 13000)], 16000, true);
        Assert.Equal([Turn("Speaker 1", 0, 1, "word")], minority.SpeakerTurns);
    }

    [Theory]
    [InlineData(112160, "Speaker 0")]  // Nemotron closed "for" 30 ms after its speaker's activity ended (2026-10-22).
    [InlineData(107521, "Speaker 0")]  // One sample inside the 320 ms emission allowance.
    [InlineData(107520, "Unknown speaker")]
    public void WordEmittedJustAfterItsSpeakerStoppedUsesTheEmissionAllowance(long activityEnd, string expected)
    {
        var update = NemotronSpeakerAttribution.Create([new(" for", 112640, 113920)],
            [new("speaker_0", 94400, activityEnd)], 120000, true);
        Assert.Equal([Turn(expected, 7.04, 7.12, "for")], update.SpeakerTurns);
    }

    [Fact]
    public void EmissionAllowanceStillAbstainsOnOverlapAndNeverLooksBeforeZero()
    {
        var overlap = NemotronSpeakerAttribution.Create([new(" for", 112640, 113920)],
            [new("speaker_0", 94400, 112160), new("speaker_1", 111000, 112000)], 120000, true);
        Assert.Equal([Turn("Unknown speaker", 7.04, 7.12, "for")], overlap.SpeakerTurns);
        var first = NemotronSpeakerAttribution.Create([new("Hi", 0, 1280)], [new("speaker_0", 2000, 3000)], 16000, true);
        Assert.Equal([Turn("Unknown speaker", 0, .08, "Hi")], first.SpeakerTurns);
    }

    [Fact]
    public void PunctuationEmittedAtThePauseDoesNotHandTheWordToTheNextSpeaker()
    {
        // Nemotron emitted "." one second after "nature", once speaker 1 had already started (2026-10-22 timing).
        var update = NemotronSpeakerAttribution.Create(
            [new(" na", 78080, 79360), new("ture", 80640, 81920), new(".", 97280, 98560), new(" Yes", 98560, 99840)],
            [new("speaker_0", 46720, 80800), new("speaker_1", 94400, 112160)], 112160, true);
        Assert.Equal([Turn("Speaker 0", 4.88, 5.12, "nature."), Turn("Speaker 1", 6.16, 6.24, "Yes")], update.SpeakerTurns);
        var punctuationOnly = NemotronSpeakerAttribution.Create(
            [new("Hi", 0, 8000), new(" -", 8000, 12000), new(" there", 12000, 16000)], [new("speaker_0", 0, 16000)], 16000, true);
        Assert.Equal([Turn("Speaker 0", 0, 1, "Hi - there")], punctuationOnly.SpeakerTurns);
    }

    [Fact]
    public void RealNemotronTimingOfThePublicSampleKeepsEveryPhraseWithItsSpeaker()
    {
        // Native Nemotron tokens (80 ms emission frames) and diarizer activity for the public sample with a
        // two-second pause inserted after "for" and at the end, captured on 2026-10-22.
        (int Frame, string Text)[] frames = [(12, "So"), (12, "me"), (18, " call"), (22, " "), (22, "me"), (25, " na"), (26, "ture"),
            (41, " other"), (43, "s"), (48, " call"), (51, " "), (51, "me"), (58, " "), (58, "m"), (58, "other"), (61, " na"), (63, "ture"),
            (76, "."), (76, " "), (78, " "), (78, "I"), (80, "'"), (80, "ve"), (82, " be"), (82, "en"), (84, " her"), (84, "e"), (88, " for"),
            (117, " over"), (122, " four"), (125, " po"), (127, "in"), (127, "t"), (129, " five"), (136, " "), (136, "bil"), (137, "li"),
            (137, "on"), (141, " year"), (142, "s"), (165, " twenty"), (167, " two"), (170, " t"), (170, "ho"), (170, "us"), (172, "and"),
            (177, " five"), (180, " hundred"), (184, " time"), (186, "s"), (188, " "), (188, "lo"), (190, "ng"), (190, "er"), (194, " t"),
            (194, "h"), (194, "an"), (197, " you")];
        var tokens = frames.Select(token => new MeetingTimedToken(token.Text, token.Frame * 1280L, (token.Frame + 1) * 1280L)).ToArray();
        SpeakerActivity[] activities = [new("speaker_0", 6400, 32800), new("speaker_0", 46720, 80800),
            new("speaker_0", 94400, 112160), new("speaker_0", 144480, 186720), new("speaker_0", 202560, 253440)];
        var update = NemotronSpeakerAttribution.Create(tokens, activities, 289151, true);
        Assert.Equal([
            Turn("Speaker 0", 0.96, 2.16, "Some call me nature"),
            Turn("Speaker 0", 3.28, 5.12, "others call me mother nature."),
            Turn("Speaker 0", 6.24, 7.12, "I've been here for"),
            Turn("Speaker 0", 9.36, 11.44, "over four point five billion years"),
            Turn("Speaker 0", 13.2, 15.84, "twenty two thousand five hundred times longer than you")], update.SpeakerTurns);
    }

    [Fact]
    public void RealKrokoTimingOfThePublicSampleKeepsEveryPhraseWithItsSpeaker()
    {
        // Kroko words end where the next word starts, so pauses sit inside word spans (2026-10-22 timing).
        (int Start, int End, string Text)[] words = [(840, 1400, "Some"), (1400, 1640, " call"), (1640, 1920, " me"), (1920, 3160, " nature,"),
            (3160, 3920, " others"), (3920, 4160, " call"), (4160, 4440, " me"), (4440, 4840, " Mother"), (4840, 6160, " Nature."),
            (6160, 6600, " I've"), (6600, 6880, " been"), (6880, 7160, " here"), (7160, 7680, " for"), (7680, 9680, " over"),
            (9680, 10040, " four"), (10040, 10440, " point"), (10440, 10840, " five"), (10840, 11440, " billion"), (11440, 12960, " years,"),
            (12960, 13360, " twenty"), (13360, 13560, " two"), (13560, 14160, " thousand"), (14160, 14440, " five"), (14440, 14800, " hundred"),
            (14800, 15200, " times"), (15200, 15680, " longer"), (15680, 15920, " than")];
        var tokens = words.Select(word => new MeetingTimedToken(word.Text, word.Start * 16L, word.End * 16L))
            .Append(new(" you", 15920 * 16L, 289151)).ToArray();
        SpeakerActivity[] activities = [new("speaker_0", 6400, 32800), new("speaker_0", 46720, 80800),
            new("speaker_0", 94400, 112160), new("speaker_0", 144480, 186720), new("speaker_0", 202560, 253440)];
        var update = NemotronSpeakerAttribution.Create(tokens, activities, 289151, true);
        Assert.Equal([
            Turn("Speaker 0", 0.84, 10.84, "Some call me nature, others call me Mother Nature. I've been here for over four point five"),
            Turn("Speaker 0", 10.84, 289151 / 16000d, "billion years, twenty two thousand five hundred times longer than you")],
            update.SpeakerTurns);
    }

    [Fact]
    public void BriefSimultaneousSpeechMakesOnlyItsWordUnknown()
    {
        var update = NemotronSpeakerAttribution.Create(
            [new("Unclear", 0, 16000), new(" clear.", 16000, 32000)],
            [new("speaker_0", 0, 32000), new("speaker_1", 7000, 7001)], 32000, true);
        Assert.Equal([Turn("Unknown speaker", 0, 1, "Unclear"), Turn("Speaker 0", 1, 2, "clear.")],
            update.SpeakerTurns);
        Assert.Equal("Unclear clear.", update.SpeechText);
    }

    [Fact]
    public void SameSpeakerOverlapsAndDuplicatesAreUnionedNotDoubleCounted()
    {
        var update = NemotronSpeakerAttribution.Create([new("word", 0, 16000)],
            [new("speaker_0", 0, 5000), new("speaker_0", 0, 5000), new("speaker_0", 2000, 5000),
                new("speaker_1", 5000, 16000)], 16000, true);
        Assert.Equal([Turn("Speaker 1", 0, 1, "word")], update.SpeakerTurns);
    }

    [Fact]
    public void InterleavedActivitiesKeepNativeSpeakerIdsAndOnlyLastLiveTurnIsProvisional()
    {
        var update = NemotronSpeakerAttribution.Create(
            [new("One.", 0, 16000), new(" Two.", 16000, 32000), new(" Three.", 32000, 48000)],
            [new("speaker_7", 32000, 48000), new("speaker_1", 16000, 32000), new("speaker_7", 0, 16000)],
            48000, false);
        Assert.Equal([Turn("Speaker 7", 0, 1, "One."), Turn("Speaker 1", 1, 2, "Two."),
            Turn("Speaker 7", 2, 3, "Three.", true)], update.SpeakerTurns);
    }

    [Fact]
    public void SplitsTurnsAtTenSecondsAndPausesOverOneSecondWithoutSplittingWords()
    {
        var update = NemotronSpeakerAttribution.Create(
            [new("A", 0, 80000), new(" B", 80000, 160000), new(" C", 160000, 176000),
                new(" D", 192000, 208000), new(" E.", 224001, 240001)],
            [new("speaker_2", 0, 240001)], 240001, false);
        Assert.Equal([Turn("Speaker 2", 0, 10, "A B"), Turn("Speaker 2", 10, 13, "C D"),
            Turn("Speaker 2", 224001 / 16000d, 240001 / 16000d, "E.", true)], update.SpeakerTurns);

        var longWord = NemotronSpeakerAttribution.Create(
            [new("Un", 0, 160000), new("broken.", 160000, 176000)],
            [new("speaker_0", 0, 176000)], 176000, true);
        Assert.Equal([Turn("Speaker 0", 0, 11, "Unbroken.")], longWord.SpeakerTurns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SilenceHasEmptyStructuredSpeech(bool final)
    {
        var update = NemotronSpeakerAttribution.Create([], [new("speaker_0", 0, 16000)], 16000, final);
        Assert.Equal("", update.Text);
        Assert.Equal("", update.SpeechText);
        Assert.Equal(final, update.IsFinal);
        Assert.Equal(16000L, update.ProcessedSamples);
        Assert.NotNull(update.SpeakerTurns);
        Assert.Empty(update.SpeakerTurns);
    }

    [Fact]
    public void FlushTailRetainsSpeechButClampsUncertainTimingAndDoesNotAssignSpeaker()
    {
        var update = NemotronSpeakerAttribution.Create(
            [new("Good", 0, 8000), new(" tail", 8000, 18000), new("!", 18000, 20000),
                new(" beyond.", long.MaxValue, long.MaxValue)],
            [new("speaker_0", 0, 16000)], 16000, true);
        Assert.Equal("Good tail! beyond.", update.SpeechText);
        Assert.Equal([Turn("Speaker 0", 0, .5, "Good"),
            Turn("Unknown speaker", .5, 1, "tail! beyond.", quality: MeetingTimingQuality.Unknown)],
            update.SpeakerTurns);
    }

    [Fact]
    public void UncertainSubwordMakesWholeWordUnknownAndZeroLengthWordIsNotAligned()
    {
        var update = NemotronSpeakerAttribution.Create(
            [new("Hel", 0, 8000), new("lo", 8000, 16001)], [new("speaker_0", 0, 16000)], 16000, false);
        Assert.Equal([Turn("Unknown speaker", 0, 1, "Hello", true, MeetingTimingQuality.Unknown)], update.SpeakerTurns);
        var zero = NemotronSpeakerAttribution.Create([new("word", 0, 0)], [], 16000, true);
        Assert.Equal([Turn("Unknown speaker", 0, 0, "word", quality: MeetingTimingQuality.Unknown)], zero.SpeakerTurns);
    }

    [Fact]
    public void NonmonotonicTokenEndsDoNotShrinkWordBounds()
    {
        var update = NemotronSpeakerAttribution.Create(
            [new("Hel", 0, 12000), new("lo", 4000, 8000), new(" there", 8000, 16000)],
            [new("speaker_0", 0, 9000), new("speaker_1", 9000, 16000)], 16000, true);
        Assert.Equal([Turn("Speaker 0", 0, .75, "Hello"), Turn("Speaker 1", .5, 1, "there")], update.SpeakerTurns);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void TrailingWhitespaceInPieceDoesNotJoinNextWordOrBorrowItsTiming(string space)
    {
        var update = NemotronSpeakerAttribution.Create(
            [new("Hello" + space, 0, 8000), new("world!", 8000, 16000)],
            [new("speaker_0", 0, 8000), new("speaker_1", 8000, 16000)], 16000, true);
        Assert.Equal("Hello" + space + "world!", update.SpeechText);
        Assert.Equal([Turn("Speaker 0", 0, .5, "Hello"), Turn("Speaker 1", .5, 1, "world!")], update.SpeakerTurns);
    }

    [Fact]
    public void WhitespaceAndEmptyTokensDoNotDropSpeechOrCreateBlankTurns()
    {
        var update = NemotronSpeakerAttribution.Create(
            [new("", 0, 0), new("  ", 0, 1), new("Hi", 1, 8000), new("\t", 8000, 8001),
                new("there!", 8001, 16000), new("  ", 16000, 16000)],
            [new("speaker_0", 0, 16000)], 16000, true);
        Assert.Equal("Hi\tthere!", update.SpeechText);
        Assert.Equal([Turn("Speaker 0", 1 / 16000d, 1, "Hi\tthere!")], update.SpeakerTurns);
    }

    [Fact]
    public void DoesNotMutateCallerCollections()
    {
        MeetingTimedToken[] tokens = [new("Hi", 0, 8000), new(" again", 8000, 16000)];
        SpeakerActivity[] activities = [new("speaker_1", 8000, 16000), new("speaker_0", 0, 8000)];
        var originalTokens = tokens.ToArray();
        var originalActivities = activities.ToArray();
        NemotronSpeakerAttribution.Create(Array.AsReadOnly(tokens), Array.AsReadOnly(activities), 16000, true);
        Assert.Equal(originalTokens, tokens);
        Assert.Equal(originalActivities, activities);
    }

    [Fact]
    public void RejectsNullInputsAndElements()
    {
        Assert.Throws<ArgumentNullException>(() => NemotronSpeakerAttribution.Create(null!, [], 0, true));
        Assert.Throws<ArgumentNullException>(() => NemotronSpeakerAttribution.Create([], null!, 0, true));
        Assert.Throws<ArgumentException>(() => NemotronSpeakerAttribution.Create([null!], [], 0, true));
        Assert.Throws<ArgumentException>(() => NemotronSpeakerAttribution.Create([new(null!, 0, 0)], [], 0, true));
        Assert.Throws<ArgumentException>(() => NemotronSpeakerAttribution.Create([], [null!], 0, true));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(115200001)]
    [InlineData(long.MaxValue)]
    public void RejectsProcessedSamplesBeyondTwoHours(long samples) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NemotronSpeakerAttribution.Create([], [], samples, true));

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(2, 1)]
    public void RejectsInvalidTokenBounds(long start, long end) =>
        Assert.Throws<ArgumentException>(() => NemotronSpeakerAttribution.Create([new("bad", start, end)], [], 16000, true));

    [Fact]
    public void RejectsOutOfOrderStarts() =>
        Assert.Throws<ArgumentException>(() => NemotronSpeakerAttribution.Create(
            [new("first", 10, 20), new(" second", 9, 30)], [], 16000, true));

    [Theory]
    [InlineData(null, 0, 1)]
    [InlineData("", 0, 1)]
    [InlineData("Speaker 0", 0, 1)]
    [InlineData("speaker_8", 0, 1)]
    [InlineData("speaker_00", 0, 1)]
    [InlineData("speaker_-1", 0, 1)]
    [InlineData("speaker_0", -1, 1)]
    [InlineData("speaker_0", 2, 1)]
    [InlineData("speaker_0", 1, 1)]
    [InlineData("speaker_0", 0, 16001)]
    public void RejectsInvalidActivitiesEvenWithoutSpeech(string? speaker, long start, long end) =>
        Assert.Throws<ArgumentException>(() => NemotronSpeakerAttribution.Create([], [new(speaker!, start, end)], 16000, true));

    [Fact]
    public void BoundsMetadataAndTextAllocations()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NemotronSpeakerAttribution.Create(new MeetingTimedToken[262145], [], 0, true));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NemotronSpeakerAttribution.Create([], new SpeakerActivity[100001], 0, true));
        Assert.Throws<ArgumentException>(() =>
            NemotronSpeakerAttribution.Create([new(new string('x', 4 * 1024 * 1024 + 1), 0, 1)], [], 1, true));
    }

    [Fact]
    public void AcceptsTwoHoursAndMaximumMetadata()
    {
        var tokens = Enumerable.Range(0, 262144)
            .Select(i => new MeetingTimedToken(" x", i * 400L, (i + 1) * 400L)).ToArray();
        var activities = Enumerable.Range(0, 100000)
            .Select(i => new SpeakerActivity("speaker_0", i * 1152L, (i + 1) * 1152L)).ToArray();
        var update = NemotronSpeakerAttribution.Create(tokens, activities, 115200000, true);
        Assert.Equal(115200000L, update.ProcessedSamples);
        Assert.Equal(string.Join(" ", Enumerable.Repeat("x", 262144)), update.SpeechText);
        Assert.Equal(656, update.SpeakerTurns!.Count);
        Assert.Equal(Turn("Speaker 0", 0, 10, string.Join(" ", Enumerable.Repeat("x", 400))), update.SpeakerTurns[0]);
        Assert.Equal(Turn("Speaker 0", 6550, 6553.6, string.Join(" ", Enumerable.Repeat("x", 144))), update.SpeakerTurns[^1]);
        Assert.All(update.SpeakerTurns, turn => Assert.False(turn.IsProvisional));
    }

    [Fact]
    public void ObservesCancellationBeforeWorkAndWhileReadingBothInputs()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => NemotronSpeakerAttribution.Create([], [], 0, true, canceled.Token));
        using var tokenCancellation = new CancellationTokenSource();
        var tokens = new CancelingList<MeetingTimedToken>(
            Enumerable.Repeat(new MeetingTimedToken(" x", 0, 1), 1024).ToArray(), tokenCancellation);
        Assert.Throws<OperationCanceledException>(() =>
            NemotronSpeakerAttribution.Create(tokens, [], 16000, true, tokenCancellation.Token));
        using var activityCancellation = new CancellationTokenSource();
        var activities = new CancelingList<SpeakerActivity>(
            Enumerable.Repeat(new SpeakerActivity("speaker_0", 0, 1), 1024).ToArray(), activityCancellation);
        Assert.Throws<OperationCanceledException>(() =>
            NemotronSpeakerAttribution.Create([], activities, 16000, true, activityCancellation.Token));
    }

    [Fact]
    public void MatchesDiscreteOracleForEveryFiveSampleTwoSpeakerTimeline()
    {
        for (var pattern = 0; pattern < 1024; pattern++)
        {
            var activities = new List<SpeakerActivity>();
            var coverage = new int[2];
            var simultaneous = false;
            for (var sample = 0; sample < 5; sample++)
            {
                var mask = (pattern >> (sample * 2)) & 3;
                simultaneous |= mask == 3;
                for (var speaker = 0; speaker < 2; speaker++)
                {
                    if ((mask & (1 << speaker)) == 0) continue;
                    coverage[speaker]++;
                    activities.Add(new($"speaker_{speaker}", sample, sample + 1));
                }
            }
            activities.Reverse();
            // Only diarized samples vote; the word starts at zero, so nothing precedes it to look back into.
            var expected = simultaneous ? "Unknown speaker" : coverage[0] > coverage[1] ? "Speaker 0"
                : coverage[1] > coverage[0] ? "Speaker 1" : "Unknown speaker";
            var update = NemotronSpeakerAttribution.Create([new("word", 0, 5)], activities, 5, true);
            Assert.Equal([Turn(expected, 0, 5 / 16000d, "word")], update.SpeakerTurns);
        }
    }

    private static MeetingTurn Turn(string speaker, double start, double end, string text,
        bool provisional = false, MeetingTimingQuality quality = MeetingTimingQuality.Aligned) =>
        new(speaker, start, end, text, provisional) { TimingQuality = quality };

    private sealed class CancelingList<T>(T[] values, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public int Count => values.Length;
        public T this[int index]
        {
            get
            {
                if (index == 512) cancellation.Cancel();
                return values[index];
            }
        }
        public IEnumerator<T> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return this[i];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
