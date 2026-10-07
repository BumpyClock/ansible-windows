using System.Runtime.InteropServices;

namespace Ansible.Core.Windows;

internal interface IWaveInApi
{
    uint Open(out nint device, in WaveInFormat format, nint callback, nuint instance);
    uint Prepare(nint device, nint header);
    uint AddBuffer(nint device, nint header);
    uint Start(nint device);
    uint Stop(nint device);
    uint Reset(nint device);
    uint Unprepare(nint device, nint header);
    uint Close(nint device);
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveInFormat
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSecond;
    public uint AverageBytesPerSecond;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort ExtraSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaveInHeader
{
    public nint Data;
    public uint BufferLength;
    public uint BytesRecorded;
    public nuint User;
    public uint Flags;
    public uint Loops;
    public nint Next;
    public nuint Reserved;
}

internal sealed partial class WaveInApi : IWaveInApi
{
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveInHeader>();

    public uint Open(out nint device, in WaveInFormat format, nint callback, nuint instance) =>
        WaveInOpen(out device, uint.MaxValue, in format, callback, instance, 0x30000);
    public uint Prepare(nint device, nint header) => WaveInPrepareHeader(device, header, HeaderSize);
    public uint AddBuffer(nint device, nint header) => WaveInAddBuffer(device, header, HeaderSize);
    public uint Start(nint device) => WaveInStart(device);
    public uint Stop(nint device) => WaveInStop(device);
    public uint Reset(nint device) => WaveInReset(device);
    public uint Unprepare(nint device, nint header) => WaveInUnprepareHeader(device, header, HeaderSize);
    public uint Close(nint device) => WaveInClose(device);

    [LibraryImport("winmm.dll", EntryPoint = "waveInOpen")]
    private static partial uint WaveInOpen(out nint device, uint deviceId, in WaveInFormat format,
        nint callback, nuint instance, uint flags);
    [LibraryImport("winmm.dll", EntryPoint = "waveInPrepareHeader")]
    private static partial uint WaveInPrepareHeader(nint device, nint header, uint size);
    [LibraryImport("winmm.dll", EntryPoint = "waveInUnprepareHeader")]
    private static partial uint WaveInUnprepareHeader(nint device, nint header, uint size);
    [LibraryImport("winmm.dll", EntryPoint = "waveInAddBuffer")]
    private static partial uint WaveInAddBuffer(nint device, nint header, uint size);
    [LibraryImport("winmm.dll", EntryPoint = "waveInStart")]
    private static partial uint WaveInStart(nint device);
    [LibraryImport("winmm.dll", EntryPoint = "waveInStop")]
    private static partial uint WaveInStop(nint device);
    [LibraryImport("winmm.dll", EntryPoint = "waveInReset")]
    private static partial uint WaveInReset(nint device);
    [LibraryImport("winmm.dll", EntryPoint = "waveInClose")]
    private static partial uint WaveInClose(nint device);
}
