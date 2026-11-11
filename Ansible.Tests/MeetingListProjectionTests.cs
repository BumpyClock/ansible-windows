using Ansible.Core;
using System.Globalization;

namespace Ansible;

public sealed class MeetingListProjectionTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);

    private static MeetingSummary Meeting(DateTime local, string title = "Meeting", MeetingStatus status = MeetingStatus.Completed,
        TimeSpan? duration = null, int speakers = 0)
    {
        var created = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        return new MeetingSummary(Guid.NewGuid(), title, created, created, status, duration, speakers);
    }

    private static string Time(DateTime local) => local.ToString("t", CultureInfo.CurrentCulture);

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

    [Fact]
    public void UntitledMeetingsAreNamedByTheirStartTimeAndUserTitlesAreKept()
    {
        var start = new DateTime(2026, 10, 20, 15, 5, 0);
        var untitled = new MeetingHistoryRow(Meeting(start, "New meeting", duration: TimeSpan.FromMinutes(8), speakers: 2));
        var titled = new MeetingHistoryRow(Meeting(start, "Planning", duration: TimeSpan.FromMinutes(8), speakers: 2));

        Assert.Equal($"Meeting at {Time(start)}", untitled.Title);
        Assert.Equal("8 min · 2 speakers", untitled.Details);
        Assert.Equal("Planning", titled.Title);
        Assert.Equal($"{Time(start)} · 8 min · 2 speakers", titled.Details);
    }

    [Fact]
    public void RowsWithoutSpeechStillHaveADetailLine()
    {
        var row = new MeetingHistoryRow(Meeting(new DateTime(2026, 10, 20, 8, 0, 0), "New meeting", MeetingStatus.Failed));

        Assert.Equal("No speech transcribed", row.Details);
        Assert.Equal("Failed", row.Status);
        Assert.True(row.IsFailed);
        Assert.False(row.HasNeutralStatus);
    }

    [Fact]
    public void OnlyMeetingsThatDidNotCompleteNormallyHaveAStatus()
    {
        var completed = new MeetingHistoryRow(Meeting(new DateTime(2026, 10, 20, 8, 0, 0)));
        var interrupted = new MeetingHistoryRow(Meeting(new DateTime(2026, 10, 20, 8, 0, 0), status: MeetingStatus.Interrupted));

        Assert.Equal("", completed.Status);
        Assert.False(completed.HasNeutralStatus);
        Assert.Equal("Interrupted", interrupted.Status);
        Assert.True(interrupted.HasNeutralStatus);
        // Every saved state has a label decision; an unmapped state would throw.
        foreach (var status in Enum.GetValues<MeetingStatus>()) MeetingText.Status(status);
    }

    [Fact]
    public void WindowTitleNamesTheDateAndTimeOnlyForUntitledMeetings()
    {
        var local = new DateTime(2026, 10, 19, 22, 9, 0);
        var created = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));

        var title = MeetingText.Title("New meeting", created, Today);

        Assert.StartsWith("Meeting on ", title);
        Assert.EndsWith($", {Time(local)}", title);
        Assert.Contains(local.ToString("ddd", CultureInfo.CurrentCulture), title);
        Assert.Equal("Planning", MeetingText.Title("Planning", created, Today));
        Assert.Contains(local.ToString("d", CultureInfo.CurrentCulture), MeetingText.Title("New meeting", created, new DateOnly(2027, 1, 2)));
    }

    [Theory]
    [InlineData(20, "Under 1 min")]
    [InlineData(60, "1 min")]
    [InlineData(474, "8 min")]
    [InlineData(3600, "1 hr")]
    [InlineData(3900, "1 hr 5 min")]
    public void DurationsReadAsMinutesAndHours(int seconds, string expected) =>
        Assert.Equal(expected, MeetingText.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void SummaryCountsAttributedSpeakersAndEndsAtTheLastSpeech()
    {
        var document = new MeetingDocument
        {
            Turns =
            [
                new MeetingTurn("Speaker 1", 0, 30, "a"),
                new MeetingTurn("Unknown speaker", 30, 40, "b"),
                new MeetingTurn("You", 40, 474, "c"),
                new MeetingTurn("Speaker 1", 100, 120, "d")
            ]
        };

        var summary = MeetingSummary.From(document);

        Assert.Equal(2, summary.SpeakerCount);
        Assert.Equal(TimeSpan.FromSeconds(474), summary.Duration);
        Assert.Null(MeetingSummary.From(new MeetingDocument()).Duration);
        Assert.Equal("8 min · 2 speakers", MeetingText.SavedDetails(document with { Status = MeetingStatus.Completed }, Today));
    }
}
