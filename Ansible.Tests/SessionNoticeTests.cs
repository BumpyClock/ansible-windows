using Ansible.Core;

namespace Ansible.Tests;

public sealed class SessionNoticeTests
{
    [Theory]
    [InlineData(NoticeKind.Information, false)]
    [InlineData(NoticeKind.Success, false)]
    [InlineData(NoticeKind.Warning, true)]
    [InlineData(NoticeKind.Error, true)]
    public void StatusVisibilityOnlyShowsFailures(NoticeKind kind, bool expected)
    {
        var notice = new SessionNotice(kind, "Outcome", "Details", NoticeArea.Application);

        Assert.Equal(expected, notice.ShouldDisplay);
        Assert.Equal(expected, notice.ShouldDisplayOn(NoticeArea.Dictionary));
    }

    [Theory]
    [InlineData(NoticeArea.Dictation)]
    [InlineData(NoticeArea.Settings)]
    [InlineData(NoticeArea.Models)]
    [InlineData(NoticeArea.Dictionary)]
    [InlineData(NoticeArea.Meetings)]
    public void FailureOnlyAppearsOnItsPage(NoticeArea area)
    {
        var notice = new SessionNotice(NoticeKind.Warning, "Could not save", "Try again.", area);

        foreach (var page in Pages)
            Assert.Equal(page == area, notice.ShouldDisplayOn(page));
    }

    [Fact]
    public void ModelFailureCanAppearOnDictationAndModelsWithoutLeakingToOtherPages()
    {
        var notice = new SessionNotice(NoticeKind.Error, "Model unavailable", "Choose a model.",
            NoticeArea.Dictation | NoticeArea.Models);

        Assert.True(notice.ShouldDisplayOn(NoticeArea.Dictation));
        Assert.True(notice.ShouldDisplayOn(NoticeArea.Models));
        Assert.False(notice.ShouldDisplayOn(NoticeArea.Settings));
        Assert.False(notice.ShouldDisplayOn(NoticeArea.Dictionary));
        Assert.False(notice.ShouldDisplayOn(NoticeArea.Meetings));
    }

    [Fact]
    public void PrivacyFailureIsVisibleAcrossTheAppUntilDismissed()
    {
        var notice = new SessionNotice(NoticeKind.Warning, "Capture privacy unavailable", "Review settings.",
            NoticeArea.Application);

        Assert.All(Pages, page => Assert.True(notice.ShouldDisplayOn(page)));
        var dismissed = notice with { IsDismissed = true };
        Assert.All(Pages, page => Assert.False(dismissed.ShouldDisplayOn(page)));
    }

    [Theory]
    [InlineData(NoticeKind.Information)]
    [InlineData(NoticeKind.Success)]
    [InlineData(NoticeKind.Warning)]
    [InlineData(NoticeKind.Error)]
    public void DismissedNoticesStayHiddenOnEveryPage(NoticeKind kind)
    {
        var notice = new SessionNotice(kind, "Outcome", "Details", NoticeArea.Application, IsDismissed: true);

        Assert.False(notice.ShouldDisplay);
        Assert.All(Pages, page => Assert.False(notice.ShouldDisplayOn(page)));
    }

    private static readonly NoticeArea[] Pages =
        [NoticeArea.Dictation, NoticeArea.Settings, NoticeArea.Models, NoticeArea.Dictionary, NoticeArea.Meetings];
}
