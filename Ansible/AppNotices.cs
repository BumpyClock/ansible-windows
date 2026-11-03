using Ansible.Core;

namespace Ansible;

public enum NoticeSeverity { Information, Warning, Error }

public sealed record NoticeAction(string Label, Action Invoke);

/// <summary>What one NoticeBar shows.</summary>
public sealed record NoticeContent(
    NoticeSeverity Severity,
    string Title,
    string Message = "",
    IReadOnlyList<NoticeAction>? Actions = null,
    bool IsClosable = true)
{
    /// <summary>
    /// True when a reader would see no difference: same severity, text, close button and action labels.
    /// Action delegates are new closures on every post, so they are not compared.
    /// </summary>
    public bool LooksLike(NoticeContent? other) =>
        other is not null && Severity == other.Severity && Title == other.Title && Message == other.Message &&
        IsClosable == other.IsClosable &&
        (Actions ?? []).Select(action => action.Label).SequenceEqual((other.Actions ?? []).Select(action => action.Label));
}

/// <summary>
/// One persistent notice in the main window. Key identifies the source. Area limits the pages that show it;
/// Application shows on every page. Dismissed runs when the user closes it, not when its source clears it.
/// </summary>
public sealed record AppNotice(string Key, NoticeContent Content, NoticeArea Area, Action? Dismissed);

/// <summary>A short confirmation that closes on its own. Choosing Undo closes it too.</summary>
public sealed record NoticeConfirmation(string Message, NoticeAction? Undo = null)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(Undo is null ? 2.4 : 5);
}

/// <summary>
/// Window-owned notices. Sources declare their current notice with Set, as often as their state changes:
/// posting what is already shown is cheap because NoticeBar keeps the visual and the announcement.
/// UI-thread only: callers on other threads dispatch first.
/// </summary>
public sealed class AppNotices
{
    private readonly List<AppNotice> _current = [];

    public event Action? Changed;

    public IReadOnlyList<AppNotice> Current => _current;

    /// <summary>The one confirmation showing now. A new confirmation replaces it.</summary>
    public NoticeConfirmation? Confirmation { get; private set; }

    /// <summary>
    /// Declares the notice for a source. Null content removes it without running Dismissed. The newest post keeps
    /// its actions and Dismissed callback, so callbacks can capture the state they were posted for.
    /// </summary>
    public void Set(string key, NoticeContent? content, NoticeArea area = NoticeArea.Application, Action? dismissed = null)
    {
        var index = _current.FindIndex(notice => notice.Key == key);
        if (content is null)
        {
            if (index < 0) return;
            _current.RemoveAt(index);
        }
        else if (index < 0) _current.Add(new AppNotice(key, content, area, dismissed));
        else _current[index] = new AppNotice(key, content, area, dismissed);
        Changed?.Invoke();
    }

    /// <summary>The user closed the notice: removes it, then runs its Dismissed callback.</summary>
    public void Dismiss(string key)
    {
        var index = _current.FindIndex(notice => notice.Key == key);
        if (index < 0) return;
        var notice = _current[index];
        _current.RemoveAt(index);
        Changed?.Invoke();
        notice.Dismissed?.Invoke();
    }

    public void Confirm(string message, NoticeAction? undo = null)
    {
        Confirmation = new NoticeConfirmation(message, undo);
        Changed?.Invoke();
    }

    /// <summary>Closes a confirmation that timed out or was closed. Ignores one that was already replaced.</summary>
    public void EndConfirmation(NoticeConfirmation confirmation)
    {
        if (!ReferenceEquals(Confirmation, confirmation)) return;
        Confirmation = null;
        Changed?.Invoke();
    }
}
