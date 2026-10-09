using System.Runtime.InteropServices;
using Ansible.Core.Windows;

namespace Ansible.Tests;

public sealed partial class WindowCapturePrivacyTests
{
    [Fact]
    public void OwnedOffscreenWindowCanEnableVerifyAndRemoveCaptureExclusion()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return;
        var window = CreateWindowExW(0, "STATIC", "Ansible capture privacy test", 0x00CF0000,
            -32000, -32000, 100, 100, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            WindowCapturePrivacy.Apply(window, true);
            Assert.True(WindowCapturePrivacy.GetWindowDisplayAffinity(window, out var affinity));
            Assert.Equal(0x11u, affinity);
            WindowCapturePrivacy.Apply(window, false);
            Assert.True(WindowCapturePrivacy.GetWindowDisplayAffinity(window, out affinity));
            Assert.Equal(0u, affinity);
        }
        finally { Assert.True(DestroyWindow(window)); }
        Assert.Throws<System.ComponentModel.Win32Exception>(() => WindowCapturePrivacy.Apply(window, true));
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint style, string className, string title, uint windowStyle,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);
}
