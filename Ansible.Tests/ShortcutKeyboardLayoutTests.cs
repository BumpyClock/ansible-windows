using Ansible.Core;

namespace Ansible;

public sealed class ShortcutKeyboardLayoutTests
{
    private const uint Alt = 1, Ctrl = 2, Shift = 4, Win = 8;
    private const uint KeyD = 0x44, Key5 = 0x35, F9 = 0x78;

    [Fact]
    public void EveryValidShortcutHighlightsEachKeyItNames()
    {
        var shortcuts = ValidShortcuts();
        Assert.True(shortcuts.Count > 600, $"Only {shortcuts.Count} valid shortcuts were enumerated.");

        var failures = new List<string>();
        foreach (var shortcut in shortcuts)
        {
            var expected = shortcut.DisplayText.Split(" + ").ToHashSet(StringComparer.Ordinal);
            var highlighted = ShortcutKeyboardLayout.Create(shortcut)
                .Where(key => key.Highlighted).Select(key => key.Label).ToHashSet(StringComparer.Ordinal);
            if (!highlighted.SetEquals(expected))
                failures.Add($"{shortcut.DisplayText} highlights [{string.Join(", ", highlighted)}]");
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void KeysStayInsideTheKeyboardCardAndNeverOverlap()
    {
        var failures = new List<string>();
        foreach (var shortcut in ValidShortcuts())
        {
            var keys = ShortcutKeyboardLayout.Create(shortcut);
            foreach (var key in keys)
            {
                if (key.X < 0 || key.Y < 0 ||
                    key.X + key.Width > ShortcutKeyboardLayout.AreaWidth ||
                    key.Y + ShortcutKeyboardLayout.KeyHeight > ShortcutKeyboardLayout.AreaHeight)
                    failures.Add($"{shortcut.DisplayText}: {key.Label} leaves the card");
            }

            for (var first = 0; first < keys.Count; first++)
            {
                for (var second = first + 1; second < keys.Count; second++)
                {
                    if (Overlaps(keys[first], keys[second]))
                        failures.Add($"{shortcut.DisplayText}: {keys[first].Label} overlaps {keys[second].Label}");
                }
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void ShiftHasItsOwnKeyAndIsHighlightedWithTheShortcut()
    {
        var keys = ShortcutKeyboardLayout.Create(new DictationShortcut(Ctrl | Shift, KeyD));
        string[] expected = ["Ctrl", "D", "Shift"];

        Assert.Equal(expected, Highlighted(keys));
        Assert.Single(keys, key => key.Label == "Shift");
    }

    [Fact]
    public void AFunctionKeyShortcutKeepsTheFunctionRow()
    {
        var keys = ShortcutKeyboardLayout.Create(new DictationShortcut(Shift, F9));
        string[] expected = ["F9", "Shift"];

        Assert.Equal(expected, Highlighted(keys));
        Assert.Equal(FunctionRow, TopRow(keys));
    }

    [Fact]
    public void ADigitShortcutShowsTheDigitRowInPlaceOfTheFunctionRow()
    {
        var keys = ShortcutKeyboardLayout.Create(new DictationShortcut(Ctrl | Shift, Key5));
        string[] expected = ["5", "Ctrl", "Shift"];
        string[] digits = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"];

        Assert.Equal(expected, Highlighted(keys));
        Assert.Equal(digits, TopRow(keys));
    }

    [Theory]
    [InlineData(Ctrl | Win, 0u)]
    [InlineData(Ctrl, KeyD)]
    [InlineData(Alt | Win, 0x20u)]
    public void ShortcutsWithoutADigitKeepTheFunctionRow(uint modifiers, uint key)
    {
        var keys = ShortcutKeyboardLayout.Create(new DictationShortcut(modifiers, key));

        Assert.Equal(FunctionRow, TopRow(keys));
    }

    private static readonly string[] FunctionRow =
        ["F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11"];

    private static string[] TopRow(IReadOnlyList<ShortcutKeyPlacement> keys) =>
        keys.Where(key => key.Y == 0).Select(key => key.Label).ToArray();

    private static string[] Highlighted(IReadOnlyList<ShortcutKeyPlacement> keys) =>
        keys.Where(key => key.Highlighted).Select(key => key.Label).Order(StringComparer.Ordinal).ToArray();

    private static bool Overlaps(ShortcutKeyPlacement first, ShortcutKeyPlacement second) =>
        first.X < second.X + second.Width && second.X < first.X + first.Width &&
        first.Y < second.Y + ShortcutKeyboardLayout.KeyHeight && second.Y < first.Y + ShortcutKeyboardLayout.KeyHeight;

    private static List<DictationShortcut> ValidShortcuts()
    {
        uint[] keys =
        [
            0, 0x20,
            .. Enumerable.Range(0x30, 10).Select(key => (uint)key),
            .. Enumerable.Range(0x41, 26).Select(key => (uint)key),
            .. Enumerable.Range(0x70, 11).Select(key => (uint)key)
        ];
        return
        [
            .. from modifiers in Enumerable.Range(0, 16)
               from key in keys
               select new DictationShortcut((uint)modifiers, key) into shortcut
               where shortcut.IsValid
               select shortcut
        ];
    }
}
