using System.Globalization;
using System.Text.Json.Serialization;

namespace Ansible.Core;

/// <summary>
/// A global activation chord: RegisterHotKey modifier bits (<see cref="Modifiers"/>: Alt=1, Ctrl=2,
/// Shift=4, Win=8) plus a primary virtual-key (<see cref="Key"/>). Ctrl+Win alone uses Key=0.
/// </summary>
public readonly record struct DictationShortcut(uint Modifiers, uint Key)
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ValidModifierMask = ModAlt | ModControl | ModShift | ModWin;
    private const uint NonShiftModifierMask = ModAlt | ModControl | ModWin;

    private const uint VkSpace = 0x20;
    private const uint VkDigit0 = 0x30, VkDigit9 = 0x39;
    private const uint VkA = 0x41, VkZ = 0x5A;
    private const uint VkL = 0x4C;
    private const uint VkF1 = 0x70, VkF11 = 0x7A;   // F12 (0x7B) is intentionally excluded.
    public static DictationShortcut Default => new(ModControl | ModWin, 0);

    [JsonIgnore]
    public bool IsModifierOnly => Modifiers == (ModControl | ModWin) && Key == 0;

    /// <summary>
    /// Validates chord structure and known exclusions, not whether RegisterHotKey succeeds.
    /// Printable keys (A-Z, 0-9, Space) require a non-Shift modifier (Ctrl/Alt/Win);
    /// F1-F11 are allowed bare. F12, Win+L, and unsupported modifier bits are rejected.
    /// Other Windows-key chords remain subject to operating-system registration restrictions.
    /// </summary>
    [JsonIgnore]
    public bool IsValid => IsModifierOnly || (
        (Modifiers & ~ValidModifierMask) == 0 &&
        IsSupportedPrimaryKey(Key) &&
        !(Modifiers == ModWin && Key == VkL) &&
        (!IsPrintableKey(Key) || (Modifiers & NonShiftModifierMask) != 0));

    /// <summary>Stable label with a fixed modifier order (Ctrl, Win, Shift, Alt), e.g. "Ctrl + Alt + D" or "F9".</summary>
    [JsonIgnore]
    public string DisplayText
    {
        get
        {
            var parts = new List<string>(5);
            if ((Modifiers & ModControl) != 0) { parts.Add("Ctrl"); }
            if ((Modifiers & ModWin) != 0) { parts.Add("Win"); }
            if ((Modifiers & ModShift) != 0) { parts.Add("Shift"); }
            if ((Modifiers & ModAlt) != 0) { parts.Add("Alt"); }
            if (!IsModifierOnly) { parts.Add(KeyName(Key)); }
            return string.Join(" + ", parts);
        }
    }

    private static bool IsPrintableKey(uint vk) =>
        (vk >= VkDigit0 && vk <= VkDigit9) || (vk >= VkA && vk <= VkZ) || vk == VkSpace;

    private static bool IsSupportedPrimaryKey(uint vk) =>
        IsPrintableKey(vk) || (vk >= VkF1 && vk <= VkF11);

    private static string KeyName(uint vk)
    {
        if ((vk >= VkDigit0 && vk <= VkDigit9) || (vk >= VkA && vk <= VkZ)) { return ((char)vk).ToString(); }
        if (vk >= VkF1 && vk <= VkF11) { return "F" + ((int)(vk - VkF1 + 1)).ToString(CultureInfo.InvariantCulture); }
        if (vk == VkSpace) { return "Space"; }
        return "0x" + vk.ToString("X2", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Qualifies the modifier-only chord before starting, so ordinary Ctrl+Win+key shortcuts pass through.
/// A third key arriving after qualification ends the hold; future input cannot be predicted.
/// </summary>
public sealed class CtrlWinShortcutState
{
    public const int QualificationMilliseconds = 200;
    private readonly HashSet<uint> _down = [];
    private long? _candidateAt;
    private bool _blocked;

    public bool IsPressed { get; private set; }
    public bool IsPending => _candidateAt is not null;

    public void Reset(IEnumerable<uint>? heldKeys = null)
    {
        _down.Clear();
        if (heldKeys is not null) _down.UnionWith(heldKeys);
        _blocked = _down.Count != 0;
        _candidateAt = null;
        IsPressed = false;
    }

    /// <returns>Always false. Every observed physical keydown and keyup must reach Windows.</returns>
    public bool Process(uint key, bool down, bool injected, long milliseconds)
    {
        if (injected) return false;
        if (down ? !_down.Add(key) : !_down.Remove(key)) return false;

        if (down && !IsChordKey(key))
        {
            _blocked = true;
        }

        var held = _down.Any(IsControl) && _down.Any(IsWindows);
        if (!held || _blocked)
        {
            _candidateAt = null;
            IsPressed = false;
        }
        else if (!IsPressed && _candidateAt is null)
        {
            _candidateAt = milliseconds;
        }

        if (_down.Count == 0)
        {
            _blocked = false;
        }
        return false;
    }

    public bool Advance(long milliseconds)
    {
        if (_candidateAt is not { } start || milliseconds - start < QualificationMilliseconds)
            return false;
        _candidateAt = null;
        IsPressed = true;
        return true;
    }

    public static bool IsControl(uint key) => key is 0x11 or 0xA2 or 0xA3;
    public static bool IsWindows(uint key) => key is 0x5B or 0x5C;
    private static bool IsChordKey(uint key) => IsControl(key) || IsWindows(key);
}
