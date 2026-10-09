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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new TaskCompletionSource<MeetingSpeakerObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        await observer.StartAsync(teams.Id, () => 16000, observation => received.TrySetResult(observation), timeout.Token);
        var result = await received.Task.WaitAsync(timeout.Token);
        await observer.StopAsync();
        Assert.Equal(16000, result.SamplePosition);
        Assert.True(Enum.IsDefined(result.Status));
        output.WriteLine($"Teams observation status: {result.Status}; explicit speaking names: {result.SpeakingNames.Count}; self name available: {result.SelfName is not null}. Names redacted. A real meeting remains unqualified.");
        output.WriteLine($"Visible windows: {observer.LastRead.WindowCount}; nodes inspected: {observer.LastRead.NodeCount}; budget exceeded: {observer.LastRead.BudgetExceeded}.");
    }
}
