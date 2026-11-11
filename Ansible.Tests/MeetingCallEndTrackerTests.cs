using Ansible.Core;

namespace Ansible.Tests;

public sealed class MeetingCallEndTrackerTests
{
    [Fact]
    public void AChatWindowAloneNeverArmsAutomaticStop()
    {
        var tracker = new MeetingCallEndTracker();
        for (var second = 0; second <= 20; second++)
            Assert.False(tracker.Observe(MeetingCallPresence.Absent, TimeSpan.FromSeconds(second)));
    }

    [Fact]
    public void AConfirmedCallMustBeAbsentForFiveSecondsAndThreeReads()
    {
        var tracker = new MeetingCallEndTracker();
        Assert.False(tracker.Observe(MeetingCallPresence.Active, TimeSpan.Zero));
        Assert.False(tracker.Observe(MeetingCallPresence.Absent, TimeSpan.FromSeconds(1)));
        Assert.False(tracker.Observe(MeetingCallPresence.Absent, TimeSpan.FromSeconds(5)));
        Assert.True(tracker.Observe(MeetingCallPresence.Absent, TimeSpan.FromSeconds(6)));
    }

    [Theory]
    [InlineData(MeetingCallPresence.Active)]
    [InlineData(MeetingCallPresence.Unknown)]
    public void ResumedOrUnreadableCallBreaksTheCallEndSequence(MeetingCallPresence interruption)
    {
        var tracker = new MeetingCallEndTracker();
        tracker.Observe(MeetingCallPresence.Active, TimeSpan.Zero);
        tracker.Observe(MeetingCallPresence.Absent, TimeSpan.FromSeconds(1));
        tracker.Observe(MeetingCallPresence.Absent, TimeSpan.FromSeconds(2));
        Assert.False(tracker.Observe(interruption, TimeSpan.FromSeconds(8)));
        Assert.False(tracker.Observe(MeetingCallPresence.Absent, TimeSpan.FromSeconds(9)));
        Assert.False(tracker.Observe(MeetingCallPresence.Absent, TimeSpan.FromSeconds(14)));
        Assert.True(tracker.Observe(MeetingCallPresence.Absent, TimeSpan.FromSeconds(15)));
    }
}
