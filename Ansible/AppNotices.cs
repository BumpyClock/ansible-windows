using Ansible.Core;

namespace Ansible;

public enum AppNoticeSeverity { Information, Warning, Error, Transient }

public sealed record AppNoticeAction(string Label, Action Invoke, bool DismissesNotice = false);

/// <summary>
/// One notification for the window's NoticeHost. Key identifies the source: showing the same key replaces it.
/// Area limits the pages that show it; Application shows on every page.
/// Transient notices are short confirmations that close on their own after AutoDismissAfter.
/// </summary>
public sealed record AppNotice(
    string Key,
    AppNoticeSeverity Severity,
    string Title,
    string Message = "",
    NoticeArea Area = NoticeArea.Application,
    bool IsClosable = true,
    IReadOnlyList<AppNoticeAction>? Actions = null,
    TimeSpan? AutoDismissAfter = null,
    Action? Dismissed = null)
{
    public static AppNotice Confirmation(string message, AppNoticeAction? undo = null) => new(
        "transient", AppNoticeSeverity.Transient, message,
        Actions: undo is null ? null : [undo with { DismissesNotice = true }],
        AutoDismissAfter: TimeSpan.FromSeconds(undo is null ? 2.4 : 5));
}

/// <summary>
/// Window-owned notification list. Producers post keyed notices; NoticeHost renders them as floating NoticeBars.
/// UI-thread only: callers on other threads dispatch first.
/// </summary>
public sealed class AppNotices
{
    private readonly List<AppNotice> _current = [];

    public event Action? Changed;

    public IReadOnlyList<AppNotice> Current => _current;

    public void Show(AppNotice notice)
    {
        var index = _current.FindIndex(existing => existing.Key == notice.Key);
        if (index >= 0) _current[index] = notice;
        else _current.Add(notice);
        Changed?.Invoke();
    }

    /// <summary>Removes a notice because its condition ended. Does not run Dismissed.</summary>
    public void Clear(string key)
    {
        if (_current.RemoveAll(notice => notice.Key == key) > 0) Changed?.Invoke();
    }

    /// <summary>Removes a notice because the user closed it or it timed out, then runs Dismissed.</summary>
    public void Dismiss(AppNotice notice)
    {
        if (!_current.Remove(notice)) return;
        Changed?.Invoke();
        notice.Dismissed?.Invoke();
    }
}
