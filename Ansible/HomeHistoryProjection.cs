using Ansible.Core;
using System.Globalization;

namespace Ansible;

internal enum HomeHistoryTagTone { Inserted, Unverified, NotInserted }

internal sealed record HomeHistoryTag(string Label, string Glyph, HomeHistoryTagTone Tone, string Meaning)
{
    public bool NeedsCopy => Tone == HomeHistoryTagTone.NotInserted;
}

internal sealed record HomeHistoryEntry(HomeHistoryItem Item, HomeModelName? Model)
{
    public HomeHistoryTag? Tag => Item.Delivery switch
    {
        TextDeliveryStatus.Verified => new("Inserted", "\uE73E", HomeHistoryTagTone.Inserted,"Inserted and confirmed in the target field."),
        TextDeliveryStatus.Sent => new("Not verified", "\uE946", HomeHistoryTagTone.Unverified,"Sent to the active app, but the field could not confirm the text arrived."),
        TextDeliveryStatus.CleanupFailed => new("Not verified", "\uE7BA", HomeHistoryTagTone.Unverified,"Windows could not release all injected keys. Check the keyboard and target field; none, some, or all of the text may have arrived."),
        TextDeliveryStatus.Rejected => new("Not inserted", "\uE711", HomeHistoryTagTone.NotInserted,"Insertion was stopped. Copy the transcript to paste it yourself."),
        TextDeliveryStatus.Partial => new("Partially inserted", "\uE7BA", HomeHistoryTagTone.NotInserted,"Only part of the text reached the target field. Copy the transcript to paste it yourself."),
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
            if (Model?.Name is { Length: > 0 } name) metadata += $" · {name}";
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
    IReadOnlySet<Guid> AllOperationIds,
    string? StorageError = null);

internal enum HomeHistoryEmptyKind { NoMatches, NothingSaved, FirstRun }

internal sealed record HomeHistoryEmptyState(HomeHistoryEmptyKind Kind, string Title, string Detail);

internal static class HomeHistoryProjection
{
    internal static HomeHistoryEmptyState? EmptyState(HomeHistoryViewModel model, string query, int? dictations) =>
        model.HasMatches ? null
        : model.HasHistory ? new(HomeHistoryEmptyKind.NoMatches, "No matches", $"No saved transcript contains \"{query}\".")
        : model.StorageError is not null ? new(HomeHistoryEmptyKind.NothingSaved, "No saved dictations",
            "History storage has a problem, so saved transcripts may be missing.")
        : dictations == 0 ? new(HomeHistoryEmptyKind.FirstRun, "No dictations yet", "Your transcripts appear here after you dictate.")
        : new(HomeHistoryEmptyKind.NothingSaved, "No saved dictations", "New dictations appear here." + dictations switch
        {
            1 => " Your earlier dictation is no longer saved.",
            { } count => $" {count:N0} earlier dictations are no longer saved.",
            null => ""
        });

    internal static HomeHistoryViewModel Create(
        IReadOnlyList<HomeHistoryItem> all,
        string query,
        bool oversizedCurrentResult,
        Func<string, HomeModelName?> modelName,
        DateTime today,
        string? storageError = null)
    {
        var filtered = query.Length == 0
            ? all
            : all.Where(item => item.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        var todayCount = all.Count(item => item.CompletedAt.LocalDateTime.Date == today.Date);
        var groups = filtered
            .GroupBy(item => DateOnly.FromDateTime(item.CompletedAt.LocalDateTime))
            .Select(group => new HomeHistoryGroup(
                DayLabel.For(group.Key, DateOnly.FromDateTime(today)),
                group.Select(item => new HomeHistoryEntry(item,
                    item.ModelId.Length > 0 ? modelName(item.ModelId) : null)).ToList()))
            .ToList();
        return new HomeHistoryViewModel(groups, todayCount, all.Count > 0, filtered.Count > 0, oversizedCurrentResult,
            all.Select(item => item.OperationId).ToHashSet(), storageError);
    }
}
