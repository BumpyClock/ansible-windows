using System.Runtime.InteropServices;

namespace DictationPoc.Core;

internal static partial class NativeMemory
{
    internal const long MaximumDecodedBytes = 64L * 1024 * 1024;
    internal const int MaximumSeconds = 5 * 60;
    internal const long MaximumStreamFrames = 16000L * MaximumSeconds;

    public static long ValidateAudio(WaveAudio audio, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(audio);
        token.ThrowIfCancellationRequested();
        if (audio.Samples is null || audio.Samples.Length == 0 || audio.SampleRate <= 0 ||
            audio.Channels <= 0 || audio.Samples.Length % audio.Channels != 0)
            throw new InvalidDataException("Recording audio must contain complete interleaved frames.");
        var bytes = (long)audio.Samples.Length * sizeof(float);
        if (bytes > MaximumDecodedBytes ||
            audio.Samples.Length / (double)audio.Channels / audio.SampleRate > MaximumSeconds)
            throw new InvalidDataException("Recording audio exceeds the five-minute or 64 MiB decoded limit.");
        for (var index = 0; index < audio.Samples.Length; index++)
        {
            if ((index & 4095) == 0)
                token.ThrowIfCancellationRequested();
            if (!float.IsFinite(audio.Samples[index]))
                throw new InvalidDataException("Recording audio contains a non-finite sample.");
        }
        token.ThrowIfCancellationRequested();
        return bytes;
    }

    internal static long EstimateRequired(long modelBytes, long decodedBytes, int headroomMegabytes, bool resident)
    {
        if (modelBytes <= 0 || decodedBytes is < 0 or > MaximumDecodedBytes || headroomMegabytes < 0)
            throw new ArgumentOutOfRangeException(nameof(modelBytes));
        // Admission estimate, not an allocator guarantee: weights, decoded/native copies, resampling and graph workspace.
        return checked((resident ? 0 : (long)Math.Ceiling(modelBytes * 1.15)) +
            3 * decodedBytes + MaximumStreamFrames * (sizeof(float) + sizeof(short)) +
            (512L + headroomMegabytes) * 1024 * 1024);
    }

    public static void CheckBudget(long modelBytes, long decodedBytes, int headroomMegabytes, bool resident)
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
        var required = EstimateRequired(modelBytes, decodedBytes, headroomMegabytes, resident);
        if (status.AvailablePhysical < (ulong)required || status.AvailablePageFile < (ulong)required)
        {
            throw new InvalidOperationException(
                $"This operation needs an estimated {required / (1024.0 * 1024 * 1024):F1} GiB of available memory, including audio, workspace and headroom. " +
                $"Windows reports {status.AvailablePhysical / (1024.0 * 1024 * 1024):F1} GiB physical and " +
                $"{status.AvailablePageFile / (1024.0 * 1024 * 1024):F1} GiB commit capacity available. " +
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
