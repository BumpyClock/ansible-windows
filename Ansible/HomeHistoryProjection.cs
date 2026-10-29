using Ansible.Core;
using System.Globalization;

namespace Ansible;

/// <summary>A row tag for a real insertion outcome. <see cref="Inserted"/> selects the accent treatment.</summary>
internal sealed record HomeHistoryTag(string Label, string Glyph, bool Inserted, string Meaning);

internal sealed record HomeHistoryEntry(HomeHistoryItem Item, string? ModelName)
{
    // Only real insertion outcomes get a tag. In-app dictations, deferred, and pending deliveries have none.
    public HomeHistoryTag? Tag => Item.Delivery switch
    {
        TextDeliveryStatus.Verified => new("Inserted", "\uE73E", true, "Inserted and confirmed in the target field."),
        TextDeliveryStatus.Sent => new("Not verified", "\uE946", false, "Sent to the active app, but the field could not confirm the text arrived."),
        TextDeliveryStatus.CleanupFailed => new("Not verified", "\uE7BA", false, "Windows could not release all injected keys. Check the keyboard and target field; none, some, or all of the text may have arrived."),
        TextDeliveryStatus.Rejected => new("Not inserted", "\uE711", false, "Insertion was stopped. Copy the transcript to paste it yourself."),
        TextDeliveryStatus.Partial => new("Partially inserted", "\uE7BA", false, "Only part of the text reached the target field. Copy the transcript to paste it yourself."),
        _ => null
    };

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

internal sealed record HomeHistoryGroup(string Label, IReadOnlyList<HomeHistoryEntry> Entries);

internal sealed record HomeHistoryViewModel(
    IReadOnlyList<HomeHistoryGroup> Groups,
    int TodayCount,
    bool HasHistory,
    bool HasMatches,
    bool OversizedCurrentResult,
    IReadOnlySet<Guid> AllOperationIds);

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
        return new HomeHistoryViewModel(groups, todayCount, all.Count > 0, filtered.Count > 0, oversizedCurrentResult,
            all.Select(item => item.OperationId).ToHashSet());
    }

    private static string GroupLabel(DateOnly date, DateOnly today)
    {
        if (date == today) return "TODAY";
        if (date == today.AddDays(-1)) return "YESTERDAY";
        return date.ToString("ddd, MMM d", CultureInfo.CurrentCulture).ToUpperInvariant();
    }
}
