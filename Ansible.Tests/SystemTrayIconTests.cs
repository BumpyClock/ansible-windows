using System.Runtime.InteropServices;
using Ansible;

namespace Ansible.Tests;

public sealed partial class SystemTrayIconTests
{
    [WindowsTrayFact]
    public void HiddenWindowKeepsTrayAndMouseAndKeyboardActivation()
    {
        using var window = new TestWindow();
        var opens = 0;
        var exits = 0;
        var errors = new List<Exception>();
        using var tray = new SystemTrayIcon(window.Handle, IconPath, () => opens++, () => exits++, errors.Add);
        AssertIconExists(window.Handle);

        ShowWindow(window.Handle, 8); // SW_SHOWNA does not activate the test window.
        Assert.True(IsWindowVisible(window.Handle));
        ShowWindow(window.Handle, 0);
        Assert.False(IsWindowVisible(window.Handle));
        tray.EnsureVisible();
        tray.EnsureVisible();
        AssertIconExists(window.Handle);

        SendMessageW(window.Handle, 0x8000 + 73, 0, (1 << 16) | 0x0400);
        SendMessageW(window.Handle, 0x8000 + 73, 0, (1 << 16) | 0x0401);
        Assert.Equal(2, opens);
        Assert.Equal(0, exits);
        Assert.Empty(errors);

        tray.Dispose();
        tray.Dispose();
        AssertIconMissing(window.Handle);
        SendMessageW(window.Handle, 0x8000 + 73, 0, (1 << 16) | 0x0400);
        Assert.Equal(2, opens);
        Assert.Throws<ObjectDisposedException>(tray.EnsureVisible);
    }

    [WindowsTrayFact]
    public void TaskbarRecreationRestoresIconWithoutOpeningWindowOrExiting()
    {
        using var window = new TestWindow();
        var opens = 0;
        var exits = 0;
        var errors = new List<Exception>();
        using var tray = new SystemTrayIcon(window.Handle, IconPath, () => opens++, () => exits++, errors.Add);
        var data = new DeleteIconData { Size = (uint)Marshal.SizeOf<DeleteIconData>(), Window = window.Handle, Id = 1 };
        Assert.True(Shell_NotifyIconW(2, ref data));
        AssertIconMissing(window.Handle);

        SendMessageW(window.Handle, RegisterWindowMessageW("TaskbarCreated"), 0, 0);
        AssertIconExists(window.Handle);
        Assert.False(IsWindowVisible(window.Handle));
        Assert.Equal(0, opens);
        Assert.Equal(0, exits);
        Assert.Empty(errors);
    }

    [WindowsTrayFact]
    public void NativeWindowDestructionRemovesIconAndReleasesCallback()
    {
        using var window = new TestWindow();
        using var tray = new SystemTrayIcon(window.Handle, IconPath, () => { }, () => { }, _ => { });
        AssertIconExists(window.Handle);
        var handle = window.Handle;
        window.Dispose();
        AssertIconMissing(handle);
        tray.Dispose();
        Assert.Throws<ObjectDisposedException>(tray.EnsureVisible);
    }

    [WindowsTrayFact]
    public void CallbackFailuresAreReportedWithoutUnwindingIntoNativeCode()
    {
        using var window = new TestWindow();
        var expected = new InvalidOperationException("Open failed.");
        var errors = new List<Exception>();
        using var tray = new SystemTrayIcon(window.Handle, IconPath, () => throw expected, () => { }, errors.Add);
        SendMessageW(window.Handle, 0x8000 + 73, 0, (1 << 16) | 0x0400);
        Assert.Same(expected, Assert.Single(errors));
        AssertIconExists(window.Handle);
    }

    [WindowsTrayFact]
    public void FailedCreationLeavesWindowUsableAndAllowsAnotherAttempt()
    {
        using var window = new TestWindow();
        Assert.Throws<System.ComponentModel.Win32Exception>(() =>
            new SystemTrayIcon(window.Handle, Path.Combine(AppContext.BaseDirectory, "missing-tray-icon.ico"),
                () => { }, () => { }, _ => { }));
        Assert.True(IsWindow(window.Handle));
        AssertIconMissing(window.Handle);
        using var tray = new SystemTrayIcon(window.Handle, IconPath, () => { }, () => { }, _ => { });
        AssertIconExists(window.Handle);
    }

    private static string IconPath => Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");

    private static void AssertIconExists(nint window)
    {
        var identifier = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = window, Id = 1 };
        Assert.Equal(0, Shell_NotifyIconGetRect(ref identifier, out _));
    }

    private static void AssertIconMissing(nint window)
    {
        var identifier = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = window, Id = 1 };
        Assert.NotEqual(0, Shell_NotifyIconGetRect(ref identifier, out _));
    }

    private sealed class TestWindow : IDisposable
    {
        public nint Handle { get; private set; } = CreateWindowExW(0, "STATIC", "Ansible tray test", 0x00CF0000,
            0, 0, 100, 100, 0, 0, 0, 0);

        public TestWindow() => Assert.NotEqual(0, Handle);

        public void Dispose()
        {
            if (Handle == 0) return;
            Assert.True(DestroyWindow(Handle));
            Handle = 0;
        }
    }

    public sealed class WindowsTrayFactAttribute : FactAttribute
    {
        public WindowsTrayFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || GetShellWindow() == 0)
                Skip = "Requires an interactive Windows shell for notification-area integration.";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconIdentifier
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DeleteIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint Callback;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string message);

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();

    [LibraryImport("shell32.dll")]
    private static partial int Shell_NotifyIconGetRect(ref IconIdentifier identifier, out Rect rect);

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIconW(uint message, ref DeleteIconData data);
}
