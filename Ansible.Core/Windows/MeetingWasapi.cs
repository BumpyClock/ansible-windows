using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ansible.Core.Windows;

internal static unsafe partial class MeetingWasapi
{
    internal static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    internal static readonly Guid CaptureClientId = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    private static readonly Guid EnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid EnumeratorId = new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    [LibraryImport("ole32.dll")] internal static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")] internal static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
    [LibraryImport("Mmdevapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int ActivateAudioInterfaceAsync(string path, in Guid iid, nint parameters, nint handler, out nint operation);
    internal static nint Slot(nint instance, int index) => ((nint*)(*(nint*)instance))[index];
    internal static void Release(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
    }
    internal static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    internal static nint OpenDefault(int flow)
    {
        Check(CoCreateInstance(in EnumeratorClass, 0, 1, in EnumeratorId, out var enumerator));
        nint device = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Slot(enumerator, 4))(enumerator, flow, 1, &device));
            nint client = 0;
            var iid = AudioClientId;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Slot(device, 3))(device, &iid, 23, 0, &client));
            return client;
        }
        finally { Release(device); Release(enumerator); }
    }
    internal static void Initialize(nint client, bool loopback)
    {
        var format = new Format { Tag = 1, Channels = 1, Rate = 16000, Bytes = 32000, Align = 2, Bits = 16 };
        // Shared-mode conversion keeps capture packets in the engine's 16 kHz PCM16 format.
        uint flags = 0x80000000 | 0x08000000 | (loopback ? 0x00020000u : 0);
        Check(((delegate* unmanaged[Stdcall]<nint, int, uint, long, long, Format*, nint, int>)Slot(client, 3))(client, 0, flags, 1000000, 0, &format, 0));
    }
    internal static nint CaptureService(nint client)
    {
        var iid = CaptureClientId;
        nint capture = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(client, 14))(client, &iid, &capture));
        return capture;
    }
    internal static void Start(nint client) => Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(client, 10))(client));
    internal static void Stop(nint client) => Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(client, 11))(client));
    internal static MeetingCapturePacket? Read(nint capture, ref bool firstPacket)
    {
        uint count = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(capture, 5))(capture, &count));
        if (count == 0) return null;
        nint data = 0;
        uint flags = 0;
        ulong position = 0, clock = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, uint*, uint*, ulong*, ulong*, int>)Slot(capture, 3))(capture, &data, &count, &flags, &position, &clock));
        try
        {
            if ((flags & 1) != 0 && !firstPacket) throw new IOException("Meeting audio capture reported a discontinuity. Audio may have been lost.");
            if ((flags & 4) != 0 || clock == 0 || clock > long.MaxValue)
                throw new IOException("Meeting capture reported an invalid audio timestamp.");
            if (count is 0 or > MeetingPacketTimeline.CapacitySamples)
                throw new IOException("Meeting capture returned an invalid native packet size.");
            firstPacket = false;
            var bytes = new byte[checked((int)count * 2)];
            if ((flags & 2) == 0) Marshal.Copy(data, bytes, 0, bytes.Length);
            return new(bytes, clock, position);
        }
        finally { Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(capture, 4))(capture, count)); }
    }
    [StructLayout(LayoutKind.Sequential, Pack = 2)] private struct Format
    {
        internal ushort Tag, Channels;
        internal uint Rate, Bytes;
        internal ushort Align, Bits, Extra;
    }
}
