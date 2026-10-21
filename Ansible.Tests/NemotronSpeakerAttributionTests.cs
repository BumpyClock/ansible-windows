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
        Assert.Equal([Turn("Speaker 0", 0, .5, "Hello"), Turn("Speaker 1", .5, 1, "world!")], update.SpeakerTurns);
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
    [InlineData(0, 8000, "Unknown speaker")]
    [InlineData(4000, 12000, "Unknown speaker")]
    [InlineData(0, 12000, "Speaker 0")]
    [InlineData(16000, 32000, "Unknown speaker")]
    public void UncoveredSamplesCountAgainstTheMajority(long start, long end, string expected)
    {
        var update = NemotronSpeakerAttribution.Create([new("word", 0, 16000)],
            [new("speaker_0", start, end)], 32000, true);
        Assert.Equal([Turn(expected, 0, 1, "word")], update.SpeakerTurns);
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
            var expected = simultaneous ? "Unknown speaker" : coverage[0] >= 3 ? "Speaker 0"
                : coverage[1] >= 3 ? "Speaker 1" : "Unknown speaker";
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
