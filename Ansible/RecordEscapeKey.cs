using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ansible;

/// <summary>Owns Escape only while a record attempt is pending, including recognition and delivery.</summary>
internal sealed partial class RecordEscapeKey : IDisposable
{
    [ThreadStatic] private static HashSet<RecordEscapeKey>? s_owners;
    private readonly HookCallback _callback;
    private readonly Action _cancel;
    private readonly uint _ownerThread;
    private nint _hook;
    private bool _down;

    internal RecordEscapeKey(Action cancel)
    {
        _cancel = cancel;
        _ownerThread = GetCurrentThreadId();
        _callback = OnInput;
        _hook = SetWindowsHookExW(13, Marshal.GetFunctionPointerForDelegate(_callback), GetModuleHandleW(null), 0);
        if (_hook == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "The record take could not register Escape cancellation.");
        (s_owners ??= []).Add(this);
    }

    private nint OnInput(int code, nuint message, nint data)
    {
        if (code >= 0)
        {
            var input = Marshal.PtrToStructure<KeyboardInput>(data);
            if (input.Key == 0x1B && (input.Flags & 0x12) == 0)
            {
                if (message is 0x0100 or 0x0104)
                {
                    if (!_down)
                    {
                        _down = true;
                        try { _cancel(); }
                        catch (Exception error) { Debug.WriteLine($"Record Escape cancellation failed: {error}"); }
                    }
                    return 1;
                }
                if (message is 0x0101 or 0x0105) { _down = false; return 1; }
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        if (GetCurrentThreadId() != _ownerThread)
            throw new InvalidOperationException("The Escape hook must be released on its owning message thread.");
        if (_hook == 0) return;
        if (!UnhookWindowsHookEx(_hook))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The record Escape hook could not be released.");
        _hook = 0;
        s_owners?.Remove(this);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint HookCallback(int code, nuint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public uint Key, ScanCode, Flags, Time;
        public nuint ExtraInfo;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowsHookExW(int hook, nint callback, nint module, uint thread);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hook);
    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? module);
    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
