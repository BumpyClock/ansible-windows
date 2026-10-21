using System.Globalization;

namespace Ansible;

internal sealed record HomeHistoryEntry(HomeHistoryItem Item, string? ModelName)
{
    public string TimeText => Item.CompletedAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
    public string Text => Item.Text;
    public string MetadataText
    {
        get
        {
            var wordCount = "Word count unavailable";
            if (Item.Words is { } words)
            {
                var unit = words == 1 ? "word" : "words";
                wordCount = $"{words:N0} {unit}";
            }
            var metadata = $"{wordCount} · {Item.Elapsed:mm\\:ss}";
            if (ModelName is { Length: > 0 }) metadata += $" · {ModelName}";
            return metadata;
        }
    }
}

internal sealed record HomeHistoryGroup(string Label, IReadOnlyList<HomeHistoryEntry> Entries)
{
    // ItemsRepeater consumes a WinRT object vector, while projection callers retain typed entries.
    public IList<object> BindableEntries { get; } = Entries.Cast<object>().ToList();
}

internal sealed record HomeHistoryViewModel(
    IReadOnlyList<HomeHistoryGroup> Groups,
    int TodayCount,
    bool HasHistory,
    bool HasMatches,
    bool OversizedCurrentResult);

internal static class HomeHistoryProjection
{
    internal static HomeHistoryViewModel Create(
        IReadOnlyList<HomeHistoryItem> all,
        string query,
        bool oversizedCurrentResult,
        Func<string, string?> modelName,
        DateTime today)
    {
        var filtered = query.Length == 0
            ? all
            : all.Where(item => item.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        var todayCount = all.Count(item => item.CompletedAt.LocalDateTime.Date == today.Date);
        var groups = filtered
            .GroupBy(item => DateOnly.FromDateTime(item.CompletedAt.LocalDateTime))
            .Select(group => new HomeHistoryGroup(
                GroupLabel(group.Key, DateOnly.FromDateTime(today)),
                group.Select(item => new HomeHistoryEntry(item,
                    item.ModelId.Length > 0 ? modelName(item.ModelId) : null)).ToList()))
            .ToList();
        return new HomeHistoryViewModel(groups, todayCount, all.Count > 0, filtered.Count > 0, oversizedCurrentResult);
    }

    private static string GroupLabel(DateOnly date, DateOnly today)
    {
        if (date == today) return "TODAY";
        if (date == today.AddDays(-1)) return "YESTERDAY";
        return date.ToString("ddd, MMM d", CultureInfo.CurrentCulture).ToUpperInvariant();
    }
}
