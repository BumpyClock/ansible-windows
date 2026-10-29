using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Ansible.Core;

namespace Ansible;

// Global dictation hotkey. Construct / Suspend / Resume / Dispose on the UI thread that owns hwnd.
// Win32 only delivers a key-DOWN for a hotkey, so press is reported from WM_HOTKEY and release is detected
// by polling GetAsyncKeyState from a DispatcherTimer. Ctrl+Win alone uses a low-level keyboard hook
// on the same message thread, with a short qualification delay to exclude ordinary three-key chords.
// Physical downs and ups are always forwarded. A balanced, unassigned-key pair masks Start on qualification.

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
    private readonly uint _ownerThread;
    private readonly CtrlWinShortcutState _modifierState = new();
    private readonly KeyboardHookCallback _keyboardCallback;
    // Keep callbacks rooted even if Windows fails to release a hook.
    [ThreadStatic] private static HashSet<GlobalDictationHotkey>? s_hookOwners;
    private nint _keyboardHook;
    private WindowsTextTarget.KeyboardEvent[]? _pendingMenuMaskRelease;

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
        _ownerThread = GetCurrentThreadId();
        if (GetWindowThreadProcessId(hwnd, out _) != _ownerThread)
            throw new ArgumentException("The hotkey must be created on its window's message thread.", nameof(hwnd));
        _keyboardCallback = OnKeyboardInput;

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

        try
        {
            Register();
        }
        catch
        {
            if (RemoveWindowSubclass(_hwnd, _subclassProcPtr, _subclassId))
                _self.Free();
            else
                Debug.WriteLine($"Global hotkey subclass removal failed after registration error: {Marshal.GetLastWin32Error()}");
            _pollTimer.Tick -= OnPollTick;
            throw;
        }
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
        VerifyOwnerThread();
        bool fireRelease = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_registered) { return; }
            ReleaseMenuMaskKey();

            if (!Unregister())
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"Failed to release {_shortcut.DisplayText} for capture; it is still registered.");
            }
            _registered = false;
            _modifierState.Reset();

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
        VerifyOwnerThread();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_registered) { return; }

            Register();
        }
    }

    public void Dispose()
    {
        VerifyOwnerThread();
        bool fireRelease = false;
        try
        {
            lock (_sync)
            {
                if (_released) { return; }
                fireRelease = _pressed;
                _disposed = true;
                ReleaseNative();
            }
        }
        finally { if (fireRelease) SafeInvoke(false); }
    }

    // Runs under _sync, or from the subclass callback on WM_NCDESTROY. Idempotent; a failed subclass removal
    // (unless the window is already being destroyed) is retried on a later call rather than leaking the GCHandle.
    private void ReleaseNative(bool windowDestroying = false)
    {
        if (_released) { return; }
        ReleaseMenuMaskKey();

        _pressed = false;
        _modifierState.Reset();
        _pollTimer.Stop();
        _pollTimer.Tick -= OnPollTick;

        _released = true;
        _disposed = true;

        if (_registered)
        {
            if (!Unregister())
            {
                _released = false;
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to release the global dictation shortcut.");
            }
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
        if (_shortcut.IsModifierOnly)
        {
            if (_disposed || !_registered) { _pollTimer.Stop(); return; }
            if (_modifierState.Advance(Environment.TickCount64))
            {
                try
                {
                    MaskWindowsMenu();
                    _pressed = true;
                    SafeInvoke(true);
                }
                catch (Exception error)
                {
                    _modifierState.Reset(PhysicalKeysDown());
                    Trace.TraceWarning($"Ctrl + Win could not suppress Start; dictation was not started: {error.Message}");
                }
            }
            if (!_modifierState.IsPending) _pollTimer.Stop();
            return;
        }
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

    // WM_HOTKEY records a valid press even if its keys were released before dispatch.
    // Check release immediately after the press callback, then poll any chord still held.
    private void OnHotkeyPressed()
    {
        bool start = false;
        lock (_sync)
        {
            if (_disposed || !_registered || _pressed) { return; }
            _pressed = true;
            _pollTimer.Start();
            start = true;
        }

        if (start)
        {
            SafeInvoke(true);
            OnPollTick(null, EventArgs.Empty);
        }
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

    private void VerifyOwnerThread()
    {
        if (GetCurrentThreadId() != _ownerThread)
            throw new InvalidOperationException("Global hotkeys must be managed on their owning window's message thread.");
    }

    private void Register()
    {
        if (_shortcut.IsModifierOnly)
        {
            // Keys held while installing/resuming must be released before a fresh chord can start.
            _modifierState.Reset(PhysicalKeysDown());
            _keyboardHook = SetWindowsHookExW(13, Marshal.GetFunctionPointerForDelegate(_keyboardCallback), GetModuleHandleW(null), 0);
            if (_keyboardHook == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to install the Ctrl + Win global keyboard hook.");
            (s_hookOwners ??= []).Add(this);
        }
        else if (!RegisterHotKey(_hwnd, _id, _shortcut.Modifiers | MOD_NOREPEAT, _shortcut.Key))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"Failed to register the global hotkey {_shortcut.DisplayText} (it may be in use by another application).");
        }
        _registered = true;
    }

    private bool Unregister()
    {
        if (!_shortcut.IsModifierOnly) return UnregisterHotKey(_hwnd, _id);
        if (_keyboardHook == 0) return true;
        if (!UnhookWindowsHookEx(_keyboardHook)) return false;
        _keyboardHook = 0;
        s_hookOwners?.Remove(this);
        return true;
    }

    private nint OnKeyboardInput(int code, nuint message, nint data)
    {
        if (code >= 0 && !_disposed && _registered)
        {
            try
            {
                var input = Marshal.PtrToStructure<KeyboardInput>(data);
                var down = message is 0x0100 or 0x0104;
                var up = message is 0x0101 or 0x0105;
                if (down || up)
                {
                    var wasPressed = _modifierState.IsPressed;
                    _modifierState.Process(input.Key, down, (input.Flags & 0x12) != 0, Environment.TickCount64);
                    if (_modifierState.IsPending) _pollTimer.Start();
                    else _pollTimer.Stop();
                    if (wasPressed && !_modifierState.IsPressed)
                    {
                        _pressed = false;
                        SafeInvoke(false);
                    }
                }
            }
            catch (Exception error)
            {
                Debug.WriteLine($"Global modifier shortcut callback failed: {error}");
            }
        }
        return CallNextHookEx(_keyboardHook, code, message, data);
    }

    private static IEnumerable<uint> PhysicalKeysDown() => Enumerable.Range(8, 248)
        .Where(key => key is not VK_CONTROL and not VK_SHIFT and not VK_MENU and not 0xE8 && IsKeyDown(key))
        .Select(key => (uint)key);

    private void MaskWindowsMenu()
    {
        ReleaseMenuMaskKey();
        // VK_E8 is unassigned. Unlike dropping Win-up, a complete mask leaves physical modifier state intact.
        // https://www.autohotkey.com/docs/v2/lib/A_MenuMaskKey.htm documents this menu-masking key.
        // Send the pair together, while Ctrl+Win are still down, before reporting the qualified press.
        var release = new[] { WindowsTextTarget.KeyboardEvent.Virtual(0xE8, 2) };
        var inputs = new[] { WindowsTextTarget.KeyboardEvent.Virtual(0xE8, 0), release[0] };
        var sent = SendMenuMaskInput(inputs);
        if (sent == 2) return;
        var error = Marshal.GetLastWin32Error();
        if (sent == 1)
        {
            _pendingMenuMaskRelease = release;
            ReleaseMenuMaskKey();
            return;
        }
        throw new Win32Exception(error, "Windows did not queue the balanced Start-menu mask.");
    }

    private void ReleaseMenuMaskKey()
    {
        if (_pendingMenuMaskRelease is not { } release) return;
        if (!WindowsTextTarget.ReleaseOwnedKeys(release, SendMenuMaskInput))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not release the Start-menu mask key.");
        _pendingMenuMaskRelease = null;
    }

    private static uint SendMenuMaskInput(WindowsTextTarget.KeyboardEvent[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<WindowsTextTarget.KeyboardEvent>());

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint KeyboardHookCallback(int code, nuint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public uint Key;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

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
                    var wasPressed = self._pressed;
                    try { lock (self._sync) { self.ReleaseNative(windowDestroying: true); } }
                    catch (Exception error) { Debug.WriteLine($"Global shortcut teardown failed: {error}"); }
                    finally { if (wasPressed) self.SafeInvoke(false); }
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

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, [In] WindowsTextTarget.KeyboardEvent[] inputs, int size);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowsHookExW(int hook, nint callback, nint module, uint threadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hook, int code, nuint message, nint data);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hWnd, nint pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint hWnd, nint pfnSubclass, nuint uIdSubclass);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam);
}
