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
        Assert.Equal("--", missing.WordsText);
        Assert.Equal("--", missing.MicrophoneMinutesText);
        Assert.Equal("--", missing.StreakText);
        Assert.Equal("Cannot read local usage.", missing.AvailabilityMessage);
        Assert.Null(missing.Activity);
        Assert.Null(missing.HourlyActivity);
        Assert.Equal("0", empty.SessionsText);
        Assert.Equal("0", empty.WordsText);
        Assert.Equal(0.ToString("N1"), empty.MicrophoneMinutesText);
        Assert.Equal("No streak", empty.StreakText);
        Assert.NotNull(empty.Activity);
        Assert.Empty(empty.HourlyActivity!);
        Assert.Null(empty.AvailabilityMessage);
    }

    [Fact]
    public void InsightsKeepExistingCountsAndExposeLaterUsageWriteFailures()
    {
        const string error = "Local counts could not be saved because the background usage queue is full.";
        var view = HomeInsightsProjection.Create(new UsageDocument(), error, _ => null,
            new DateOnly(2026, 10, 20), TimeZoneInfo.Utc);

        Assert.Equal("0", view.SessionsText);
        Assert.NotNull(view.Activity);
        Assert.Null(view.AvailabilityMessage);
        Assert.Equal(error, view.UsageWarning);
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
        Assert.Equal(2, model.Activity![new DateOnly(2026, 10, 20)]);
        Assert.Equal(1.ToString("N1"), model.MicrophoneMinutesText);
    }

    [Fact]
    public void UnknownDictationWordsDoNotBecomeZeroOrAnIncompleteHeadline()
    {
        var time = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var usage = new UsageDocument
        {
            Entries =
            [
                new UsageEntry { Id = Guid.NewGuid(), CompletedAt = time, ModelId = "model",
                    Source = UsageSource.Dictation, Words = null, RecordingSeconds = 120, ElapsedSeconds = 1 },
                new UsageEntry { Id = Guid.NewGuid(), CompletedAt = time, ModelId = "model",
                    Source = UsageSource.Dictation, Words = 30, RecordingSeconds = 60, ElapsedSeconds = 1 }
            ]
        };

        var model = HomeInsightsProjection.Create(usage, null, _ => null, new DateOnly(2026, 10, 20), TimeZoneInfo.Utc);

        Assert.Equal("--", model.WordsText);
        Assert.Equal("--", model.PaceText);
        Assert.Equal("2", model.SessionsText);
        Assert.Equal(3.ToString("N1"), model.MicrophoneMinutesText);
        Assert.Contains("no word count", model.Footnote);
        Assert.Equal(2, model.Activity![new DateOnly(2026, 10, 20)]);
    }

    [Fact]
    public void UnknownFileWordsDoNotHideMeasuredDictationWords()
    {
        var time = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var usage = new UsageDocument
        {
            Entries =
            [
                new UsageEntry { Id = Guid.NewGuid(), CompletedAt = time, ModelId = "model",
                    Source = UsageSource.Dictation, Words = 60, RecordingSeconds = 30, ElapsedSeconds = 1 },
                new UsageEntry { Id = Guid.NewGuid(), CompletedAt = time, ModelId = "file",
                    Source = UsageSource.File, Words = null, RecordingSeconds = 0, ElapsedSeconds = 1 }
            ]
        };

        var model = HomeInsightsProjection.Create(usage, null, id => id, new DateOnly(2026, 10, 20), TimeZoneInfo.Utc);

        Assert.Equal("60", model.WordsText);
        Assert.Equal("120", model.PaceText);
        Assert.Equal(2, model.Models.Count);
        Assert.Equal(1, model.Activity![new DateOnly(2026, 10, 20)]);
    }

    [Theory]
    [InlineData(false, 7)]
    [InlineData(true, 30)]
    public void ActivityWindowIncludesTodayAndExactlyTheRequestedLocalDays(bool month, int days)
    {
        var today = new DateOnly(2026, 10, 20);
        var start = today.AddDays(1 - days);
        HomeActivityHour[] activity =
        [
            new(start.AddDays(-1), 0, 100),
            new(start, 6, 2),
            new(today.AddDays(-1), 12, 4),
            new(today, 18, 1),
            new(today.AddDays(1), 23, 100)
        ];

        var chart = HomeInsightsProjection.CreateActivity(activity, today, month);

        Assert.Equal(24, chart.Hours.Count);
        Assert.Equal(Enumerable.Range(0, 24), chart.Hours.Select(hour => hour.Hour));
        Assert.Equal(start.ToString("d MMM"), chart.StartText);
        Assert.Equal(today.ToString("d MMM"), chart.EndText);
        Assert.Equal("7 dictations", chart.TotalText);
        Assert.Equal("7", chart.TotalValueText);
        Assert.Equal("dictations", chart.UnitText);
        Assert.Contains(days.ToString(), chart.PeriodText);
        Assert.Equal("4 dictations", chart.ScaleText);
        Assert.Equal(2, chart.Hours[6].Dictations);
        Assert.Equal(4, chart.Hours[12].Dictations);
        Assert.Equal(1, chart.Hours[18].Dictations);
        Assert.Equal(0, chart.Hours[0].Dictations);
        Assert.Equal(0, chart.Hours[23].Dictations);
        Assert.Equal(7, chart.Hours.Sum(hour => hour.Dictations));
        Assert.Contains("1 dictation", chart.Hours[18].Description);
        Assert.DoesNotContain("1 dictations", chart.Hours[18].Description);
    }

    [Fact]
    public void DictationActivityUsesLocalCalendarBoundariesAndExcludesFiles()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test West", TimeSpan.FromHours(-7), "Test West", "Test West");
        var today = new DateOnly(2026, 10, 20);
        UsageEntry Entry(int hour, UsageSource source) => new()
        {
            Id = Guid.NewGuid(), CompletedAt = new DateTimeOffset(2026, 10, 20, hour, 0, 0, TimeSpan.Zero),
            ModelId = "model", Source = source, Words = 10,
            RecordingSeconds = source == UsageSource.Dictation ? 10 : 0, ElapsedSeconds = 1
        };
        var usage = new UsageDocument
        {
            Entries = [Entry(6, UsageSource.Dictation), Entry(7, UsageSource.Dictation), Entry(8, UsageSource.File)]
        };

        var model = HomeInsightsProjection.Create(usage, null, id => id, today, zone);

        Assert.Equal(1, model.Activity![today.AddDays(-1)]);
        Assert.Equal(1, model.Activity[today]);
        Assert.Equal("2 days streak", model.StreakText);
        Assert.Equal(3, Assert.Single(model.Models).Sessions);
        Assert.Equal(
            [new HomeActivityHour(today.AddDays(-1), 23, 1), new HomeActivityHour(today, 0, 1)],
            model.HourlyActivity);
        var chart = HomeInsightsProjection.CreateActivity(model.HourlyActivity!, today, false);
        Assert.Equal(1, chart.Hours[23].Dictations);
        Assert.Equal(1, chart.Hours[0].Dictations);
        Assert.Equal(0, chart.Hours[1].Dictations);
    }

    [Fact]
    public void FileSessionsDoNotExtendTheDictationStreak()
    {
        var today = new DateOnly(2026, 10, 20);
        UsageEntry Entry(int day, UsageSource source) => new()
        {
            Id = Guid.NewGuid(), CompletedAt = new DateTimeOffset(2026, 10, day, 12, 0, 0, TimeSpan.Zero),
            ModelId = "model", Source = source, Words = 10,
            RecordingSeconds = source == UsageSource.Dictation ? 10 : 0, ElapsedSeconds = 1
        };
        var usage = new UsageDocument
        {
            Entries = [Entry(20, UsageSource.File), Entry(19, UsageSource.Dictation),
                Entry(18, UsageSource.Dictation), Entry(17, UsageSource.File)]
        };

        var model = HomeInsightsProjection.Create(usage, null, id => id, today, TimeZoneInfo.Utc);

        Assert.Equal("2 days streak", model.StreakText);
    }

    [Fact]
    public void EmptyActivityHasNoHighlightedDotsAndAnExplicitEmptyState()
    {
        var chart = HomeInsightsProjection.CreateActivity([], new DateOnly(2026, 10, 20), false);

        Assert.True(chart.IsEmpty);
        Assert.Equal("0 dictations", chart.TotalText);
        Assert.Equal("0 dictations", chart.ScaleText);
        Assert.Equal(24, chart.Hours.Count);
        Assert.All(chart.Hours, hour =>
        {
            Assert.Equal(0, hour.Dictations);
            Assert.Contains("0 dictations", hour.Description);
        });
        var dots = HomeInsightsProjection.CreateClockDots(chart.Hours);
        Assert.NotEmpty(dots);
        Assert.DoesNotContain(dots, dot => dot.IsActive);
    }

    [Fact]
    public void ClockCombinesTheSameCompletionHourAcrossDaysAndScalesAgainstItsBusiestHour()
    {
        var today = new DateOnly(2026, 10, 20);
        HomeActivityHour[] activity = [new(today, 0, 1), new(today.AddDays(-1), 0, 1), new(today, 6, 4)];

        var chart = HomeInsightsProjection.CreateActivity(activity, today, false);

        Assert.Equal("6 dictations", chart.TotalText);
        Assert.Equal(2, chart.Hours[0].Dictations);
        Assert.Equal(4, chart.Hours[6].Dictations);
        var dots = HomeInsightsProjection.CreateClockDots(chart.Hours);
        var midnight = dots.Where(dot => dot.X > 160 && dot.Y < 160).ToArray();
        var six = dots.Where(dot => dot.X > 160 && dot.Y > 160).ToArray();
        Assert.Equal(20, midnight.Count(dot => dot.IsActive));
        Assert.Equal(40, six.Count(dot => dot.IsActive));
        Assert.DoesNotContain(dots, dot => dot.X < 160 && dot.IsActive);
        Assert.All(dots, dot =>
        {
            var radius = Math.Sqrt(Math.Pow(dot.X - 160, 2) + Math.Pow(dot.Y - 160, 2));
            Assert.InRange(radius, 75.99, 121.01);
            Assert.InRange(dot.X, 38, 282);
            Assert.InRange(dot.Y, 38, 282);
        });
    }

    [Fact]
    public void ClockKeepsSparseActivityVisibleAndUsesSingularUnits()
    {
        var today = new DateOnly(2026, 10, 20);
        var single = HomeInsightsProjection.CreateActivity([new(today, 18, 1)], today, false);
        var sparse = HomeInsightsProjection.CreateActivity([new(today, 18, 1), new(today, 6, 100)], today, false);

        Assert.Equal("1 dictation", single.TotalText);
        Assert.Equal("dictation", single.UnitText);
        Assert.Equal("1 dictation", single.ScaleText);
        var dots = HomeInsightsProjection.CreateClockDots(sparse.Hours);
        Assert.Contains(dots, dot => dot.IsActive && dot.X < 160 && dot.Y < 160);
        Assert.DoesNotContain(dots, dot => dot.IsActive && dot.X < 160 && dot.Y > 160);
    }

    [Fact]
    public void ClockCombinesRepeatedLocalHoursAtTheDaylightSavingTransition()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var today = new DateOnly(2026, 11, 1);
        var usage = new UsageDocument
        {
            Entries = [Entry(8), Entry(9)]
        };
        UsageEntry Entry(int hour) => new()
        {
            Id = Guid.NewGuid(), CompletedAt = new DateTimeOffset(2026, 11, 1, hour, 30, 0, TimeSpan.Zero),
            ModelId = "model", Source = UsageSource.Dictation, Words = 10, RecordingSeconds = 10, ElapsedSeconds = 1
        };

        var model = HomeInsightsProjection.Create(usage, null, _ => "Model", today, zone);
        var chart = HomeInsightsProjection.CreateActivity(model.HourlyActivity!, today, false);

        Assert.Equal(new HomeActivityHour(today, 1, 2), Assert.Single(model.HourlyActivity!));
        Assert.Equal(2, chart.Hours[1].Dictations);
        Assert.Equal(2, chart.Hours.Sum(hour => hour.Dictations));
    }

    [Theory]
    [InlineData(TextDeliveryStatus.Verified, "Inserted", true)]
    [InlineData(TextDeliveryStatus.CleanupFailed, "Not verified", false)]
    [InlineData(TextDeliveryStatus.Sent, "Not verified", false)]
    [InlineData(TextDeliveryStatus.Partial, "Partially inserted", false)]
    [InlineData(TextDeliveryStatus.Rejected, "Not inserted", false)]
    public void HistoryTagNamesTheReportedInsertionOutcome(TextDeliveryStatus status, string label, bool inserted)
    {
        var item = History(DateTimeOffset.Now, "speech") with { Delivery = status };

        var tag = new HomeHistoryEntry(item, null).Tag;

        Assert.NotNull(tag);
        Assert.Equal(label, tag.Label);
        Assert.Equal(inserted, tag.Inserted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(TextDeliveryStatus.Deferred)]
    public void HistoryTagIsAbsentWithoutAReportedInsertion(TextDeliveryStatus? status)
    {
        var item = History(DateTimeOffset.Now, "speech") with { Delivery = status };

        Assert.Null(new HomeHistoryEntry(item, null).Tag);
    }

    [Fact]
    public async Task FailedReleaseOfPartialPasteNeverClaimsInsertionOrReplaysInput()
    {
        var insertions = 0;
        var sentKeys = new List<WindowsTextTarget.KeyboardEvent[]>();
        var delivery = new FinalTextDelivery((_, _) =>
        {
            insertions++;
            var result = WindowsTextTarget.SendPasteChord(PasteShortcut.ControlV, keys =>
            {
                sentKeys.Add(keys);
                return sentKeys.Count == 1 ? 1u : 0u;
            }, _ => throw new InvalidOperationException("A partial chord must not wait."), () => false);
            return Task.FromResult(result!.Value);
        }, () => true, () => false);

        await delivery.RunAsync(Task.FromResult(new SessionOutcome(SessionOutcomeKind.Completed,
            new RecognitionResult("speech", "speech"))), CancellationToken.None);

        Assert.Equal(1, insertions);
        Assert.False(delivery.Sent);
        Assert.Equal(TextDeliveryStatus.CleanupFailed, delivery.Delivery!.Value.Status);
        Assert.Contains("release", delivery.Error);
        Assert.All(sentKeys.Skip(1), keys =>
        {
            var key = Assert.Single(keys);
            Assert.Equal((ushort)0x11, key.Key.VirtualKey);
            Assert.Equal(2u, key.Key.Flags);
        });
        var tag = new HomeHistoryEntry(History(DateTimeOffset.Now, "speech") with
            { Delivery = delivery.Delivery.Value.Status }, null).Tag!;
        Assert.Equal("Not verified", tag.Label);
        Assert.False(tag.Inserted);
        Assert.Contains("keys", tag.Meaning);
        Assert.DoesNotContain("clipboard", tag.Meaning, StringComparison.OrdinalIgnoreCase);
    }

    private static HomeHistoryItem History(DateTimeOffset time, string text) =>
        new(Guid.NewGuid(), time, text, "model", null, TimeSpan.FromSeconds(5));
}
