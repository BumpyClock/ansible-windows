using System.Runtime.InteropServices;

namespace DictationPoc.Core;

internal static partial class NativeMemory
{
    public static void CheckModelBudget(string path, int headroomMegabytes)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("This native POC runs on Windows.");
        }
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect available memory before loading a model.");
        }
        var required = new FileInfo(path).Length * 1.15 + (256L + headroomMegabytes) * 1024 * 1024;
        if (status.AvailablePhysical < required)
        {
            throw new InvalidOperationException(
                $"This model needs an estimated {required / (1024 * 1024 * 1024):F1} GiB of available memory, including headroom. " +
                $"Windows currently reports {status.AvailablePhysical / (1024.0 * 1024 * 1024):F1} GiB. " +
                "Free memory or choose a smaller model; the app does not force a load or silently change models.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint Load;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
