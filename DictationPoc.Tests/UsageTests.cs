using System.Text;
using DictationPoc.Core;

namespace DictationPoc.Tests;

public sealed class UsageTests
{
    [Fact]
    public void EmptyUsageHasNoInventedPaceOrActivity()
    {
        var summary = UsageSummary.Create([], new DateOnly(2026, 10, 2), TimeZoneInfo.Utc);
        Assert.Null(summary.WordsPerMinute);
        Assert.Equal(0, summary.Sessions);
        Assert.Equal(0, summary.TotalWords);
        Assert.Empty(summary.Activity);
        Assert.Empty(summary.Models);
    }

    [Fact]
    public void PaceUsesRecordedDictationTimeAndExcludesFiles()
    {
        var entries = new[]
        {
            Entry(2, "moonshine", UsageSource.Dictation, 60, 30),
            Entry(2, "moonshine", UsageSource.Dictation, 30, 60),
            Entry(1, "vibevoice", UsageSource.File, 1000, 0)
        };
        var summary = UsageSummary.Create(entries, new DateOnly(2026, 10, 2), TimeZoneInfo.Utc);
        Assert.Equal(60, summary.WordsPerMinute);
        Assert.Equal(90, summary.DictatedWords);
        Assert.Equal(1000, summary.FileWords);
        Assert.Equal(1090, summary.TotalWords);
        Assert.Equal(2, summary.DictationSessions);
        Assert.Equal(1, summary.FileSessions);
        Assert.Equal(2, summary.CurrentStreak);
        Assert.Equal(2, summary.LongestStreak);
        Assert.Equal(2, summary.Models[0].Sessions);
        Assert.Equal(2, summary.Activity[new DateOnly(2026, 10, 2)]);
    }

    [Fact]
    public void ActivityUsesLocalCalendarDays()
    {
        var entry = Entry(2, "moonshine", UsageSource.Dictation, 8, 4) with
        {
            CompletedAt = new DateTimeOffset(2026, 10, 2, 1, 0, 0, TimeSpan.Zero)
        };
        var zone = TimeZoneInfo.CreateCustomTimeZone("test-west", TimeSpan.FromHours(-7), "test-west", "test-west");
        var summary = UsageSummary.Create([entry], new DateOnly(2026, 10, 2), zone);
        Assert.True(summary.Activity.ContainsKey(new DateOnly(2026, 10, 1)));
        Assert.Equal(1, summary.CurrentStreak);
    }

    [Fact]
    public async Task StoreDeduplicatesSessionsAndHonorsDisabledCollection()
    {
        var directory = System.IO.Path.Combine(AppContext.BaseDirectory, "usage-test-" + Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "usage.json");
        var store = new UsageStore(path);
        try
        {
            var entry = Entry(2, "moonshine", UsageSource.Dictation, 8, 4);
            await store.RecordAsync(entry);
            await store.RecordAsync(entry);
            await store.SetEnabledAsync(false);
            await store.RecordAsync(Entry(2, "vibevoice", UsageSource.File, 20, 0));
            var data = await store.LoadAsync();
            Assert.False(data.Enabled);
            Assert.Single(data.Entries);
            var wire = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("transcript", wire, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("audio", wire, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("path", wire, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task CorruptUsageIsNotSilentlyReplaced()
    {
        var directory = System.IO.Path.Combine(AppContext.BaseDirectory, "usage-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "usage.json");
        try
        {
            await File.WriteAllTextAsync(path, "{broken");
            var store = new UsageStore(path);
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
                store.RecordAsync(Entry(2, "moonshine", UsageSource.Dictation, 8, 4)));
            Assert.Equal("{broken", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void WaveformUsesActualPcmEnergy()
    {
        Assert.Equal(0, AudioMeter.Pcm16Level(new byte[80]));
        Assert.Equal(1, AudioMeter.Pcm16Level([0, 128, 0, 128]), 8);
        Assert.Equal(2, AudioMeter.CountWords("  Hello,\nworld. "));
    }

    [Fact]
    public void UnknownSpeechContentDoesNotBecomeZeroPaceOrAnnotatedWords()
    {
        var entry = Entry(2, "vibevoice", UsageSource.Dictation, 0, 30) with { Words = null };
        var summary = UsageSummary.Create([entry], new DateOnly(2026, 10, 2), TimeZoneInfo.Utc);
        Assert.Equal(1, summary.DictationSessions);
        Assert.Equal(1, summary.UnknownWordSessions);
        Assert.Null(summary.WordsPerMinute);
        Assert.Equal(0, summary.TotalWords);
    }

    [Fact]
    public void RevisitedUsageCanChangeStreakWithoutChangingItsRecords()
    {
        var entries = new[] { Entry(1, "moonshine", UsageSource.Dictation, 8, 4) };
        var beforeMidnight = UsageSummary.Create(entries, new DateOnly(2026, 10, 2), TimeZoneInfo.Utc);
        var nextDay = UsageSummary.Create(entries, new DateOnly(2026, 10, 3), TimeZoneInfo.Utc);
        Assert.Equal(1, beforeMidnight.CurrentStreak);
        Assert.Equal(0, nextDay.CurrentStreak);
        Assert.Equal(beforeMidnight.TotalWords, nextDay.TotalWords);
        Assert.Equal(beforeMidnight.Sessions, nextDay.Sessions);
    }

    private static UsageEntry Entry(int day, string model, UsageSource source, int words, double seconds) => new()
    {
        Id = Guid.NewGuid(),
        CompletedAt = new DateTimeOffset(2026, 10, day, 12, 0, 0, TimeSpan.Zero),
        ModelId = model,
        Source = source,
        Words = words,
        RecordingSeconds = seconds,
        ElapsedSeconds = seconds + 1
    };
}
