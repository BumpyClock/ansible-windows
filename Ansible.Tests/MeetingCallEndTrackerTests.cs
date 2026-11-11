using Ansible.Core;

namespace Ansible.Tests;

public sealed class MeetingCallEndTrackerTests
{
    [Fact]
    public async Task UnreadableMonitoringReportsWarningAndRecoveryWithoutEndingMeeting()
    {
        using var stop = new CancellationTokenSource();
        var availability = new List<bool>();
        var ends = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MeetingContextMonitor.FollowAsync(
            new RecoveringCall(), new(42, DateTime.UtcNow), new(MeetingCallPresence.Unknown),
            _ => Task.CompletedTask, () => { ends++; return Task.CompletedTask; },
            available =>
            {
                availability.Add(available);
                if (availability.Count == 3) stop.Cancel();
            }, stop.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(new[] { false, true, false }, availability);
        Assert.Equal(0, ends);
    }

    private sealed class RecoveringCall : IMeetingContextReader
    {
        private int _reads;
        public Task<MeetingContextObservation> ReadAsync(MeetingDetectionCandidate identity, CancellationToken cancellationToken)
            => Task.FromResult(new MeetingContextObservation(++_reads == 1
                ? MeetingCallPresence.Active : MeetingCallPresence.Unknown));
    }

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
