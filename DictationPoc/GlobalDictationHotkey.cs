using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DictationPoc;

// System-wide hotkey adapter. Construct/Change/Dispose on the UI thread that owns
// hwnd; WM_HOTKEY is posted to that window so the callback runs on the UI thread.
// The window is never focused when the chord fires. Registration failures throw.

/// <summary>Stable, persistable identifiers for the supported modifier+letter chords.</summary>
internal enum HotkeyChoice
{
    CtrlAltD = 1,
    CtrlShiftD = 2,
    WinAltD = 3,
    CtrlAltJ = 4,
}

/// <summary>Selectable chord for settings UI: <see cref="DisplayText"/> labels it, <see cref="Value"/> persists it.</summary>
internal sealed class HotkeyOption
{
    internal HotkeyOption(HotkeyChoice choice, string displayText)
    {
        Choice = choice;
        DisplayText = displayText;
    }

    public HotkeyChoice Choice { get; }
    public int Value => (int)Choice;
    public string DisplayText { get; }
}

internal sealed partial class GlobalDictationHotkey : IDisposable
{
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_D = 0x44;
    private const uint VK_J = 0x4A;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_NCDESTROY = 0x0082;

    // Application hotkey ids must live in 0x0000..0xBFFF; use a high band to avoid clashes.
    private const int IdBase = 0x9000;
    private const int IdSpan = 0x2000;
    private static int s_nextId = -1;

    /// <summary>The default chord (Ctrl + Alt + D).</summary>
    public static HotkeyChoice Default => HotkeyChoice.CtrlAltD;

    /// <summary>All selectable chords, suitable for binding a settings list. Built once.</summary>
    public static IReadOnlyList<HotkeyOption> Options { get; } =
    [
        new(HotkeyChoice.CtrlAltD, "Ctrl + Alt + D"),
        new(HotkeyChoice.CtrlShiftD, "Ctrl + Shift + D"),
        new(HotkeyChoice.WinAltD, "Win + Alt + D"),
        new(HotkeyChoice.CtrlAltJ, "Ctrl + Alt + J"),
    ];

    private readonly object _sync = new();
    private readonly nint _hwnd;
    private readonly Action _callback;
    private readonly int _id;
    private readonly nuint _subclassId;
    private readonly nint _subclassProcPtr;

    private GCHandle _self;
    private HotkeyChoice _current;
    private bool _registered;
    private volatile bool _disposed;
    private bool _released;

