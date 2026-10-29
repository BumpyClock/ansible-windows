using Ansible.Core;
using System.Text.Json;

namespace Ansible.Tests;

public sealed class DictationShortcutTests
{
    [Fact]
    public void DefaultIsControlWindowsWithoutAPrimaryKey()
    {
        Assert.Equal(new DictationShortcut(10, 0), DictationShortcut.Default);
        Assert.Equal("Ctrl + Win", DictationShortcut.Default.DisplayText);
        Assert.True(DictationShortcut.Default.IsModifierOnly);
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
    [InlineData(8, 0x4C)]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(8, 0)]
    [InlineData(14, 0)]
    [InlineData(11, 0)]
    [InlineData(10, 0x11)]
    [InlineData(2, 0x5B)]
    public void CannotConsumeOrdinaryTypingOrReservedKeys(uint modifiers, uint key) =>
        Assert.False(new DictationShortcut(modifiers, key).IsValid);

    [Theory]
    [InlineData(0, 0x70)]
    [InlineData(0, 0x78)]
    [InlineData(0, 0x7A)]
    [InlineData(2, 0x41)]
    [InlineData(1, 0x20)]
    [InlineData(8, 0x31)]
    [InlineData(9, 0x44)]
    [InlineData(9, 0x4C)]
    [InlineData(2, 0x4C)]
    [InlineData(6, 0x44)]
    public void AcceptsFunctionKeysAndModifiedPrintableKeys(uint modifiers, uint key) =>
        Assert.True(new DictationShortcut(modifiers, key).IsValid);

    [Fact]
    public void DisplaysCapturedShortcutClearly() =>
        Assert.Equal("Ctrl + Shift + D", new DictationShortcut(6, 0x44).DisplayText);

    [Fact]
    public void ExplicitF9RemainsARegularShortcut()
    {
        var shortcut = new DictationShortcut(0, 0x78);
        Assert.True(shortcut.IsValid);
        Assert.False(shortcut.IsModifierOnly);
        Assert.Equal("F9", shortcut.DisplayText);
    }

    [Theory]
    [InlineData(0u, 0x78u)]
    [InlineData(10u, 0u)]
    public void ExplicitSavedShortcutsRoundTripWithoutDerivedProperties(uint modifiers, uint key)
    {
        var shortcut = new DictationShortcut(modifiers, key);
        var json = JsonSerializer.Serialize(new AppSettings { Shortcut = shortcut }, AppSettingsJsonContext.Default.AppSettings);
        var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;
        Assert.Equal(shortcut, settings.Shortcut);
        using var document = JsonDocument.Parse(json);
        var saved = document.RootElement.GetProperty("Shortcut");
        Assert.Equal(modifiers, saved.GetProperty("Modifiers").GetUInt32());
        Assert.Equal(key, saved.GetProperty("Key").GetUInt32());
        Assert.False(saved.TryGetProperty("IsModifierOnly", out _));
    }
}
