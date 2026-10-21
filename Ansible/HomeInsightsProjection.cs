using Ansible.Core;
using System.Globalization;

namespace Ansible;

internal sealed record HomeModelViewModel(string DisplayName, int Sessions, int MaximumSessions, bool IsPrimary);

internal sealed record HomeInsightsViewModel(
    string PaceText,
    string SessionsText,
    string WordsText,
    string StreakText,
    string Footnote,
    string? AvailabilityMessage,
    IReadOnlyList<HomeModelViewModel> Models,
    IReadOnlyDictionary<DateOnly, int>? Activity,
    DateOnly Today);

internal static class HomeInsightsProjection
{
    internal static HomeInsightsViewModel Create(
        UsageDocument? usage,
        string? usageError,
        Func<string, string?> modelName,
        DateOnly today,
        TimeZoneInfo timeZone)
    {
        if (usage is null)
        {
            return new HomeInsightsViewModel(
                "--", "--", "--", "--", usageError ?? "Usage statistics are not available yet.",
                usageError ?? "Usage statistics are not available yet.", new List<HomeModelViewModel>(), null, today);
        }

        var summary = UsageSummary.Create(usage.Entries, today, timeZone);
        var totalSessions = Math.Max(1, summary.Sessions);
        var models = summary.Models.Take(5).Select((model, index) => new HomeModelViewModel(
            modelName(model.ModelId) ?? "Unknown model", model.Sessions,
            totalSessions, index == 0)).ToList();
        var dayUnit = summary.CurrentStreak == 1 ? "day" : "days";
        var streak = summary.CurrentStreak == 0 ? "No streak" : $"{summary.CurrentStreak} {dayUnit} streak";
        return new HomeInsightsViewModel(
            summary.WordsPerMinute?.ToString("N0", CultureInfo.CurrentCulture) ?? "--",
            summary.DictationSessions.ToString("N0", CultureInfo.CurrentCulture),
            summary.DictatedWords.ToString("N0", CultureInfo.CurrentCulture),
            streak, Footnote(summary), null, models, summary.Activity, today);
    }

    private static string Footnote(UsageSummary summary)
    {
        if (summary.RecordingSeconds > 0)
            return $"{summary.RecordingSeconds / 60:N1} min of microphone time, pauses included.";
        if (summary.DictationSessions == 0) return "No completed dictations yet.";
        return "Microphone time is unavailable.";
    }
}
