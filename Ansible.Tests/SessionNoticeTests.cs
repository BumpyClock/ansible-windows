using Ansible.Core;

namespace Ansible.Tests;

public sealed class SessionNoticeTests
{
    [Theory]
    [InlineData(NoticeKind.Information, false, false)]
    [InlineData(NoticeKind.Success, false, false)]
    [InlineData(NoticeKind.Warning, false, true)]
    [InlineData(NoticeKind.Error, false, true)]
    [InlineData(NoticeKind.Information, true, true)]
    [InlineData(NoticeKind.Success, true, true)]
    [InlineData(NoticeKind.Warning, true, true)]
    [InlineData(NoticeKind.Error, true, true)]
    public void StatusVisibilityPreservesWarningsAndExplicitDeliveryOutcomes(
        NoticeKind kind, bool showInStatus, bool expected)
    {
        var notice = new SessionNotice(kind, "Outcome", "Details", showInStatus);

        Assert.Equal(expected, notice.ShouldDisplay);
    }
}
