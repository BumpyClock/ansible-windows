using Ansible.Core;

namespace Ansible;

/// <summary>
/// Session-only history used by the home surface. The app deliberately does not persist transcript text.
/// </summary>
internal sealed record HomeHistoryItem(
    Guid OperationId,
    DateTimeOffset CompletedAt,
    string Text,
    string ModelId,
    int? Words,
    TimeSpan Elapsed);

internal static class HomeDictationHistory
{
    private const int MaximumItems = 100;
    private static readonly object Gate = new();
    private static readonly List<HomeHistoryItem> Items = [];
    private static long _version;
    internal const int MaximumCharacters = 500_000;

    public static long Version { get { lock (Gate) { return _version; } } }

    public static IReadOnlyList<HomeHistoryItem> Snapshot()
    {
        lock (Gate) { return Items.ToArray(); }
    }

    public static bool Add(HomeHistoryItem item)
    {
        lock (Gate)
        {
            if (Items.Any(existing => existing.OperationId == item.OperationId)) { return false; }
            // Keep copy actions authoritative. Oversized transcripts stay in the current result and are omitted here.
            if (item.Text.Length > MaximumCharacters) { return false; }
            Items.Insert(0, item);
            while (Items.Count > MaximumItems || Items.Sum(existing => existing.Text.Length) > MaximumCharacters)
                Items.RemoveAt(Items.Count - 1);
            _version++;
            return true;
        }
    }

    internal static void Reset()
    {
        lock (Gate)
        {
            Items.Clear();
            _version++;
        }
    }
}
