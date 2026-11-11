using Ansible.Core.Windows;

namespace Ansible.Tests;

public sealed class TeamsMeetingContextTests
{
    [Theory]
    [InlineData("Design review | Microsoft Teams", "Design review")]
    [InlineData("Design review - Teams", "Design review")]
    [InlineData("Chat | Microsoft Teams", null)]
    [InlineData("New meeting | Microsoft Teams", null)]
    [InlineData("Meeting - Microsoft Teams", null)]
    [InlineData("", null)]
    public void CleansOnlyTeamsWindowSuffix(string input, string? expected)
        => Assert.Equal(expected, TeamsMeetingContextReader.CleanTitle(input));

    [Theory]
    [InlineData("Hang up", "", false)]
    [InlineData("", "hangup-button", true)]
    [InlineData("Leave meeting", "", false)]
    [InlineData("Leave", "", false)]
    [InlineData("End", "", false)]
    [InlineData("Mute", "hangup-ish", false)]
    public void RequiresAnExplicitCallControl(string name, string automationId, bool expected)
        => Assert.Equal(expected, TeamsMeetingContextReader.IsCallWindow([(name, automationId)]));

    [Fact]
    public void CallToolbarDistinguishesMeetingFromGroupLeaveDialog()
    {
        Assert.False(TeamsMeetingContextReader.IsCallWindow([("Leave", ""), ("Cancel", "")]));
        Assert.True(TeamsMeetingContextReader.IsCallWindow([("Leave", ""), ("Unmute mic", "")]));
        Assert.False(TeamsMeetingContextReader.IsCallWindow([("Call", ""), ("Turn camera on", "")]));
    }

    [Fact]
    public void ImportedTitleFitsTheTranscriptStorageLimit()
        => Assert.Equal(500, TeamsMeetingContextReader.CleanTitle(new string('A', 501))!.Length);

    [Theory]
    [InlineData("Mute mic", "", true)]
    [InlineData("Unmute microphone", "", true)]
    [InlineData("", "microphone-button", true)]
    [InlineData("Leave", "", false)]
    [InlineData("Mute", "", false)]
    public void InCallToolbarEvidenceIsSeparateFromGroupLeave(string name, string automationId, bool expected)
        => Assert.Equal(expected, TeamsMeetingContextReader.IsInCallToolbarButton(name, automationId));
}
