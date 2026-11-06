using System.Diagnostics;
using System.Runtime.InteropServices;

internal static partial class NativeCapture
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Result
    {
        internal uint Size, Frames, Width, Height;
        internal int BorderRequired, BorderlessHResult;
        internal long StartQpc, QpcFrequency, FirstFrame100ns, LastFrame100ns;
        internal uint EncodedSamples, HeldSamples;
        internal long FirstEncodeQpc, LastEncodeQpc, StopQpc;
    }

    internal sealed record Completion(int HResult, Result Capture, double WallMs, double WorkerCpuMs);

    internal static Completion Record(string path, nint stop)
    {
        var value = new Result { Size = (uint)Marshal.SizeOf<Result>() };
        var started = Stopwatch.GetTimestamp();
        var cpu = ThreadCpu100ns();
        var hr = Run(path, 60, 0, stop, ref value);
        return new(hr, value, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            (ThreadCpu100ns() - cpu) / 10000.0);
    }

    private static ulong ThreadCpu100ns()
    {
        if (!GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        return kernel + user;
    }

    internal static double SystemActivePercent((ulong Idle, ulong Kernel, ulong User) before,
        (ulong Idle, ulong Kernel, ulong User) after)
    {
        var total = after.Kernel - before.Kernel + after.User - before.User;
        return total == 0 ? double.NaN : 100.0 * (total - (after.Idle - before.Idle)) / total;
    }

    internal static (ulong Idle, ulong Kernel, ulong User) SystemTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        return (idle, kernel, user);
    }

    [LibraryImport("record_encoder_spike.dll", EntryPoint = "record_encoder_run", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int Run(string path, uint seconds, int borderless, nint stop, ref Result result);
    [LibraryImport("record_encoder_spike.dll", EntryPoint = "record_encoder_inspect", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int Inspect(string path, out uint frames, out uint audioStreams, out long duration100ns);
    [LibraryImport("kernel32.dll")] private static partial nint GetCurrentThread();
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(nint thread, out ulong created, out ulong exited, out ulong kernel, out ulong user);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
