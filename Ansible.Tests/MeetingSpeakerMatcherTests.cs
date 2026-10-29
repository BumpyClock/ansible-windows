using Ansible.Core;

namespace Ansible.Tests;

public sealed class MeetingSpeakerMatcherTests
{
    [Fact]
    public void MissingObservationsClearOldAutomaticNamesWithoutChangingManualNamesOrSpeech()
    {
        var document = Document(Turn("Speaker 0", 1, 3)) with
        {
            AutoSpeakerNames = new Dictionary<string, string> { ["Speaker 0"] = "Old observation" },
            SpeakerNames = new Dictionary<string, string> { ["Speaker 0"] = "Chosen name" }
        };
        var refined = new MeetingSpeakerMatcher().Refine(document);
        Assert.Empty(refined.AutoSpeakerNames);
        Assert.Equal("Chosen name", refined.GetSpeakerName("Speaker 0"));
        Assert.Equal(document.Turns, refined.Turns);
    }

    [Fact]
    public void TwoIndependentCoveredTurnsNameSpeakerWithoutChangingSpeech()
    {
        var matcher = Observations(0, 10, "Alex");
        var document = Document(Turn("Speaker 0", 1, 3), Turn("Speaker 0", 5, 7));
        Assert.Empty(matcher.Refine(Document(document.Turns[0])).AutoSpeakerNames);
        var refined = matcher.Refine(document);
        Assert.Equal("Alex", refined.GetSpeakerName("Speaker 0"));
        Assert.Equal(document.Turns, refined.Turns);
        Assert.Equal(document.Title, refined.Title);
        Assert.Equal(refined.AutoSpeakerNames, matcher.Refine(refined).AutoSpeakerNames);
    }

    [Fact]
    public void ManualNamesOverrideAutomaticNamesAndAreNeverChanged()
    {
        var document = Document(Turn("Speaker 0", 1, 3), Turn("Speaker 0", 5, 7)) with
        { SpeakerNames = new Dictionary<string, string> { ["Speaker 0"] = "My chosen name" } };
        var refined = Observations(0, 10, "Alex").Refine(document);
        Assert.Equal("My chosen name", refined.GetSpeakerName("Speaker 0"));
        Assert.Equal(document.SpeakerNames, refined.SpeakerNames);
    }

    [Theory]
    [InlineData(MeetingAudioSource.Mixed, MeetingTimingQuality.Chunk)]
    [InlineData(MeetingAudioSource.Playback, MeetingTimingQuality.Unknown)]
    [InlineData(MeetingAudioSource.Playback, MeetingTimingQuality.Chunk)]
    public void UncertainSourceOrTimingAbstains(MeetingAudioSource source, MeetingTimingQuality quality)
    {
        var turns = new[] { Turn("Speaker 0", 1, 3) with { Source = source, TimingQuality = quality },
            Turn("Speaker 0", 5, 7) with { Source = source, TimingQuality = quality } };
        Assert.Empty(Observations(0, 10, "Alex").Refine(Document(turns)).AutoSpeakerNames);
    }

    [Fact]
    public void ProvisionalAndRepeatedSameWindowTurnsCannotSupplyIndependentEvidence()
    {
        var matcher = Observations(0, 10, "Alex");
        Assert.Empty(matcher.Refine(Document(Turn("Speaker 0", 1, 3), Turn("Speaker 0", 1, 3))).AutoSpeakerNames);
        Assert.Empty(matcher.Refine(Document(Turn("Speaker 0", 1, 3), Turn("Speaker 0", 5, 7) with { IsProvisional = true })).AutoSpeakerNames);
    }

    [Fact]
    public void StaleAndMissingBoundaryEvidenceAbstain()
    {
        var stale = new MeetingSpeakerMatcher();
        stale.Observe(Observation(0, "Alex"));
        stale.Observe(Observation(10, "Alex"));
        var document = Document(Turn("Speaker 0", 1, 3), Turn("Speaker 0", 5, 7));
        Assert.Empty(stale.Refine(document).AutoSpeakerNames);
        Assert.Empty(Observations(1.5, 8, "Alex").Refine(document).AutoSpeakerNames);
        Assert.Empty(Observations(0, 6.5, "Alex").Refine(document).AutoSpeakerNames);
    }

    [Theory]
    [InlineData(MeetingSpeakerReadStatus.Unavailable)]
    [InlineData(MeetingSpeakerReadStatus.Ambiguous)]
    public void UnreadableOrPausedSnapshotBreaksCoverage(MeetingSpeakerReadStatus status)
    {
        var matcher = Observations(0, 10, "Alex");
        matcher.Observe(Observation(2, "Alex") with { Status = status });
        Assert.Empty(matcher.Refine(Document(Turn("Speaker 0", 1, 3), Turn("Speaker 0", 5, 7))).AutoSpeakerNames);
    }

