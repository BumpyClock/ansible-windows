using Ansible.Core;

namespace Ansible;

internal readonly record struct ShortcutKeyPlacement(string Label, double X, double Y, double Width, bool Highlighted);

/// <summary>
/// Places the keys of the welcome-page keyboard illustration. The keyboard card is sized for five rows,
/// so the top row shows the digit keys instead of F1-F11 only when the shortcut's primary key is a digit.
/// Every key a valid shortcut can name has a place, because the illustration highlights keys by label.
/// </summary>
internal static class ShortcutKeyboardLayout
{
    // The 500 x 216 card in ShortcutIllustration.xaml, less the Keys canvas offset of 12 and 10 on each side.
    public const double AreaWidth = 476;
    public const double AreaHeight = 196;
    public const double KeyHeight = 34;

    private const double RowPitch = 40;
    private const double Gap = 5;
    private const double FunctionKeyWidth = 38;
    private const double KeyWidth = 43;
    private const double ModifierWidth = 65;
    private const double SpaceWidth = 190;

    private static readonly string[] FunctionKeys = ["F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11"];
    private static readonly string[] DigitKeys = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"];
    private static readonly string[] UpperLetters = ["Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P"];
    private static readonly string[] HomeLetters = ["A", "S", "D", "F", "G", "H", "J", "K", "L"];
    private static readonly string[] LowerKeys = ["Shift", "Z", "X", "C", "V", "B", "N", "M"];
    private static readonly string[] BottomKeys = ["Ctrl", "Win", "Alt", "Space", "Alt"];

    public static IReadOnlyList<ShortcutKeyPlacement> Create(DictationShortcut shortcut)
    {
        var selected = shortcut.DisplayText.Split(" + ").ToHashSet(StringComparer.Ordinal);
        var digits = DigitKeys.Any(selected.Contains);
        var keys = new List<ShortcutKeyPlacement>(48);
        AddRow(keys, selected, 0, 0, digits ? DigitKeys : FunctionKeys, _ => digits ? KeyWidth : FunctionKeyWidth);
        AddRow(keys, selected, 1, 0, UpperLetters, _ => KeyWidth);
        AddRow(keys, selected, 2, 12, HomeLetters, _ => KeyWidth);
        AddRow(keys, selected, 3, 0, LowerKeys, label => label == "Shift" ? ModifierWidth : KeyWidth);
        AddRow(keys, selected, 4, 0, BottomKeys, label => label == "Space" ? SpaceWidth : ModifierWidth);
        return keys;
    }

    private static void AddRow(
        List<ShortcutKeyPlacement> keys, HashSet<string> selected, int row, double x,
        string[] labels, Func<string, double> widthOf)
    {
        foreach (var label in labels)
        {
            var width = widthOf(label);
            keys.Add(new ShortcutKeyPlacement(label, x, row * RowPitch, width, selected.Contains(label)));
            x += width + Gap;
        }
    }
}
