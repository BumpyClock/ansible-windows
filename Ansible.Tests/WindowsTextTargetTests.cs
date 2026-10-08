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

    [Theory]
    [InlineData(1u, false, false, nameof(WindowsTextTarget.ClipboardFormatKind.Memory))]          // CF_TEXT
    [InlineData(13u, false, false, nameof(WindowsTextTarget.ClipboardFormatKind.Memory))]         // CF_UNICODETEXT
    [InlineData(15u, false, false, nameof(WindowsTextTarget.ClipboardFormatKind.Memory))]         // CF_HDROP
    [InlineData(8u, true, false, nameof(WindowsTextTarget.ClipboardFormatKind.Memory))]           // CF_DIB
    [InlineData(0xC123u, false, false, nameof(WindowsTextTarget.ClipboardFormatKind.Memory))]     // registered format
    [InlineData(2u, true, false, nameof(WindowsTextTarget.ClipboardFormatKind.Synthesized))]      // CF_BITMAP from a DIB
    [InlineData(9u, true, false, nameof(WindowsTextTarget.ClipboardFormatKind.Synthesized))]      // CF_PALETTE from a DIB
    [InlineData(3u, false, true, nameof(WindowsTextTarget.ClipboardFormatKind.Synthesized))]      // CF_METAFILEPICT
    [InlineData(14u, false, true, nameof(WindowsTextTarget.ClipboardFormatKind.EnhancedMetafile))]
    [InlineData(2u, false, false, nameof(WindowsTextTarget.ClipboardFormatKind.Uncopyable))]      // bitmap without a DIB
    [InlineData(0x80u, false, false, nameof(WindowsTextTarget.ClipboardFormatKind.Uncopyable))]   // CF_OWNERDISPLAY
    [InlineData(0x82u, false, false, nameof(WindowsTextTarget.ClipboardFormatKind.Uncopyable))]   // CF_DSPBITMAP
    [InlineData(0x200u, false, false, nameof(WindowsTextTarget.ClipboardFormatKind.Uncopyable))]  // CF_PRIVATEFIRST
    [InlineData(0x3FFu, false, false, nameof(WindowsTextTarget.ClipboardFormatKind.Uncopyable))]  // CF_GDIOBJLAST
    public void ClassifiesClipboardFormatsForBackup(
        uint format, bool hasDib, bool hasEnhancedMetafile, string expected) =>
        Assert.Equal(expected, WindowsTextTarget.ClassifyClipboardFormat(format, hasDib, hasEnhancedMetafile).ToString());

    [Fact]
    public void DoesNotHideUnexpectedAccessErrorsAsMissingAccessibility() =>
        Assert.False(WindowsTextTarget.IsAccessibilityUnavailable(unchecked((int)0x80070005)));
}
