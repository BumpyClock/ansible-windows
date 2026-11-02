namespace Ansible.Core;

public sealed record ModelUsage(string ModelId, int Sessions);

public sealed record UsageSummary(
    long DictatedWords,
    int DictationSessions,
    int FileSessions,
    double RecordingSeconds,
    double? WordsPerMinute,
    int CurrentStreak,
    IReadOnlyList<ModelUsage> Models,
    IReadOnlyDictionary<DateOnly, int> Activity)
{
    public int Sessions => DictationSessions + FileSessions;

    public static UsageSummary Create(
        IEnumerable<UsageEntry> entries, DateOnly today, TimeZoneInfo timeZone)
    {
        var records = entries.ToArray();
        var dictations = records.Where(entry => entry.Source == UsageSource.Dictation).ToArray();
        var dictatedWords = dictations.Sum(entry => (long)(entry.Words ?? 0));
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
        var models = records.GroupBy(entry => entry.ModelId, StringComparer.Ordinal)
            .Select(group => new ModelUsage(group.Key, group.Count()))
            .OrderByDescending(model => model.Sessions)
            .ThenBy(model => model.ModelId, StringComparer.Ordinal)
            .ToArray();
        return new UsageSummary(
            dictatedWords, dictations.Length, records.Length - dictations.Length, recordingSeconds,
            recordingSeconds > 0 ? dictatedWords * 60.0 / recordingSeconds : null,
            currentStreak, models, activity);
    }
}
