using Ansible.Core;

namespace Ansible.Tests;

public sealed class AppNoticesTests
{
    private static NoticeContent Content(NoticeSeverity severity, string title = "Title") => new(severity, title);

    private static string[] Keys(NoticeStack stack) => stack.Shown.Select(notice => notice.Key).ToArray();

    [Fact]
    public void ArrangeShowsOnlyNoticesForTheArea()
    {
        var notices = new AppNotices();
        notices.Set("dictation", Content(NoticeSeverity.Warning), NoticeArea.Dictation);
        notices.Set("settings", Content(NoticeSeverity.Warning), NoticeArea.Settings);
        notices.Set("both", Content(NoticeSeverity.Warning), NoticeArea.Settings | NoticeArea.Dictation);
        notices.Set("everywhere", Content(NoticeSeverity.Warning));

        Assert.Equal(["dictation", "both", "everywhere"], Keys(notices.Arrange(NoticeArea.Dictation, expanded: true)));
        Assert.Equal(["settings", "both", "everywhere"], Keys(notices.Arrange(NoticeArea.Settings, expanded: true)));
        Assert.Equal(["everywhere"], Keys(notices.Arrange(NoticeArea.Models, expanded: true)));
    }

    [Fact]
    public void ArrangeWithoutAnAreaShowsEveryNotice()
    {
        var notices = new AppNotices();
        notices.Set("dictation", Content(NoticeSeverity.Warning), NoticeArea.Dictation);
        notices.Set("meetings", Content(NoticeSeverity.Warning), NoticeArea.Meetings);

        Assert.Equal(["dictation", "meetings"], Keys(notices.Arrange(null, expanded: false)));
    }

    [Fact]
    public void ArrangeOrdersBySeverityThenPostingOrder()
    {
        var notices = new AppNotices();
        notices.Set("info-1", Content(NoticeSeverity.Information));
        notices.Set("warning-1", Content(NoticeSeverity.Warning));
        notices.Set("error-1", Content(NoticeSeverity.Error));
        notices.Set("info-2", Content(NoticeSeverity.Information));
        notices.Set("warning-2", Content(NoticeSeverity.Warning));
        notices.Set("error-2", Content(NoticeSeverity.Error));

        Assert.Equal(["error-1", "error-2", "warning-1", "warning-2", "info-1", "info-2"],
            Keys(notices.Arrange(null, expanded: true)));
    }

    [Fact]
    public void ReplacingANoticeKeepsItsPlaceWithinItsSeverity()
    {
        var notices = new AppNotices();
        notices.Set("first", Content(NoticeSeverity.Warning, "Old"));
        notices.Set("second", Content(NoticeSeverity.Warning));
        notices.Set("first", Content(NoticeSeverity.Warning, "New"));

        var shown = notices.Arrange(null, expanded: true).Shown;
        Assert.Equal(["first", "second"], shown.Select(notice => notice.Key));
        Assert.Equal("New", shown[0].Content.Title);
    }

    [Fact]
    public void CollapsedArrangeShowsTheMostSevereThreeAndCountsTheRest()
    {
        var notices = new AppNotices();
        notices.Set("info", Content(NoticeSeverity.Information));
        notices.Set("warning-1", Content(NoticeSeverity.Warning));
        notices.Set("warning-2", Content(NoticeSeverity.Warning));
        notices.Set("error", Content(NoticeSeverity.Error));
        notices.Set("warning-3", Content(NoticeSeverity.Warning));

        var collapsed = notices.Arrange(null, expanded: false);
        Assert.Equal(["error", "warning-1", "warning-2"], Keys(collapsed));
        Assert.Equal(2, collapsed.Hidden);

        var expanded = notices.Arrange(null, expanded: true);
        Assert.Equal(5, expanded.Shown.Count);
        Assert.Equal(0, expanded.Hidden);
    }

    [Fact]
    public void HiddenCountsOnlyNoticesForTheArea()
    {
        var notices = new AppNotices();
        for (var index = 0; index < 4; index++)
            notices.Set($"dictation-{index}", Content(NoticeSeverity.Warning), NoticeArea.Dictation);
        notices.Set("meetings", Content(NoticeSeverity.Warning), NoticeArea.Meetings);

        Assert.Equal(1, notices.Arrange(NoticeArea.Dictation, expanded: false).Hidden);
        Assert.Equal(0, notices.Arrange(NoticeArea.Meetings, expanded: false).Hidden);
    }

