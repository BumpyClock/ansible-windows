namespace DictationPoc.Core;

public sealed record ModelUsage(string ModelId, int Sessions, long Words);

public sealed record UsageSummary(
    long TotalWords,
    long DictatedWords,
    long FileWords,
    int DictationSessions,
    int FileSessions,
    double RecordingSeconds,
    double? WordsPerMinute,
    int CurrentStreak,
    int LongestStreak,
    IReadOnlyList<ModelUsage> Models,
    IReadOnlyDictionary<DateOnly, int> Activity,
    int UnknownWordSessions)
{
    public int Sessions => DictationSessions + FileSessions;

    public static UsageSummary Create(
        IEnumerable<UsageEntry> entries, DateOnly today, TimeZoneInfo timeZone)
    {
        var records = entries.ToArray();
        var dictations = records.Where(entry => entry.Source == UsageSource.Dictation).ToArray();
        var dictatedWords = dictations.Sum(entry => (long)(entry.Words ?? 0));
        var fileWords = records.Where(entry => entry.Source == UsageSource.File).Sum(entry => (long)(entry.Words ?? 0));
        var recordingSeconds = dictations.Where(entry => entry.Words.HasValue).Sum(entry => entry.RecordingSeconds);
        var activity = records.GroupBy(entry =>
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(entry.CompletedAt, timeZone).DateTime))
            .ToDictionary(group => group.Key, group => group.Count());
        var currentStreak = 0;
        var cursor = activity.ContainsKey(today) ? today : today.AddDays(-1);
        while (activity.ContainsKey(cursor))
        {
            currentStreak++;
            cursor = cursor.AddDays(-1);
        }
        var longestStreak = 0;
        var streak = 0;
        DateOnly? previous = null;
        foreach (var date in activity.Keys.Order())
        {
            streak = previous?.AddDays(1) == date ? streak + 1 : 1;
            longestStreak = Math.Max(longestStreak, streak);
            previous = date;
        }
        var models = records.GroupBy(entry => entry.ModelId, StringComparer.Ordinal)
            .Select(group => new ModelUsage(group.Key, group.Count(), group.Sum(entry => (long)(entry.Words ?? 0))))
            .OrderByDescending(model => model.Sessions)
            .ThenBy(model => model.ModelId, StringComparer.Ordinal)
            .ToArray();
        return new UsageSummary(
            dictatedWords + fileWords, dictatedWords, fileWords, dictations.Length,
            records.Length - dictations.Length, recordingSeconds,
            recordingSeconds > 0 ? dictatedWords * 60.0 / recordingSeconds : null,
            currentStreak, longestStreak, models, activity, records.Count(entry => entry.Words is null));
    }
}
