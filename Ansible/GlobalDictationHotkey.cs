using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Ansible.Core;

namespace Ansible;

// Global dictation hotkey. Construct / Suspend / Resume / Dispose on the UI thread that owns hwnd.
// Win32 only delivers a key-DOWN for a hotkey, so press is reported from WM_HOTKEY and release is detected
// by polling GetAsyncKeyState from a DispatcherTimer (no low-level keyboard hook). The window is never
// focused and no input is synthesised. onPressedChanged(true) fires on press; onPressedChanged(false) fires
// when any chord key/modifier is released.

internal sealed partial class GlobalDictationHotkey : IDisposable
{
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_NCDESTROY = 0x0082;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;   // Alt
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    private const int PollIntervalMs = 20;

    // Application hotkey ids must live in 0x0000..0xBFFF; use a high band to avoid clashes.
    private const int IdBase = 0x9000;
    private const int IdSpan = 0x2000;
    private static int s_nextId = -1;

    private readonly object _sync = new();
    private readonly nint _hwnd;
    private readonly Action<bool> _callback;
    private readonly DictationShortcut _shortcut;
    private readonly int _id;
    private readonly nuint _subclassId;
    private readonly nint _subclassProcPtr;
    private readonly DispatcherTimer _pollTimer;

    private GCHandle _self;
    private bool _registered;
    private bool _pressed;
    private volatile bool _disposed;
    private bool _released;

    /// <exception cref="ArgumentException">hwnd is zero or the shortcut is not valid.</exception>
    /// <exception cref="Win32Exception">Subclassing or hotkey registration failed.</exception>
    public GlobalDictationHotkey(nint hwnd, DictationShortcut shortcut, Action<bool> onPressedChanged)
    {
        if (hwnd == 0) { throw new ArgumentException("A valid window handle is required.", nameof(hwnd)); }
        if (!shortcut.IsValid) { throw new ArgumentException($"{shortcut.DisplayText} is not a valid dictation shortcut.", nameof(shortcut)); }
        _callback = onPressedChanged ?? throw new ArgumentNullException(nameof(onPressedChanged));

        _hwnd = hwnd;
        _id = NextId();
        _subclassId = (nuint)_id;
        _shortcut = shortcut;

        // Created on the UI thread so the timer binds to this thread's dispatcher queue.
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
        _pollTimer.Tick += OnPollTick;

        unsafe
        {
            delegate* unmanaged<nint, uint, nuint, nint, nuint, nuint, nint> proc = &SubclassProc;
            _subclassProcPtr = (nint)proc;
        }

        _self = GCHandle.Alloc(this); // rooted; handed to the subclass proc as ref-data.

        if (!SetWindowSubclass(_hwnd, _subclassProcPtr, _subclassId, (nuint)(nint)GCHandle.ToIntPtr(_self)))
        {
            int err = Marshal.GetLastWin32Error();
            _self.Free();
            throw new Win32Exception(err, "Failed to install the window subclass for the global hotkey.");
        }

        if (!RegisterHotKey(_hwnd, _id, shortcut.Modifiers | MOD_NOREPEAT, shortcut.Key))
        {
            int err = Marshal.GetLastWin32Error();
            if (RemoveWindowSubclass(_hwnd, _subclassProcPtr, _subclassId))
                _self.Free();
            else
                Debug.WriteLine($"Global hotkey subclass removal failed after registration error: {Marshal.GetLastWin32Error()}");
            throw new Win32Exception(err, $"Failed to register the global hotkey {shortcut.DisplayText} (it may be in use by another application).");
        }

        _registered = true;
    }

    /// <summary>
    /// Unregisters the chord so the caller can capture a new shortcut by pressing it (otherwise RegisterHotKey
    /// would swallow the recorded keys). Stops release polling and emits a release if the chord was held. A
    /// failed release changes nothing and throws, since the keys would still be intercepted. Idempotent.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The adapter has been disposed.</exception>
    /// <exception cref="Win32Exception">The chord could not be released.</exception>
    public void Suspend()
    {
        bool fireRelease = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_registered) { return; }

