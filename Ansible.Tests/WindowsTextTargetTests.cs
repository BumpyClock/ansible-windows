namespace Ansible.Tests;

public sealed class WindowsTextTargetTests
{
    [Fact]
    public void UsesFieldIdentityWhenBothProvidersExposeOne()
    {
        var target = new CapturedTextTarget(1, 2, [3, 4]);

        Assert.True(target.Matches(1, 2, [3, 4]));
        Assert.False(target.Matches(1, 2, [3, 5]));
    }

    [Fact]
    public void FallsBackToWindowAndFocusHandleWhenFieldIdentityIsUnavailable()
    {
        var target = new CapturedTextTarget(1, 2, null);

        Assert.True(target.Matches(1, 2, null));
        Assert.True(new CapturedTextTarget(1, 2, [3]).Matches(1, 2, null));
        Assert.False(target.Matches(5, 2, null));
        Assert.False(target.Matches(1, 6, null));
    }

    [Theory]
    [InlineData(unchecked((int)0x80040204))]
    [InlineData(unchecked((int)0x80040201))]
    [InlineData(unchecked((int)0x80131505))]
    [InlineData(unchecked((int)0x80010108))]
    public void TreatsUnavailableAccessibilityAsWindowOnlyFocus(int error) =>
        Assert.True(WindowsTextTarget.IsAccessibilityUnavailable(error));

    [Fact]
    public void DoesNotHideUnexpectedAccessErrorsAsMissingAccessibility() =>
        Assert.False(WindowsTextTarget.IsAccessibilityUnavailable(unchecked((int)0x80070005)));
}
