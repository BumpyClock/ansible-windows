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
    internal static nint Slot(nint instance, int index) => ((nint*)(*(nint*)instance))[index];
    internal static void Release(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
    }
    internal static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    internal static nint CreateEnumerator()
    {
        Check(CoCreateInstance(in EnumeratorClass, 0, 1, in EnumeratorId, out var enumerator));
        return enumerator;
    }
    internal static nint OpenDefault(int flow)
    {
        var enumerator = CreateEnumerator();
        nint device = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Slot(enumerator, 4))(enumerator, flow, 1, &device));
            return Activate(device);
        }
        finally { Release(device); Release(enumerator); }
    }
    /// <summary>Endpoint IDs stay in memory to match devices across rescans; never log them.</summary>
    internal static List<string> ActiveRenderEndpointIds(nint enumerator)
    {
        nint collection = 0;
        // eRender = 0, DEVICE_STATE_ACTIVE = 1.
        Check(((delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)Slot(enumerator, 3))(enumerator, 0, 1, &collection));
        try
        {
            uint count = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(collection, 3))(collection, &count));
            var ids = new List<string>((int)count);
            for (uint index = 0; index < count; index++)
            {
                nint device = 0, text = 0;
                try
                {
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(collection, 4))(collection, index, &device));
                    Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(device, 5))(device, &text));
                    if (Marshal.PtrToStringUni(text) is { Length: > 0 } id) ids.Add(id);
                }
                finally { Marshal.FreeCoTaskMem(text); Release(device); }
            }
            return ids;
        }
        finally { Release(collection); }
    }
    internal static nint OpenEndpoint(nint enumerator, string id)
    {
        nint device = 0;
        try
        {
            fixed (char* text = id)
                Check(((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)Slot(enumerator, 5))(enumerator, text, &device));
            return Activate(device);
        }
        finally { Release(device); }
    }
    private static nint Activate(nint device)
    {
        nint client = 0;
        var iid = AudioClientId;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Slot(device, 3))(device, &iid, 23, 0, &client));
        return client;
    }
    internal static void Initialize(nint client, bool loopback)
    {
        var format = new Format { Tag = 1, Channels = 1, Rate = 16000, Bytes = 32000, Align = 2, Bits = 16 };
        // Shared-mode conversion keeps capture packets in the engine's 16 kHz PCM16 format.
        uint flags = 0x80000000 | 0x08000000 | (loopback ? 0x00020000u : 0);
        // 500 ms of headroom absorbs brief capture-thread stalls without adding latency.
        Check(((delegate* unmanaged[Stdcall]<nint, int, uint, long, long, Format*, nint, int>)Slot(client, 3))(client, 0, flags, 5000000, 0, &format, 0));
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
    /// <summary>Copies one packet with its raw flags. The capture worker owns discontinuity and timestamp policy.</summary>
    internal static MeetingCapturePacket? Read(nint capture)
    {
        uint count = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(capture, 5))(capture, &count));
        if (count == 0) return null;
        nint data = 0;
        uint flags = 0;
        ulong position = 0, clock = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, uint*, uint*, ulong*, ulong*, int>)Slot(capture, 3))(capture, &data, &count, &flags, &position, &clock));
        var release = (delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(capture, 4);
        MeetingCapturePacket packet;
        try
        {
            if (count is 0 or > MeetingPacketTimeline.CapacitySamples)
                throw new IOException("Meeting capture returned an invalid native packet size.");
            var bytes = new byte[checked((int)count * 2)];
            if ((flags & MeetingCapturePacket.Silent) == 0) Marshal.Copy(data, bytes, 0, bytes.Length);
            packet = new(bytes, clock, position, flags);
        }
        catch
        {
            // The packet failure is the actionable error; a release failure here must not replace it.
            release(capture, count);
            throw;
        }
        Check(release(capture, count));
        return packet;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 2)] private struct Format
    {
        internal ushort Tag, Channels;
        internal uint Rate, Bytes;
        internal ushort Align, Bits, Extra;
    }
}
