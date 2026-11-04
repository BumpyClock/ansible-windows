using Ansible.Core;
using System.Globalization;

namespace Ansible;

public sealed class MeetingListProjectionTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);

    private static MeetingSummary Meeting(DateTime local, string title = "Meeting", MeetingStatus status = MeetingStatus.Completed)
    {
        var created = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        return new MeetingSummary(Guid.NewGuid(), title, created, created, status);
    }

    [Fact]
    public void EmptyListHasNoItems() => Assert.Empty(MeetingList.Create([], Today));

    [Fact]
    public void GroupsByLocalDayNewestFirstWithAHeaderBeforeEachDay()
    {
        MeetingSummary[] summaries =
        [
            Meeting(new DateTime(2026, 10, 19, 9, 0, 0), "yesterday"),
            Meeting(new DateTime(2026, 10, 20, 8, 0, 0), "early"),
            Meeting(new DateTime(2026, 10, 20, 15, 0, 0), "late"),
            Meeting(new DateTime(2026, 10, 12, 10, 0, 0), "last week")
        ];

        var items = MeetingList.Create(summaries, Today);

        Assert.Equal(7, items.Count);
        Assert.Equal("TODAY", Assert.IsType<MeetingDayHeader>(items[0]).Label);
        Assert.Equal("late", Assert.IsType<MeetingHistoryRow>(items[1]).Title);
        Assert.Equal("early", Assert.IsType<MeetingHistoryRow>(items[2]).Title);
        Assert.Equal("YESTERDAY", Assert.IsType<MeetingDayHeader>(items[3]).Label);
        Assert.Equal("yesterday", Assert.IsType<MeetingHistoryRow>(items[4]).Title);
        Assert.Equal(new DateOnly(2026, 10, 12).ToString("ddd, MMM d", CultureInfo.CurrentCulture).ToUpperInvariant(),
            Assert.IsType<MeetingDayHeader>(items[5]).Label);
        Assert.Equal("last week", Assert.IsType<MeetingHistoryRow>(items[6]).Title);
    }

    [Fact]
    public void FailedMeetingsAreFlagged()
    {
        var items = MeetingList.Create([Meeting(new DateTime(2026, 10, 20, 8, 0, 0), status: MeetingStatus.Failed),
            Meeting(new DateTime(2026, 10, 20, 7, 0, 0))], Today);

        Assert.True(Assert.IsType<MeetingHistoryRow>(items[1]).IsFailed);
        Assert.False(Assert.IsType<MeetingHistoryRow>(items[2]).IsFailed);
    }

    [Fact]
    public void DatesOutsideTheCurrentYearNameTheYear()
    {
        var items = MeetingList.Create([Meeting(new DateTime(2025, 12, 31, 12, 0, 0))], Today);

        Assert.Equal(new DateOnly(2025, 12, 31).ToString("ddd, MMM d, yyyy", CultureInfo.CurrentCulture).ToUpperInvariant(),
            Assert.IsType<MeetingDayHeader>(items[0]).Label);
    }

    [Fact]
    public void DayLabelsAreSharedWithHomeHistory()
    {
        Assert.Equal("TODAY", DayLabel.For(Today, Today));
        Assert.Equal("YESTERDAY", DayLabel.For(Today.AddDays(-1), Today));
        Assert.Equal("YESTERDAY", DayLabel.For(new DateOnly(2025, 12, 31), new DateOnly(2026, 1, 1)));
    }
}
