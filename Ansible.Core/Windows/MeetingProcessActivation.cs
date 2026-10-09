using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ansible.Core.Windows;

internal sealed unsafe class MeetingProcessActivation
{
    private readonly TaskCompletionSource<nint> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _references = 1;
    private nint _instance;
    private GCHandle _handle;
    private static readonly nint Vtable = MakeVtable();
    private static nint MakeVtable()
    {
        var table = (nint*)System.Runtime.InteropServices.NativeMemory.Alloc(4, (nuint)sizeof(nint));
        table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&Query;
        table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
        table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&Completed;
        return (nint)table;
    }
    private MeetingProcessActivation()
    {
        _handle = GCHandle.Alloc(this);
        _instance = (nint)System.Runtime.InteropServices.NativeMemory.Alloc(2, (nuint)sizeof(nint));
        ((nint*)_instance)[0] = Vtable;
        ((nint*)_instance)[1] = GCHandle.ToIntPtr(_handle);
    }
    internal static nint Open(int processId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            throw new PlatformNotSupportedException("Meeting-app audio requires Windows build 20348 or newer. Select System audio explicitly instead.");
        var handler = new MeetingProcessActivation();
        nint operation = 0;
        try
        {
            var activation = new Activation { Type = 1, ProcessId = (uint)processId, Mode = 0 };
            var variant = new Variant { Type = 65, Length = (uint)sizeof(Activation), Data = (nint)(&activation) };
            MeetingWasapi.Check(MeetingWasapi.ActivateAudioInterfaceAsync("VAD\\Process_Loopback", in MeetingWasapi.AudioClientId, (nint)(&variant), handler._instance, out operation));
            // Keep ownership until activation completes: abandoning the callback could leak a returned client.
            return handler._completion.Task.GetAwaiter().GetResult();
        }
        finally { MeetingWasapi.Release(operation); handler.ReleaseReference(); }
    }
    private static MeetingProcessActivation Get(nint self) => (MeetingProcessActivation)GCHandle.FromIntPtr(((nint*)self)[1]).Target!;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Query(nint self, Guid* iid, nint* result)
    {
        *result = 0;
        if (*iid != new Guid("00000000-0000-0000-C000-000000000046") &&
            *iid != new Guid("41D949AB-9862-444A-80F6-C261334DA5EB") &&
            *iid != new Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")) return unchecked((int)0x80004002);
        Interlocked.Increment(ref Get(self)._references);
        *result = self;
        return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(nint self) => (uint)Interlocked.Increment(ref Get(self)._references);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(nint self) => Get(self).ReleaseReference();
    private uint ReleaseReference()
    {
        var remaining = Interlocked.Decrement(ref _references);
        if (remaining == 0) { System.Runtime.InteropServices.NativeMemory.Free((void*)_instance); _handle.Free(); }
        return (uint)remaining;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Completed(nint self, nint operation)
    {
        var handler = Get(self);
        nint client = 0;
        try
        {
            int result = 0;
            MeetingWasapi.Check(((delegate* unmanaged[Stdcall]<nint, int*, nint*, int>)MeetingWasapi.Slot(operation, 3))(operation, &result, &client));
            MeetingWasapi.Check(result);
            handler._completion.TrySetResult(client);
        }
        catch (Exception error) { MeetingWasapi.Release(client); handler._completion.TrySetException(error); }
        return 0;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Activation { internal uint Type, ProcessId, Mode; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct Variant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal uint Length;
        [FieldOffset(16)] internal nint Data;
    }
}

