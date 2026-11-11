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
        Assert.Equal("Today", Assert.IsType<MeetingDayHeader>(items[0]).Label);
        Assert.Equal("late", Assert.IsType<MeetingHistoryRow>(items[1]).Title);
        Assert.Equal("early", Assert.IsType<MeetingHistoryRow>(items[2]).Title);
        Assert.Equal("Yesterday", Assert.IsType<MeetingDayHeader>(items[3]).Label);
        Assert.Equal("yesterday", Assert.IsType<MeetingHistoryRow>(items[4]).Title);
        Assert.Equal(new DateOnly(2026, 10, 12).ToString("ddd d MMM", CultureInfo.CurrentCulture),
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

    [Theory]
    [InlineData(MeetingStatus.Failed, true, true, false, "Failed")]
    [InlineData(MeetingStatus.Failed, false, false, true, "")]
    [InlineData(MeetingStatus.Interrupted, true, false, false, "Interrupted")]
    [InlineData(MeetingStatus.Interrupted, false, false, true, "")]
    [InlineData(MeetingStatus.Completed, true, false, false, "")]
    [InlineData(MeetingStatus.Completed, false, false, true, "")]
    [InlineData(MeetingStatus.Recording, false, false, false, "Recording")]
    public void OnlyAFailureWithSavedTextIsCriticalAndOnlyFinishedMeetingsWithoutSpeechRecede(
        MeetingStatus status, bool hasSpeech, bool critical, bool empty, string badge)
    {
        var row = new MeetingHistoryRow(Meeting(new DateTime(2026, 10, 20, 8, 0, 0), status: status,
            duration: hasSpeech ? TimeSpan.FromMinutes(3) : null, speakers: hasSpeech ? 1 : 0));

        Assert.Equal(critical, row.IsCriticalFailure);
        Assert.Equal(empty, row.IsEmpty);
        Assert.Equal(!empty, row.HasReadableTitle);
        Assert.Equal(empty, MeetingList.Empty([row.Summary]).Count == 1);
        Assert.Equal(badge, row.Status);
    }

    [Fact]
    public void EachRowShowsAtMostOneStatusBadge()
    {
        foreach (var status in Enum.GetValues<MeetingStatus>())
        foreach (var duration in new TimeSpan?[] { null, TimeSpan.FromMinutes(3) })
        {
            var row = new MeetingHistoryRow(Meeting(new DateTime(2026, 10, 20, 8, 0, 0), status: status, duration: duration));
            Assert.True(new[] { row.IsCriticalFailure, row.HasNeutralStatus }.Count(shown => shown) <= 1);
        }
    }

    [Fact]
    public void AFailedMeetingWithNoSpeechStatesTheFailureOnceInItsAccessibleName()
    {
        var row = new MeetingHistoryRow(Meeting(new DateTime(2026, 10, 20, 8, 0, 0), "Planning", MeetingStatus.Failed));

        Assert.Equal(1, row.AccessibleName.Split("Failed").Length - 1);
        Assert.Contains("Failed before any speech was transcribed", row.AccessibleName);
    }

    [Fact]
    public void DatesOutsideTheCurrentYearNameTheYear()
    {
        var items = MeetingList.Create([Meeting(new DateTime(2025, 12, 31, 12, 0, 0))], Today);

        Assert.Equal(new DateOnly(2025, 12, 31).ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture),
            Assert.IsType<MeetingDayHeader>(items[0]).Label);
    }

    [Fact]
    public void DayLabelsAreSharedWithHomeHistory()
    {
        Assert.Equal("Today", DayLabel.For(Today, Today));
        Assert.Equal("Yesterday", DayLabel.For(Today.AddDays(-1), Today));
        Assert.Equal("Yesterday", DayLabel.For(new DateOnly(2025, 12, 31), new DateOnly(2026, 1, 1)));
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
        Assert.Equal($"8 min · 2 speakers · {Time(start)}", titled.Details);
    }

    [Fact]
    public void RowsWithoutSpeechSayWhatIsKnownAboutHowTheMeetingEnded()
    {
        var start = new DateTime(2026, 10, 20, 8, 0, 0);
        var failed = new MeetingHistoryRow(Meeting(start, "New meeting", MeetingStatus.Failed));

        Assert.Equal("Failed before any speech was transcribed", failed.Details);
        Assert.Equal("", failed.Status);
        Assert.True(failed.IsFailed);
        Assert.False(failed.HasNeutralStatus);
        Assert.Equal("Interrupted before any speech was transcribed",
            new MeetingHistoryRow(Meeting(start, "New meeting", MeetingStatus.Interrupted)).Details);
        Assert.Equal("No speech transcribed", new MeetingHistoryRow(Meeting(start, "New meeting")).Details);
        Assert.Equal("No speech transcribed yet", new MeetingHistoryRow(Meeting(start, "New meeting", MeetingStatus.Recording)).Details);
        Assert.Equal($"Failed before any speech was transcribed · {Time(start)}",
            new MeetingHistoryRow(Meeting(start, "Planning", MeetingStatus.Failed)).Details);
    }

    [Fact]
    public void MeetingsThatStoppedEarlySayTheirEarlierTextIsKept()
    {
        var start = new DateTime(2026, 10, 20, 8, 0, 0);
        var failed = new MeetingHistoryRow(Meeting(start, "New meeting", MeetingStatus.Failed, TimeSpan.FromMinutes(3), 1));
        var interrupted = new MeetingHistoryRow(Meeting(start, "New meeting", MeetingStatus.Interrupted, TimeSpan.FromMinutes(3), 1));
        var recording = new MeetingHistoryRow(Meeting(start, "New meeting", MeetingStatus.Recording, TimeSpan.FromMinutes(3), 1));

        Assert.Equal("3 min · 1 speaker · Text before the failure is saved", failed.Details);
        Assert.Equal("3 min · 1 speaker · Text before the interruption is saved", interrupted.Details);
        Assert.Equal("3 min · 1 speaker", recording.Details);
    }

    [Fact]
    public void OnlyFinishedMeetingsWithoutSpeechAreDeletedTogether()
    {
        var start = new DateTime(2026, 10, 20, 8, 0, 0);
        MeetingSummary[] summaries =
        [
            Meeting(start, status: MeetingStatus.Failed),
            Meeting(start, status: MeetingStatus.Interrupted),
            Meeting(start),
            Meeting(start, status: MeetingStatus.Failed, duration: TimeSpan.FromMinutes(2), speakers: 1),
            Meeting(start, duration: TimeSpan.FromMinutes(2)),
            Meeting(start, status: MeetingStatus.Recording),
            Meeting(start, status: MeetingStatus.Starting)
        ];

        Assert.Equal(summaries.Take(3).Select(summary => summary.Id), MeetingList.Empty(summaries));
        Assert.All(summaries.Take(5), summary => Assert.True(new MeetingHistoryRow(summary).CanDelete));
        Assert.All(summaries.Skip(5), summary => Assert.False(new MeetingHistoryRow(summary).CanDelete));
        Assert.Equal("Delete this meeting?", MeetingText.DeleteConfirmation(1).Title);
        Assert.Equal("Delete 3 empty meetings?", MeetingText.DeleteConfirmation(3).Title);
    }

    [Fact]
    public void OnlyMeetingsThatDidNotCompleteNormallyHaveAStatus()
    {
        var completed = new MeetingHistoryRow(Meeting(new DateTime(2026, 10, 20, 8, 0, 0)));
        var interrupted = new MeetingHistoryRow(Meeting(new DateTime(2026, 10, 20, 8, 0, 0), status: MeetingStatus.Interrupted,
            duration: TimeSpan.FromMinutes(3), speakers: 2));

        Assert.Equal("", completed.Status);
        Assert.False(completed.HasNeutralStatus);
        Assert.Equal("Interrupted", interrupted.Status);
        Assert.True(interrupted.HasNeutralStatus);
        foreach (var status in Enum.GetValues<MeetingStatus>()) MeetingText.Status(status);
    }

    [Fact]
    public void TheWindowUsesTheListTitleAndPutsTheDateOnItsDetailLine()
    {
        var local = new DateTime(2026, 10, 19, 22, 9, 0);
        var created = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        var untitled = new MeetingDocument { CreatedAt = created, UpdatedAt = created, Status = MeetingStatus.Completed };

        Assert.Equal(new MeetingHistoryRow(MeetingSummary.From(untitled)).Title, MeetingText.Title(untitled.Title, created));
        Assert.Equal($"Meeting at {Time(local)}", MeetingText.Title("New meeting", created));
        Assert.Equal("Planning", MeetingText.Title("Planning", created));
        Assert.Equal(MeetingText.Date(local, Today), MeetingText.SavedDetails(untitled, Today));
        Assert.Equal($"{MeetingText.Date(local, Today)}, {Time(local)} · Failed",
            MeetingText.SavedDetails(untitled with { Title = "Planning", Status = MeetingStatus.Failed }, Today));
        Assert.Contains(local.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture), MeetingText.SavedDetails(untitled, new DateOnly(2027, 1, 2)));
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
        var local = new DateTime(2026, 10, 19, 22, 9, 0);
        var created = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        var document = new MeetingDocument
        {
            CreatedAt = created,
            UpdatedAt = created,
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
        Assert.Equal($"{MeetingText.Date(local, Today)} · 8 min · 2 speakers",
            MeetingText.SavedDetails(document with { Status = MeetingStatus.Completed }, Today));
    }
}
