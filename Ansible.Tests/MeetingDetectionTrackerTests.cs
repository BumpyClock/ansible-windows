using Ansible.Core;

namespace Ansible.Tests;

public sealed class MeetingDetectionTrackerTests
{
    private static readonly MeetingDetectionCandidate Teams = new(42, new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void OfferRequiresThreeSecondsOfSameIdentityAndFlickerResetsStability()
    {
        var tracker = new MeetingDetectionTracker();
        Assert.Null(Update(tracker, Teams, 0));
        Assert.Null(Update(tracker, Teams, 2.9));
        Assert.Null(Update(tracker, null, 3));
        Assert.Null(Update(tracker, Teams, 4));
        Assert.Null(Update(tracker, Teams, 6.9));
        Assert.Equal(Teams, Update(tracker, Teams, 7));
        Assert.Equal(Teams, tracker.CurrentOffer);
        Assert.Null(Update(tracker, null, 8));
        Assert.Null(tracker.CurrentOffer);
    }

    [Fact]
    public void DismissSurvivesFlickerUntilTenSecondsOfConfirmedAbsence()
    {
        var tracker = Offered();
        tracker.Dismiss(Teams);
        Assert.Null(tracker.CurrentOffer);
        Assert.Null(Update(tracker, Teams, 20));
        Assert.Null(Update(tracker, null, 21));
        Assert.Null(Update(tracker, null, 30.9));
        Assert.Null(Update(tracker, Teams, 31));
        Assert.Null(Update(tracker, Teams, 40));
        Assert.Null(Update(tracker, null, 41));
        Assert.Null(Update(tracker, null, 51));
        Assert.Null(Update(tracker, Teams, 52));
        Assert.Equal(Teams, Update(tracker, Teams, 55));
    }

    [Fact]
    public void BusyHidesOffersAndRequiresFreshStableObservation()
    {
        var tracker = Offered();
        Assert.Null(Update(tracker, Teams, 4, busy: true));
        Assert.Null(tracker.CurrentOffer);
        Assert.Null(Update(tracker, Teams, 100, busy: true));
        Assert.Null(Update(tracker, Teams, 101));
        Assert.Equal(Teams, Update(tracker, Teams, 104));
    }

    [Fact]
    public void DisableResetsStabilityAndDoesNotRearmDismissedMeeting()
    {
        var tracker = Offered();
        tracker.Dismiss(Teams);
        Update(tracker, null, 4);
        Assert.Null(Update(tracker, null, 100, enabled: false));
        Assert.Null(Update(tracker, Teams, 101));
        Assert.Null(Update(tracker, Teams, 110));
        Update(tracker, null, 111);
        Update(tracker, null, 121);
        Update(tracker, Teams, 122);
        Assert.Equal(Teams, Update(tracker, Teams, 125));
        Assert.Null(Update(tracker, Teams, 126, enabled: false));
        Assert.Null(Update(tracker, Teams, 127));
        Assert.Equal(Teams, Update(tracker, Teams, 130));
    }

    [Fact]
    public void ReusedProcessIdWithNewStartTimeIsAnIndependentMeetingIdentity()
    {
        var tracker = Offered();
        tracker.Dismiss(Teams);
        var restarted = new MeetingDetectionCandidate(Teams.ProcessId, Teams.ProcessStartedUtc.AddSeconds(1));
        Assert.Null(Update(tracker, restarted, 4));
        Assert.Equal(restarted, Update(tracker, restarted, 7));
        tracker.Dismiss(restarted);
        Assert.Null(Update(tracker, Teams, 8));
        Assert.Null(Update(tracker, Teams, 11));
        Assert.Null(Update(tracker, restarted, 12));
        Assert.Null(Update(tracker, restarted, 15));
    }

    [Fact]
    public void FailedObservationHidesOfferAndBreaksAbsenceWithoutRearmingDismissal()
    {
        var tracker = Offered();
        tracker.InvalidateObservation();
        Assert.Null(tracker.CurrentOffer);
        Assert.Null(Update(tracker, Teams, 4));
        Assert.Equal(Teams, Update(tracker, Teams, 7));
        tracker.Dismiss(Teams);
        Update(tracker, null, 8);
        tracker.InvalidateObservation();
        Assert.Null(Update(tracker, null, 100));
        Assert.Null(Update(tracker, Teams, 109));
        Assert.Null(Update(tracker, Teams, 112));
        Update(tracker, null, 113);
        Update(tracker, null, 123);
        Update(tracker, Teams, 124);
        Assert.Equal(Teams, Update(tracker, Teams, 127));
    }

    [Fact]
    public void MonotonicClockRegressionIsRejectedWithoutChangingOffer()
    {
        var tracker = Offered();
        Assert.Throws<ArgumentOutOfRangeException>(() => Update(tracker, null, 2));
        Assert.Equal(Teams, tracker.CurrentOffer);
    }

    private static MeetingDetectionTracker Offered()
    {
        var tracker = new MeetingDetectionTracker();
        Update(tracker, Teams, 0);
        Assert.Equal(Teams, Update(tracker, Teams, 3));
        return tracker;
    }

    private static MeetingDetectionCandidate? Update(MeetingDetectionTracker tracker, MeetingDetectionCandidate? candidate,
        double seconds, bool enabled = true, bool busy = false) => tracker.Update(candidate, TimeSpan.FromSeconds(seconds), enabled, busy);
}