    [Fact]
    public void SetRaisesChangedOnEveryPostSoHostsHoldTheNewestDelegates()
    {
        var notices = new AppNotices();
        var changes = 0;
        notices.Changed += () => changes++;
        var first = new NoticeContent(NoticeSeverity.Warning, "Title", "Message", [new("Retry", () => { })]);
        var second = new NoticeContent(NoticeSeverity.Warning, "Title", "Message", [new("Retry", () => { })]);
        Assert.True(second.LooksLike(first));

        notices.Set("source", first);
        notices.Set("source", second);

        Assert.Equal(2, changes);
        Assert.Same(second, Assert.Single(notices.Current).Content);
    }

    [Fact]
    public void SetWithoutContentRemovesTheNoticeWithoutRunningDismissed()
    {
        var notices = new AppNotices();
        var dismissed = false;
        notices.Set("source", Content(NoticeSeverity.Warning), dismissed: () => dismissed = true);
        var changes = 0;
        notices.Changed += () => changes++;

        notices.Set("source", null);
        notices.Set("source", null);

        Assert.Empty(notices.Current);
        Assert.False(dismissed);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void DismissRemovesTheNoticeAndRunsTheNewestDismissedCallback()
    {
        var notices = new AppNotices();
        var calls = new List<string>();
        notices.Set("source", Content(NoticeSeverity.Warning), dismissed: () => calls.Add("first"));
        notices.Set("source", Content(NoticeSeverity.Warning), dismissed: () => calls.Add("newest"));
        var currentWhenDismissed = -1;
        notices.Changed += () => currentWhenDismissed = notices.Current.Count;

        notices.Dismiss("source");
        notices.Dismiss("source");

        Assert.Equal(["newest"], calls);
        Assert.Equal(0, currentWhenDismissed);
        Assert.Empty(notices.Current);
    }

    [Fact]
    public void SetErrorReplacesTheSourcesPreviousNotice()
    {
        var notices = new AppNotices();
        notices.SetError("source", new InvalidOperationException("First failure."), NoticeArea.Meetings);
        notices.SetError("source", new InvalidOperationException("Second failure."), NoticeArea.Meetings);

        var notice = Assert.Single(notices.Current);
        Assert.Equal(NoticeSeverity.Error, notice.Content.Severity);
        Assert.Equal("Operation failed", notice.Content.Title);
        Assert.Equal("Second failure.", notice.Content.Message);
        Assert.Equal(NoticeArea.Meetings, notice.Area);
    }

    [Fact]
    public void SetErrorDefaultsToTheDictationArea()
    {
        var notices = new AppNotices();
        notices.SetError("source", new InvalidOperationException("Failure."));

        Assert.Equal(NoticeArea.Dictation, Assert.Single(notices.Current).Area);
    }

    [Fact]
    public void ConfirmReplacesTheCurrentConfirmation()
    {
        var notices = new AppNotices();
        notices.Confirm("Copied");
        var first = notices.Confirmation;
        notices.Confirm("Removed", new NoticeAction("Undo", () => { }));

        Assert.NotSame(first, notices.Confirmation);
        Assert.Equal("Removed", notices.Confirmation!.Message);
        Assert.Equal(TimeSpan.FromSeconds(5), notices.Confirmation.Duration);
        Assert.Equal(TimeSpan.FromSeconds(2.4), first!.Duration);
    }

    [Fact]
    public void EndConfirmationIgnoresAReplacedConfirmation()
    {
        var notices = new AppNotices();
        notices.Confirm("Copied");
        var replaced = notices.Confirmation!;
        notices.Confirm("Copied again");
        var current = notices.Confirmation!;
        var changes = 0;
        notices.Changed += () => changes++;

        notices.EndConfirmation(replaced);
        Assert.Same(current, notices.Confirmation);
        Assert.Equal(0, changes);

        notices.EndConfirmation(current);
        Assert.Null(notices.Confirmation);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ConfirmationsAreNotArrangedWithPersistentNotices()
    {
        var notices = new AppNotices();
        notices.Confirm("Copied");

        Assert.Empty(notices.Arrange(null, expanded: true).Shown);
    }
}
