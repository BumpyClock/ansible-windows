using DictationPoc.Core;

namespace DictationPoc.Tests;

public sealed class DictationShortcutTests
{
    [Fact]
    public void DefaultAllowsTypingWithoutHoldingModifiers()
    {
        Assert.Equal(new DictationShortcut(0, 0x78), DictationShortcut.Default);
        Assert.Equal("F9", DictationShortcut.Default.DisplayText);
        Assert.True(DictationShortcut.Default.IsValid);
    }

    [Theory]
    [InlineData(0, 0x41)]
    [InlineData(4, 0x41)]
    [InlineData(0, 0x31)]
    [InlineData(0, 0x20)]
    [InlineData(4, 0x20)]
    [InlineData(0, 0x7B)]
    [InlineData(0x4000, 0x78)]
    public void CannotConsumeOrdinaryTypingOrReservedKeys(uint modifiers, uint key) =>
        Assert.False(new DictationShortcut(modifiers, key).IsValid);

    [Theory]
    [InlineData(0, 0x70)]
    [InlineData(0, 0x7A)]
    [InlineData(2, 0x41)]
    [InlineData(1, 0x20)]
    [InlineData(8, 0x31)]
    [InlineData(6, 0x44)]
    public void AcceptsFunctionKeysAndModifiedPrintableKeys(uint modifiers, uint key) =>
        Assert.True(new DictationShortcut(modifiers, key).IsValid);

    [Fact]
    public void DisplaysCapturedShortcutClearly() =>
        Assert.Equal("Ctrl + Shift + D", new DictationShortcut(6, 0x44).DisplayText);
}