            if (!UnregisterHotKey(_hwnd, _id))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"Failed to release {_shortcut.DisplayText} for capture; it is still registered.");
            }
            _registered = false;

            _pollTimer.Stop();
            if (_pressed) { _pressed = false; fireRelease = true; }
        }

        if (fireRelease) { SafeInvoke(false); }
    }

    /// <summary>
    /// Re-registers the chord after <see cref="Suspend"/>. On failure the adapter stays unregistered and throws,
    /// so a chord taken by another app surfaces instead of silently vanishing. Idempotent when already live.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The adapter has been disposed.</exception>
    /// <exception cref="Win32Exception">The chord could not be re-registered.</exception>
    public void Resume()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_registered) { return; }

            if (!RegisterHotKey(_hwnd, _id, _shortcut.Modifiers | MOD_NOREPEAT, _shortcut.Key))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"Failed to re-register {_shortcut.DisplayText} after capture (it may be in use by another application).");
            }
            _registered = true;
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

    // Runs under _sync, or from the subclass callback on WM_NCDESTROY. Idempotent; a failed subclass removal
    // (unless the window is already being destroyed) is retried on a later call rather than leaking the GCHandle.
    private void ReleaseNative(bool windowDestroying = false)
    {
        if (_released) { return; }

        _pressed = false;
        _pollTimer.Stop();
        _pollTimer.Tick -= OnPollTick;

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

    // UI-thread tick: ends the press as soon as any chord key/modifier is no longer held.
    private void OnPollTick(object? sender, object e)
    {
        bool fireRelease = false;
        lock (_sync)
        {
            if (_disposed || !_pressed)
            {
                _pollTimer.Stop();
            }
            else if (!ChordHeld(_shortcut))
            {
                _pressed = false;
                _pollTimer.Stop();
                fireRelease = true;
            }
        }

        if (fireRelease) { SafeInvoke(false); }
    }

    // UI-thread WM_HOTKEY handler. Only starts a press when the chord is genuinely held, so a stale/queued
    // message whose keys are already up cannot start a push-to-talk session that never ends. _pressed and
    // MOD_NOREPEAT together suppress auto-repeat and reentrancy.
    private void OnHotkeyPressed()
    {
        bool start = false;
        lock (_sync)
        {
            if (_disposed || !_registered || _pressed) { return; }
            if (!ChordHeld(_shortcut)) { return; }
            _pressed = true;
            _pollTimer.Start();
            start = true;
        }

        if (start) { SafeInvoke(true); }
    }

    private void SafeInvoke(bool pressed)
    {
        try { _callback(pressed); }
        catch (Exception ex) { Debug.WriteLine($"[GlobalDictationHotkey] callback failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static bool ChordHeld(DictationShortcut shortcut)
    {
        if (!IsKeyDown((int)shortcut.Key)) { return false; }

        uint m = shortcut.Modifiers;
        if ((m & MOD_CONTROL) != 0 && !IsKeyDown(VK_CONTROL)) { return false; }
        if ((m & MOD_ALT) != 0 && !IsKeyDown(VK_MENU)) { return false; }
        if ((m & MOD_SHIFT) != 0 && !IsKeyDown(VK_SHIFT)) { return false; }
        if ((m & MOD_WIN) != 0 && !(IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN))) { return false; }
        return true;
    }

    private static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [UnmanagedCallersOnly]
    private static nint SubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (dwRefData != 0)
        {
            var handle = GCHandle.FromIntPtr((nint)dwRefData);
            // `self` is a strong local, so freeing the GCHandle (on WM_NCDESTROY) cannot collect the instance
            // before this frame — including the DefSubclassProc call below — returns.
            if (handle.IsAllocated && handle.Target is GlobalDictationHotkey self)
            {
                if (uMsg == WM_HOTKEY && !self._disposed && (int)wParam == self._id)
                {
                    self.OnHotkeyPressed();
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

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hWnd, nint pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint hWnd, nint pfnSubclass, nuint uIdSubclass);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam);
}