    /// <exception cref="ArgumentException">hwnd is zero.</exception>
    /// <exception cref="Win32Exception">Subclassing or hotkey registration failed.</exception>
    public GlobalDictationHotkey(nint hwnd, HotkeyChoice choice, Action callback)
    {
        if (hwnd == 0) { throw new ArgumentException("A valid window handle is required.", nameof(hwnd)); }
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));

        var (modifiers, vk, _) = Resolve(choice); // validates choice before any native state.

        _hwnd = hwnd;
        _id = NextId();
        _subclassId = (nuint)_id;
        _current = choice;

        unsafe
        {
            delegate* unmanaged<nint, uint, nuint, nint, nuint, nuint, nint> proc = &SubclassProc;
            _subclassProcPtr = (nint)proc;
        }

        _self = GCHandle.Alloc(this); // rooted; passed back to the callback as ref-data.

        if (!SetWindowSubclass(_hwnd, _subclassProcPtr, _subclassId, (nuint)(nint)GCHandle.ToIntPtr(_self)))
        {
            int err = Marshal.GetLastWin32Error();
            _self.Free();
            throw new Win32Exception(err, "Failed to install the window subclass for the global hotkey.");
        }

        if (!RegisterHotKey(_hwnd, _id, modifiers | MOD_NOREPEAT, vk))
        {
            int err = Marshal.GetLastWin32Error();
            if (RemoveWindowSubclass(_hwnd, _subclassProcPtr, _subclassId))
                _self.Free();
            else
                Debug.WriteLine($"Global hotkey subclass removal failed after registration error: {Marshal.GetLastWin32Error()}");
            throw new Win32Exception(err, $"Failed to register the global hotkey {Describe(choice)} (it may be in use by another application).");
        }

        _registered = true;
    }

    /// <summary>The chord currently registered.</summary>
    public HotkeyChoice Current
    {
        get { lock (_sync) { return _current; } }
    }

    /// <summary>The option descriptor for <see cref="Current"/>.</summary>
    public HotkeyOption CurrentOption
    {
        get
        {
            var choice = Current;
            foreach (var option in Options)
            {
                if (option.Choice == choice) { return option; }
            }
            return Options[0];
        }
    }

    /// <summary>
    /// Switches to <paramref name="choice"/>. On any failure a <see cref="Win32Exception"/> is thrown and
    /// <see cref="Current"/> plus the live registration are left in a truthful state (never a silent no-op).
    /// </summary>
    public void Change(HotkeyChoice choice)
    {
        var (modifiers, vk, _) = Resolve(choice);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (choice == _current && _registered) { return; }

            // Release the currently registered chord (shares the same id). Surface a failed release.
            if (_registered)
            {
                if (!UnregisterHotKey(_hwnd, _id))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"Failed to release the current hotkey {Describe(_current)}; it remains registered.");
                }
                _registered = false;
            }

            if (RegisterHotKey(_hwnd, _id, modifiers | MOD_NOREPEAT, vk))
            {
                _current = choice;
                _registered = true;
                return;
            }

            // New chord failed. Try to restore the previous one and report what actually happened.
            int err = Marshal.GetLastWin32Error();
            var (pm, pvk, _) = Resolve(_current);
            if (RegisterHotKey(_hwnd, _id, pm | MOD_NOREPEAT, pvk))
            {
                _registered = true; // _current unchanged and active again.
                throw new Win32Exception(err,
                    $"Failed to register hotkey {Describe(choice)}; kept {Describe(_current)}.");
            }

            _registered = false; // nothing is active now.
            throw new Win32Exception(err,
                $"Failed to register hotkey {Describe(choice)} and could not restore {Describe(_current)}; no hotkey is active.");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) { return; }
            _disposed = true;
            ReleaseNative();
        }
    }

    // Runs under _sync, or from the subclass callback on the UI thread. Idempotent.
    private void ReleaseNative(bool windowDestroying = false)
    {
        if (_released) { return; }
        _released = true;
        _disposed = true;

        if (_registered)
        {
            if (!UnregisterHotKey(_hwnd, _id))
                Debug.WriteLine($"Global hotkey unregistration failed: {Marshal.GetLastWin32Error()}");
            _registered = false;
        }
        if (!RemoveWindowSubclass(_hwnd, _subclassProcPtr, _subclassId))
        {
            Debug.WriteLine($"Global hotkey subclass removal failed: {Marshal.GetLastWin32Error()}");
            if (!windowDestroying)
            {
                _released = false;
                return;
            }
        }
        if (_self.IsAllocated) { _self.Free(); }
    }

    [UnmanagedCallersOnly]
    private static nint SubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (dwRefData != 0)
        {
            var handle = GCHandle.FromIntPtr((nint)dwRefData);
            // `self` is a strong local, so freeing the GCHandle (on WM_NCDESTROY) cannot collect
            // the instance before this frame — including the DefSubclassProc call below — returns.
            if (handle.IsAllocated && handle.Target is GlobalDictationHotkey self)
            {
                if (uMsg == WM_HOTKEY && !self._disposed && (int)wParam == self._id)
                {
                    try { self._callback(); }
                    catch (Exception ex) { Debug.WriteLine($"[GlobalDictationHotkey] callback failed: {ex.GetType().Name}: {ex.Message}"); }
                }
                else if (uMsg == WM_NCDESTROY)
                {
                    lock (self._sync) { self.ReleaseNative(windowDestroying: true); }
                }
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private static int NextId()
    {
        int n = Interlocked.Increment(ref s_nextId);
        return IdBase + ((n % IdSpan) + IdSpan) % IdSpan;
    }

    private static (uint Modifiers, uint Vk, string Display) Resolve(HotkeyChoice choice) => choice switch
    {
        HotkeyChoice.CtrlAltD => (MOD_CONTROL | MOD_ALT, VK_D, "Ctrl + Alt + D"),
        HotkeyChoice.CtrlShiftD => (MOD_CONTROL | MOD_SHIFT, VK_D, "Ctrl + Shift + D"),
        HotkeyChoice.WinAltD => (MOD_WIN | MOD_ALT, VK_D, "Win + Alt + D"),
        HotkeyChoice.CtrlAltJ => (MOD_CONTROL | MOD_ALT, VK_J, "Ctrl + Alt + J"),
        _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, "Unsupported hotkey choice."),
    };

    private static string Describe(HotkeyChoice choice) => Resolve(choice).Display;

    /// <summary>Maps a persisted stable value back to a known chord.</summary>
    public static bool TryFromValue(int value, out HotkeyChoice choice)
    {
        foreach (var option in Options)
        {
            if (option.Value == value) { choice = option.Choice; return true; }
        }
        choice = Default;
        return false;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hWnd, nint pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint hWnd, nint pfnSubclass, nuint uIdSubclass);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam);
}
