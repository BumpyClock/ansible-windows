using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ansible;

// Owned by the main window's UI thread. The HWND stays alive while the window is hidden.
internal sealed unsafe partial class SystemTrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 73;
    private const uint IconId = 1;
    private const nuint SubclassId = 0xA751;
    private const uint OpenCommand = 1;
    private const uint ExitCommand = 2;
    private readonly nint _window;
    private readonly Action _open;
    private readonly Action _exit;
    private readonly Action<Exception> _reportError;
    private readonly uint _taskbarCreated;
    private readonly nint _procedure;
    private GCHandle _self;
    private nint _icon;
    private bool _added;
    private bool _disposed;

    public SystemTrayIcon(nint window, string iconPath, Action open, Action exit, Action<Exception> reportError)
    {
        _window = window;
        _open = open;
        _exit = exit;
        _reportError = reportError;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        if (_taskbarCreated == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register tray recovery.");
        _icon = LoadImageW(0, iconPath, 1, 0, 0, 0x0010 | 0x0040); // IMAGE_ICON, LR_LOADFROMFILE | LR_DEFAULTSIZE.
        if (_icon == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load the Ansible tray icon.");
        _procedure = (nint)(delegate* unmanaged<nint, uint, nuint, nint, nuint, nuint, nint>)&WindowProcedure;
        _self = GCHandle.Alloc(this);
        if (!SetWindowSubclass(_window, _procedure, SubclassId, (nuint)(nint)GCHandle.ToIntPtr(_self)))
        {
            var error = Marshal.GetLastWin32Error();
            _self.Free();
            DestroyIcon(_icon);
            _icon = 0;
            throw new Win32Exception(error, "Could not attach the Ansible tray icon.");
        }
        try { EnsureVisible(); }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void EnsureVisible()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_added) return;
        var data = CreateData();
        if (!Shell_NotifyIconW(0, ref data))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the Ansible tray icon. The window will stay open.");
        _added = true;
        data.Version = 4;
        if (!Shell_NotifyIconW(4, ref data))
        {
            var error = Marshal.GetLastWin32Error();
            RemoveIcon();
            throw new Win32Exception(error, "Could not configure the Ansible tray icon. The window will stay open.");
        }
    }

    private NotifyIconData CreateData()
    {
        var data = new NotifyIconData
        {
            Size = (uint)sizeof(NotifyIconData),
            Window = _window,
            Id = IconId,
            Flags = 0x0001 | 0x0002 | 0x0004 | 0x0080, // MESSAGE | ICON | TIP | SHOWTIP.
            CallbackMessage = CallbackMessage,
            Icon = _icon
        };
        const string tooltip = "Ansible";
        for (var index = 0; index < tooltip.Length; index++) data.Tip[index] = tooltip[index];
        return data;
    }

    private void ShowMenu(nuint position)
    {
        var menu = CreatePopupMenu();
        if (menu == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the tray menu.");
        try
        {
            if (!AppendMenuW(menu, 0, OpenCommand, "Open Ansible") ||
                !AppendMenuW(menu, 0, ExitCommand, "Exit") || !SetMenuDefaultItem(menu, OpenCommand, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the tray menu.");
            // Version 4 notifications supply signed screen coordinates, including for keyboard invocation.
            var x = (short)(position & 0xFFFF);
            var y = (short)((position >> 16) & 0xFFFF);
            SetForegroundWindow(_window);
            var command = TrackPopupMenuEx(menu, 0x0100 | 0x0002, x, y, _window, 0); // RETURNCMD | RIGHTBUTTON.
            PostMessageW(_window, 0, 0, 0); // Let the shell dismiss the menu on the next invocation.
            if (command == OpenCommand) _open();
            else if (command == ExitCommand) _exit();
        }
        finally { DestroyMenu(menu); }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference)
    {
        var self = (SystemTrayIcon)GCHandle.FromIntPtr((nint)reference).Target!;
        try
        {
            if (message == 0x0082) self.Release(windowDestroying: true); // WM_NCDESTROY.
            else if (!self._disposed)
            {
                if (message == self._taskbarCreated)
                {
                    self._added = false;
                    self.EnsureVisible();
                }
                else if (message == CallbackMessage && (uint)((nuint)lParam >> 16) == IconId)
                {
                    var notification = (uint)((nuint)lParam & 0xFFFF);
                    if (notification is 0x0400 or 0x0401) self._open(); // NIN_SELECT / NIN_KEYSELECT.
                    else if (notification == 0x007B) self.ShowMenu(wParam); // WM_CONTEXTMENU.
                    return 0;
                }
            }
        }
        catch (Exception error)
        {
            // Never unwind managed exceptions through the shell's native callback.
            try { self._reportError(error); }
            catch (Exception reportError) { Debug.WriteLine($"Ansible: tray error reporting failed: {reportError}"); }
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose() => Release(windowDestroying: false);

    private void Release(bool windowDestroying)
    {
        _disposed = true;
        RemoveIcon();
        if (_icon != 0)
        {
            if (!DestroyIcon(_icon)) Debug.WriteLine("Ansible: could not release the tray icon handle.");
            _icon = 0;
        }
        if (!_self.IsAllocated) return;
        if (RemoveWindowSubclass(_window, _procedure, SubclassId) || windowDestroying) _self.Free();
        else Debug.WriteLine("Ansible: tray subclass removal failed; retaining callback ownership until window destruction.");
    }

    private void RemoveIcon()
    {
        if (!_added) return;
        var data = CreateData();
        if (!Shell_NotifyIconW(2, ref data)) Debug.WriteLine("Ansible: could not remove the tray icon.");
        _added = false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint Version;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint RegisterWindowMessageW(string message);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint LoadImageW(nint instance, string name, uint type, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint window, nint procedure, nuint id, nuint reference);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint window, nint procedure, nuint id);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nuint id, string text);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetMenuDefaultItem(nint menu, uint item, uint byPosition);

    [LibraryImport("user32.dll")]
    private static partial uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
}
