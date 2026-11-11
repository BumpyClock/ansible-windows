using Ansible.Core;
using System.Globalization;

namespace Ansible;

/// <summary>
/// How a row tag reads. Inserted uses the accent. Unverified is quiet secondary text. NotInserted is amber,
/// and its row keeps Copy visible because the text did not fully reach the app.
/// </summary>
internal enum HomeHistoryTagTone { Inserted, Unverified, NotInserted }

/// <summary>A row tag for a real insertion outcome.</summary>
internal sealed record HomeHistoryTag(string Label, string Glyph, HomeHistoryTagTone Tone, string Meaning)
{
    public bool NeedsCopy => Tone == HomeHistoryTagTone.NotInserted;
}

internal sealed record HomeHistoryEntry(HomeHistoryItem Item, HomeModelName? Model)
{
    // Only real insertion outcomes get a tag. In-app dictations, deferred, and pending deliveries have none.
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

/// <summary>What History says when it shows no rows.</summary>
internal sealed record HomeHistoryEmptyState(HomeHistoryEmptyKind Kind, string Title, string Detail);

internal static class HomeHistoryProjection
{
    /// <summary>
    /// Null while rows match. Usage counts survive removed and unsaved history rows, so "No dictations yet" needs
    /// usage that is known and holds no dictation. When usage counts dictations that History does not hold, the
    /// empty state says why beside those lifetime stats: they were removed or never saved. A storage failure may
    /// hide rows that are still on disk, so it gets the wording that is true either way, as does unknown usage.
    /// </summary>
    internal static HomeHistoryEmptyState? EmptyState(HomeHistoryViewModel model, string query, int? dictations) =>
        model.HasMatches ? null
        : model.HasHistory ? new(HomeHistoryEmptyKind.NoMatches, "No matches", $"No saved transcript contains \"{query}\".")
        : model.StorageError is not null ? new(HomeHistoryEmptyKind.NothingSaved, "No saved transcripts",
            "History storage has a problem, so saved transcripts may be missing.")
        : dictations == 0 ? new(HomeHistoryEmptyKind.FirstRun, "No dictations yet", "Your transcripts appear here after you dictate.")
        : dictations is { } count ? new(HomeHistoryEmptyKind.NothingSaved, "No saved transcripts", count == 1
            ? "Your earlier dictation was removed or not saved."
            : $"{count:N0} earlier dictations were removed or not saved.")
        : new(HomeHistoryEmptyKind.NothingSaved, "No saved transcripts", "New dictations appear here.");

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
