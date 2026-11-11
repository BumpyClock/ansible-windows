using Ansible.Core;
using System.Globalization;

namespace Ansible;

/// <summary>
/// A model as Home names it: the plain name Speech models shows, and the full catalog name for its tooltip.
/// </summary>
internal sealed record HomeModelName(string Name, string FullName)
{
    internal static HomeModelName From(NativeModelEntry entry) =>
        new(ModelCardText.PlainName(entry), entry.DisplayName ?? entry.Id);
}

internal sealed record HomeModelViewModel(string DisplayName, string FullName, int Sessions, int MaximumSessions, bool IsPrimary);

internal sealed record HomeActivityHour(DateOnly Day, int Hour, int Dictations);

internal sealed record HomeClockHour(int Hour, int Dictations, string Description);

internal readonly record struct HomeClockDot(double X, double Y, bool IsActive);

internal sealed record HomeActivityViewModel(
    string PeriodText,
    string TotalText,
    string TotalValueText,
    string UnitText,
    string ScaleText,
    string StartText,
    string EndText,
    bool IsEmpty,
    IReadOnlyList<HomeClockHour> Hours);

internal sealed record HomeInsightsViewModel(
    string PaceText,
    string SessionsText,
    string WordsText,
    string MicrophoneMinutesText,
    string Footnote,
    string? AvailabilityMessage,
    IReadOnlyList<HomeModelViewModel> Models,
    IReadOnlyDictionary<DateOnly, int>? Activity,
    IReadOnlyList<HomeActivityHour>? HourlyActivity,
    DateOnly Today,
    string? UsageWarning,
    string? StreakText,
    int? Dictations,
    string? LastDictationText);

internal static class HomeInsightsProjection
{
    internal static HomeInsightsViewModel Create(
        UsageDocument? usage,
        string? usageError,
        Func<string, HomeModelName?> modelName,
        DateOnly today,
        TimeZoneInfo timeZone)
    {
        if (usage is null)
        {
            return new HomeInsightsViewModel(
                "--", "--", "--", "--", usageError ?? "Usage statistics are not available yet.",
                usageError ?? "Usage statistics are not available yet.", new List<HomeModelViewModel>(), null, null, today, null, null, null, null);
        }

        var summary = UsageSummary.Create(usage.Entries, today, timeZone);
        var totalSessions = Math.Max(1, summary.Sessions);
        var models = summary.Models.Take(5).Select((model, index) =>
        {
            var name = modelName(model.ModelId);
            return new HomeModelViewModel(name?.Name ?? "Unknown model", name?.FullName ?? model.ModelId,
                model.Sessions, totalSessions, index == 0);
        }).ToList();
        var dictations = usage.Entries.Where(entry => entry.Source == UsageSource.Dictation).ToArray();
        var unknownWords = dictations.Count(entry => entry.Words is null);
        var microphoneSeconds = dictations.Sum(entry => entry.RecordingSeconds);
        var activity = dictations.GroupBy(entry =>
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(entry.CompletedAt, timeZone).DateTime))
            .ToDictionary(group => group.Key, group => group.Count());
        var hourlyActivity = dictations.Select(entry => TimeZoneInfo.ConvertTime(entry.CompletedAt, timeZone))
            .GroupBy(local => (Day: DateOnly.FromDateTime(local.DateTime), local.Hour))
            .Select(group => new HomeActivityHour(group.Key.Day, group.Key.Hour, group.Count())).ToArray();
        var streak = 0;
        var cursor = activity.ContainsKey(today) ? today : today.AddDays(-1);
        while (activity.ContainsKey(cursor))
        {
            streak++;
            cursor = cursor.AddDays(-1);
        }
        // After a failed usage write the newest record may not be the last dictation, so the date is left out.
        var lastDictation = usageError is null && dictations.Length > 0
            ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(dictations.Max(entry => entry.CompletedAt), timeZone).DateTime)
            : (DateOnly?)null;
        var footnote = unknownWords > 0
            ? $"Word count and pace unavailable. {unknownWords:N0} {(unknownWords == 1 ? "dictation has" : "dictations have")} no word count. Microphone time includes pauses."
            : dictations.Length == 0 ? "No completed dictations yet." : "Pace uses microphone time, including pauses.";
        return new HomeInsightsViewModel(
            unknownWords == 0 ? summary.WordsPerMinute?.ToString("N0", CultureInfo.CurrentCulture) ?? "--" : "--",
            summary.DictationSessions.ToString("N0", CultureInfo.CurrentCulture),
            unknownWords == 0 ? summary.DictatedWords.ToString("N0", CultureInfo.CurrentCulture) : "--",
            (microphoneSeconds / 60).ToString("N1", CultureInfo.CurrentCulture),
            footnote, null, models, activity, hourlyActivity, today, usageError,
            streak == 0 ? null : $"{streak:N0} {(streak == 1 ? "day" : "days")} streak", summary.DictationSessions,
            lastDictation is { } day
                ? $"Last dictation {day.ToString(day.Year == today.Year ? "d MMM" : "d MMM yyyy", CultureInfo.CurrentCulture)}"
                : null);
    }

    internal static HomeActivityViewModel CreateActivity(
        IReadOnlyList<HomeActivityHour> activity, DateOnly today, bool month)
    {
        var days = month ? 30 : 7;
        var start = today.AddDays(1 - days);
        var selected = activity.Where(item => item.Day >= start && item.Day <= today)
            .GroupBy(item => item.Hour).ToDictionary(group => group.Key, group => group.Sum(item => item.Dictations));
        var counts = Enumerable.Range(0, 24).Select(hour => selected.GetValueOrDefault(hour)).ToArray();
        var maximum = counts.Max();
        var total = counts.Sum(count => (long)count);
        var hours = counts.Select((count, hour) =>
        {
            var unit = count == 1 ? "dictation" : "dictations";
            return new HomeClockHour(hour, count,
                $"{hour:00}:00 to {hour:00}:59 local time: {count:N0} {unit}");
        }).ToArray();
        return new HomeActivityViewModel(
            $"Last {days} days",
            $"{total:N0} {(total == 1 ? "dictation" : "dictations")}",
            total.ToString("N0", CultureInfo.CurrentCulture),
            total == 1 ? "dictation" : "dictations",
            $"{maximum:N0} {(maximum == 1 ? "dictation" : "dictations")}",
            start.ToString("d MMM", CultureInfo.CurrentCulture),
            today.ToString("d MMM", CultureInfo.CurrentCulture),
            total == 0, hours);
    }

    internal static IReadOnlyList<HomeClockDot> CreateClockDots(IReadOnlyList<HomeClockHour> hours)
    {
        const int rings = 10;
        const int spokesPerHour = 4;
        var maximum = hours.Max(hour => hour.Dictations);
        var dots = new List<HomeClockDot>(24 * spokesPerHour * rings);
        foreach (var hour in hours)
        {
            var filledRings = maximum == 0 || hour.Dictations == 0 ? 0
                : Math.Max(1, (int)Math.Ceiling(hour.Dictations * (double)rings / maximum));
            for (var spoke = 0; spoke < spokesPerHour; spoke++)
            {
                var angle = (hour.Hour + (spoke + 0.5) / spokesPerHour) * Math.Tau / 24 - Math.PI / 2;
                for (var ring = 0; ring < rings; ring++)
                {
                    var radius = 76 + ring * 5;
                    dots.Add(new HomeClockDot(160 + Math.Cos(angle) * radius,
                        160 + Math.Sin(angle) * radius, ring < filledRings));
                }
            }
        }
        return dots;
    }
}
