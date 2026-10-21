using Ansible.Core;
using System.Globalization;

namespace Ansible;

public sealed class HomeProjectionTests
{
    [Theory]
    [InlineData("en-GB")]
    [InlineData("de-DE")]
    public void HistoryUsesTheUsersTwentyFourHourClock(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var local = new DateTime(2026, 10, 20, 15, 5, 0);
            var item = History(new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)), "speech");

            var row = new HomeHistoryEntry(item, "Model");

            Assert.Equal("15:05", row.TimeText);
            Assert.Contains("Word count unavailable", row.MetadataText);
            Assert.Contains("00:05", row.MetadataText);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void FilteringHistoryKeepsTheTodayCountAndTheMatchingDateGroup()
    {
        var today = new DateTime(2026, 10, 20);
        DateTimeOffset Local(int day) => new(today.AddDays(day).AddHours(12),
            TimeZoneInfo.Local.GetUtcOffset(today.AddDays(day).AddHours(12)));
        HomeHistoryItem[] items = [History(Local(0), "newest"), History(Local(0), "another"), History(Local(-1), "older")];

        var model = HomeHistoryProjection.Create(items, "OLDER", false, _ => "Speech model", today);

        Assert.Equal(2, model.TodayCount);
        var group = Assert.Single(model.Groups);
        Assert.Equal("YESTERDAY", group.Label);
        var entry = Assert.Single(group.Entries);
        Assert.Equal("older", entry.Text);
        Assert.Equal("Speech model", entry.ModelName);
    }

    [Fact]
    public void MissingUsageStaysUnknownAndLoadedEmptyUsageShowsZero()
    {
        var today = new DateOnly(2026, 10, 20);
        var missing = HomeInsightsProjection.Create(null, "Cannot read local usage.", _ => null, today, TimeZoneInfo.Utc);
        var empty = HomeInsightsProjection.Create(new UsageDocument(), null, _ => null, today, TimeZoneInfo.Utc);

        Assert.Equal("--", missing.SessionsText);
        Assert.Equal("Cannot read local usage.", missing.AvailabilityMessage);
        Assert.Null(missing.Activity);
        Assert.Equal("0", empty.SessionsText);
        Assert.NotNull(empty.Activity);
        Assert.Null(empty.AvailabilityMessage);
    }

    [Fact]
    public void InsightsCountDictationWordsAndCompareModelsAgainstAllSessions()
    {
        var time = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        UsageEntry Entry(string model, UsageSource source, int words) => new()
        {
            Id = Guid.NewGuid(), CompletedAt = time, ModelId = model, Source = source, Words = words,
            RecordingSeconds = source == UsageSource.Dictation ? 30 : 0, ElapsedSeconds = 1
        };
        var document = new UsageDocument
        {
            Entries = [Entry("primary", UsageSource.Dictation, 30), Entry("primary", UsageSource.Dictation, 30),
                Entry("file", UsageSource.File, 600)]
        };

        var model = HomeInsightsProjection.Create(document, null, id => id, new DateOnly(2026, 10, 20), TimeZoneInfo.Utc);

        Assert.Equal("2", model.SessionsText);
        Assert.Equal("60", model.WordsText);
        Assert.Equal("60", model.PaceText);
        Assert.Equal(2, model.Models[0].Sessions);
        Assert.All(model.Models, row => Assert.Equal(3, row.MaximumSessions));
        Assert.Equal(3, model.Activity![new DateOnly(2026, 10, 20)]);
    }

    private static HomeHistoryItem History(DateTimeOffset time, string text) =>
        new(Guid.NewGuid(), time, text, "model", null, TimeSpan.FromSeconds(5));
}
