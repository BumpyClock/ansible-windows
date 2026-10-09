using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Ansible.Core.Windows;

public static partial class WindowCapturePrivacy
{
    public const uint ExcludeFromCapture = 0x11;

    public static void Apply(nint windowHandle, bool enabled)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Screen capture privacy requires Windows.");
        if (enabled && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException("Excluding windows from capture requires Windows 10 version 2004 or later.");
        if (windowHandle == 0) throw new ArgumentException("An owned top-level window is required.", nameof(windowHandle));
        var affinity = enabled ? ExcludeFromCapture : 0u;
        if (!SetWindowDisplayAffinity(windowHandle, affinity))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not update screen capture privacy for this window.");
        if (!GetWindowDisplayAffinity(windowHandle, out var actual))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not verify screen capture privacy for this window.");
        if (actual != affinity)
            throw new InvalidOperationException("Windows did not apply the requested screen capture privacy. The window may remain visible in captures.");
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint windowHandle, uint affinity);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowDisplayAffinity(nint windowHandle, out uint affinity);
}
