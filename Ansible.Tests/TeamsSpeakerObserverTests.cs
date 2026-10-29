using System.Collections.Concurrent;
using System.Diagnostics;
using Ansible.Core;
using Ansible.Core.Windows;
using Xunit.Abstractions;

namespace Ansible.Tests;

public sealed class TeamsSpeakerObserverTests(ITestOutputHelper output)
{
    [Fact]
    public void ExplicitSpeakingTileSuppliesNameAndSelfBadge()
    {
        var observation = TeamsSpeakerClassifier.Classify([
            new("Jordan (You) (Guest), Video on, Active speaker"),
            new("Morgan, Video off, Not speaking")], 16000);
        Assert.Equal(MeetingSpeakerReadStatus.Readable, observation.Status);
        Assert.Equal(new[] { "Jordan" }, observation.SpeakingNames);
        Assert.Equal("Jordan", observation.SelfName);
        Assert.Equal(16000, observation.SamplePosition);
    }
    [Theory]
    [InlineData("Morgan, video is on, Has control of production tools, Context menu is available")]
    [InlineData("Morgan, video is off, Context menu is available")]
    [InlineData("Morgan, Has control of production tools, muted, Context menu is available")]
    [InlineData("Morgan, muted, Context menu is available")]
    public void CurrentTeamsTilesKeepNamesAndRequireSpeakingEvidence(string label)
    {
        var silent = TeamsSpeakerClassifier.Classify([new(label)], 0);
        Assert.Equal(MeetingSpeakerReadStatus.Unavailable, silent.Status);
        Assert.Empty(silent.SpeakingNames);
        var active = TeamsSpeakerClassifier.Classify([new(label), new("Speaking", Parent: 0)], 16000);
        Assert.Equal(MeetingSpeakerReadStatus.Readable, active.Status);
        Assert.Equal(new[] { "Morgan" }, active.SpeakingNames);
    }
    [Fact]
    public void CurrentTeamsSelfTileRemovesRolePrefixAndPreservesCommaInName()
    {
        var observation = TeamsSpeakerClassifier.Classify([
            new("Myself video, Taylor, Jordan, Has control of production tools, Unmuted, Has context menu"),
            new("You are speaking", Parent: 0)], 0);
        Assert.Equal(MeetingSpeakerReadStatus.Readable, observation.Status);
        Assert.Equal("Taylor, Jordan", observation.SelfName);
        Assert.Equal(new[] { "Taylor, Jordan" }, observation.SpeakingNames);
    }
    [Fact]
    public void UnmutedSelfTileIdentifiesSelfButDoesNotProveSpeaking()
    {
        var observation = TeamsSpeakerClassifier.Classify([
            new("Myself video, Jordan, Unmuted, Has context menu")], 0);
        Assert.Equal("Jordan", observation.SelfName);
        Assert.Equal(MeetingSpeakerReadStatus.Unavailable, observation.Status);
        Assert.Empty(observation.SpeakingNames);
    }
    [Fact]
    public void CurrentTeamsDescriptionsInChatTextAreNotParticipantTiles()
    {
        var observation = TeamsSpeakerClassifier.Classify([
            new("Morgan, video is on, Speaking", ControlType: 50026)], 0);
        Assert.Equal(MeetingSpeakerReadStatus.Unavailable, observation.Status);
        Assert.Empty(observation.SpeakingNames);
    }
    [Fact]
    public void PartialStateWordsInNamesDoNotCreateTiles()
    {
        var observation = TeamsSpeakerClassifier.Classify([
            new("Morgan, Video online, Speaking"), new("Jordan, Unmuted guest, Speaking")], 0);
        Assert.Equal(MeetingSpeakerReadStatus.Unavailable, observation.Status);
        Assert.Empty(observation.SpeakingNames);
    }
    [Fact]
    public void CurrentTeamsNamesAndSilenceMarkersRemainConsistentAcrossDuplicateTiles()
    {
        var observation = TeamsSpeakerClassifier.Classify([
            new("Myself video, Jordan, Unmuted, Has context menu", ItemStatus: "Not speaking"),
            new("Morgan, video is on, Context menu is available", ItemStatus: "Speaking"),
            new("Morgan, video is on, Has control of production tools", HelpText: "Speaking")], 0);
        Assert.Equal(MeetingSpeakerReadStatus.Readable, observation.Status);
        Assert.Equal("Jordan", observation.SelfName);
        Assert.Equal(new[] { "Morgan" }, observation.SpeakingNames);
    }
    [Fact]
    public void RosterAndVideoStateDoNotProveSpeaking()
    {
        var observation = TeamsSpeakerClassifier.Classify([
            new("Jordan", "roster-participant"), new("Morgan, Video on")], 0);
        Assert.Equal(MeetingSpeakerReadStatus.Unavailable, observation.Status);
        Assert.Empty(observation.SpeakingNames);
    }
    [Fact]
    public void SpeakingWordsInANameAreNotEvidence()
    {
        var observation = TeamsSpeakerClassifier.Classify([new("Speaking, Video on"), new("Jordan Speaking, Video off")], 0);
        Assert.Equal(MeetingSpeakerReadStatus.Unavailable, observation.Status);
    }
    [Fact]
    public void CommasInDisplayNamesArePreserved()
    {
        var observation = TeamsSpeakerClassifier.Classify([new("Taylor, Jordan, Video on, Speaking")], 0);
        Assert.Equal(new[] { "Taylor, Jordan" }, observation.SpeakingNames);
        Assert.Empty(TeamsSpeakerClassifier.Classify([new("Taylor, Speaking, Video on")], 0).SpeakingNames);
    }
    [Fact]
    public void ExplicitMarkerChildUsesParticipantTileAncestor()
    {
        var observation = TeamsSpeakerClassifier.Classify([
            new("Morgan, Video on"), new("Active speaker", Parent: 0)], 50);
        Assert.Equal(MeetingSpeakerReadStatus.Readable, observation.Status);
        Assert.Equal(new[] { "Morgan" }, observation.SpeakingNames);
    }
    [Theory]
    [InlineData("Active speaker", "")]
    [InlineData("", "Speaking")]
    public void TileHelpOrStatusCanProvideExplicitEvidence(string help, string status)
    {
        Assert.Equal(new[] { "Morgan" }, TeamsSpeakerClassifier.Classify([
            new("Morgan", "participant-tile", HelpText: help, ItemStatus: status)], 0).SpeakingNames);
    }
    [Fact]
    public void TwoSimultaneousSpeakersAreAmbiguous()
    {
        var observation = TeamsSpeakerClassifier.Classify([
            new("Jordan, Video on, Speaking"), new("Morgan, Video off, Active speaker")], 0);
        Assert.Equal(MeetingSpeakerReadStatus.Ambiguous, observation.Status);
        Assert.Equal(2, observation.SpeakingNames.Count);
    }
    [Fact]
    public void ContradictoryMainAndCompactEvidenceIsAmbiguous()
    {
        Assert.Equal(MeetingSpeakerReadStatus.Ambiguous, TeamsSpeakerClassifier.Classify([
            new("Jordan, Video on, Speaking"), new("Jordan, Video off, Not speaking")], 0).Status);
    }
    [Fact]
    public void MainAndCompactDuplicateNamesMerge()
    {
        var observation = TeamsSpeakerClassifier.Classify([
            new("Jordan, Video on, Speaking"), new("Jordan, Video off, Speaking")], 0);
        Assert.Equal(MeetingSpeakerReadStatus.Readable, observation.Status);
        Assert.Single(observation.SpeakingNames);
    }
    [Fact]
    public void AnExplicitSilentTileIsReadableWithoutAnActiveSpeaker()
    {
        var observation = TeamsSpeakerClassifier.Classify([new("Jordan, Video on, Not speaking")], 0);
        Assert.Equal(MeetingSpeakerReadStatus.Readable, observation.Status);
        Assert.Empty(observation.SpeakingNames);
    }
    [Fact]
    public void GenericActiveSpeakerWithoutANamedTileIsUnavailable()
    {
        Assert.Equal(MeetingSpeakerReadStatus.Unavailable,
            TeamsSpeakerClassifier.Classify([new("Active speaker")], 0).Status);
    }
    [Fact]
    public void NonTeamsProcessNamesAreRejected()
    {
        Assert.False(TeamsSpeakerObserver.IsTeamsProcess("notepad"));
        Assert.False(TeamsSpeakerObserver.IsTeamsProcess("ms-teams-fake"));
        Assert.True(TeamsSpeakerObserver.IsTeamsProcess("ms-teams"));
    }
    [Fact, Trait("Category", "NativeIntegration")]
    public async Task ExistingTeamsWindowCanBeReadWithoutInteraction()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var teams = Process.GetProcessesByName("ms-teams").FirstOrDefault(process => process.MainWindowHandle != 0);
        if (teams is null) { output.WriteLine("Teams is not open; no live validation."); return; }
        await using var observer = new TeamsSpeakerObserver();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var readings = new ConcurrentQueue<(MeetingSpeakerObservation Observation, TeamsSpeakerReadDiagnostics Diagnostics)>();
        await observer.StartAsync(teams.Id, () => 16000,
            observation => readings.Enqueue((observation, observer.LastRead)), timeout.Token);
        await Task.Delay(TimeSpan.FromSeconds(5), timeout.Token);
        await observer.StopAsync();
        Assert.NotEmpty(readings);
        Assert.All(readings, reading =>
        {
            Assert.Equal(16000, reading.Observation.SamplePosition);
            Assert.True(Enum.IsDefined(reading.Observation.Status));
        });
        var best = readings.OrderByDescending(reading => reading.Diagnostics.NodeCount).First();
        output.WriteLine($"Readings: {readings.Count}; readable: {readings.Count(reading => reading.Observation.Status == MeetingSpeakerReadStatus.Readable)}; self name available: {readings.Any(reading => reading.Observation.SelfName is not null)}; explicit speaking names: {readings.Max(reading => reading.Observation.SpeakingNames.Count)}. Names redacted. Mapping a real meeting still requires separate qualification.");
        output.WriteLine($"Most populated read: visible windows {best.Diagnostics.WindowCount}; nodes inspected {best.Diagnostics.NodeCount}; budget exceeded {best.Diagnostics.BudgetExceeded}; error HRESULT {best.Diagnostics.ErrorHResult:X8}.");
        output.WriteLine($"Budget failures: {readings.Count(reading => reading.Diagnostics.BudgetExceeded)}; errors: {string.Join(", ", readings.Where(reading => reading.Diagnostics.ErrorHResult is not null).Select(reading => $"{reading.Diagnostics.ErrorHResult:X8}").Distinct())}.");
    }
}