    [Fact]
    public void OverlappingVoicesOrMultipleSpeakingNamesAbstain()
    {
        var matcher = Observations(0, 10, "Alex");
        matcher.Observe(Observation(2, "Alex") with { SpeakingNames = new[] { "Alex", "Blair" } });
        Assert.Empty(matcher.Refine(Document(Turn("Speaker 0", 1, 3), Turn("Speaker 0", 5, 7))).AutoSpeakerNames);
        var overlap = Document(Turn("Speaker 0", 1, 3), Turn("Speaker 1", 2, 4), Turn("Speaker 0", 5, 7));
        Assert.Empty(Observations(0, 10, "Alex").Refine(overlap).AutoSpeakerNames);
    }

    [Fact]
    public void ConflictingLaterSpeakerEvidenceRevokesPreviousAutomaticAssignment()
    {
        var matcher = Observations(0, 7, "Alex");
        var document = Document(Turn("Speaker 0", 1, 3), Turn("Speaker 0", 5, 7));
        var named = matcher.Refine(document);
        Assert.Equal("Alex", named.GetSpeakerName("Speaker 0"));
        ObserveRange(matcher, 8, 12, "Blair");
        var corrected = matcher.Refine(named with { Turns = [.. named.Turns, Turn("Speaker 0", 9, 11)] });
        Assert.Empty(corrected.AutoSpeakerNames);
        Assert.Equal("Speaker 0", corrected.GetSpeakerName("Speaker 0"));
    }

    [Fact]
    public void NameChangeInsideChunkAndLateBoundaryChangeAreNotAssigned()
    {
        var matcher = Observations(0, 10, "Alex");
        matcher.Observe(Observation(2.5, "Blair"));
        Assert.Empty(matcher.Refine(Document(Turn("Speaker 0", 1, 3), Turn("Speaker 0", 5, 7))).AutoSpeakerNames);
        var late = new MeetingSpeakerMatcher();
        ObserveRange(late, 0, 2.5, "Alex");
        late.Observe(Observation(3.5, "Blair"));
        ObserveRange(late, 4, 10, "Alex");
        Assert.Empty(late.Refine(Document(Turn("Speaker 0", 1, 3), Turn("Speaker 0", 5, 7))).AutoSpeakerNames);
    }

    [Fact]
    public void SameVisibleNameAcrossDifferentSpeakerIdsIsAmbiguous()
    {
        var document = Document(Turn("Speaker 0", 1, 2), Turn("Speaker 0", 3, 4),
            Turn("Speaker 1", 5, 6), Turn("Speaker 1", 7, 8));
        Assert.Empty(Observations(0, 10, "Alex").Refine(document).AutoSpeakerNames);
    }

    [Fact]
    public void IsolatedMicrophoneCanUseSelfNameButMixedAudioCannot()
    {
        var matcher = new MeetingSpeakerMatcher();
        for (var sample = 0; sample <= 20; sample++)
            matcher.Observe(new((long)(sample * 0.5 * 16000), [], "You", MeetingSpeakerReadStatus.Readable));
        var document = Document(Turn("Speaker 0", 1, 3) with { Source = MeetingAudioSource.Microphone },
            Turn("Speaker 0", 5, 7) with { Source = MeetingAudioSource.Microphone });
        Assert.Equal("You", matcher.Refine(document).GetSpeakerName("Speaker 0"));
        Assert.Empty(matcher.Refine(document with { Turns = document.Turns.Select(turn => turn with { Source = MeetingAudioSource.Mixed }).ToArray() }).AutoSpeakerNames);
    }

    [Fact]
    public void ObservationsAreCopiedAndCapacityOverflowAbstains()
    {
        var matcher = new MeetingSpeakerMatcher();
        var names = new List<string> { "Alex" };
        matcher.Observe(new(0, names, null, MeetingSpeakerReadStatus.Readable));
        names[0] = "Blair";
        ObserveRange(matcher, 0.5, 8, "Alex");
        var document = Document(Turn("Speaker 0", 1, 3), Turn("Speaker 0", 5, 7));
        Assert.Equal("Alex", matcher.Refine(document).GetSpeakerName("Speaker 0"));
        for (var sample = 17; sample <= 15000; sample++) matcher.Observe(Observation(sample * 0.5, "Alex"));
        Assert.Empty(matcher.Refine(document).AutoSpeakerNames);
    }

    private static MeetingDocument Document(params MeetingTurn[] turns) => new() { Turns = turns, Status = MeetingStatus.Completed };
    private static MeetingTurn Turn(string speaker, double start, double end) => new(speaker, start, end, "Exact native words")
        { Source = MeetingAudioSource.Playback, TimingQuality = MeetingTimingQuality.Aligned };
    private static MeetingSpeakerObservation Observation(double seconds, string name) => new((long)(seconds * 16000), [name], null, MeetingSpeakerReadStatus.Readable);
    private static MeetingSpeakerMatcher Observations(double start, double end, string name)
    {
        var matcher = new MeetingSpeakerMatcher();
        ObserveRange(matcher, start, end, name);
        return matcher;
    }
    private static void ObserveRange(MeetingSpeakerMatcher matcher, double start, double end, string name)
    {
        for (var time = start; time <= end; time += 0.5) matcher.Observe(Observation(time, name));
    }
}
