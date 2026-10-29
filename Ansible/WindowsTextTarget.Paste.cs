using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Ansible.Core;

namespace Ansible;

internal static unsafe partial class WindowsTextTarget
{
    private const uint CfUnicodeText = 13;
    private const uint GlobalMoveable = 0x0002;
    private const ushort VkControl = 0x11, VkV = 0x56;
    private const string PasteWindowClass = "AnsibleClipboardPaste";
    private static readonly Lock s_classLock = new();
    private static bool s_classRegistered;

    private static TextDeliveryOutcome? Paste(string text, PasteShortcut shortcut,
        CancellationToken cancellationToken, Func<bool> stillCurrent,
        Func<TextDeliveryOutcome?> recheck, Func<TextDeliveryOutcome?> prepareSend)
    {
        var window = CreatePasteWindow();
        uint sequence = 0;
        try
        {
            return PasteWithClipboard(
                () =>
                {
                    var failure = PublishClipboardText(window, text);
                    sequence = GetClipboardSequenceNumber();
                    return failure;
                },
                recheck,
                () =>
                {
                    if (prepareSend() is { } veto) { return veto; }
                    if (GetClipboardOwner() != window || GetClipboardSequenceNumber() != sequence)
                        return TextDeliveryOutcome.Rejected("Another app changed the clipboard before insertion. No paste shortcut was sent. Use Copy transcript instead.", TextDeliveryReason.ClipboardChanged);
                    return SendPasteChord(shortcut, NativeSend, Thread.Sleep,
                        () => cancellationToken.IsCancellationRequested || !stillCurrent());
                });
        }
        finally { DestroyWindow(window); }
    }

    internal static TextDeliveryOutcome? PasteWithClipboard(
        Func<TextDeliveryOutcome?> publish, Func<TextDeliveryOutcome?> recheck,
        Func<TextDeliveryOutcome?> send)
    {
        if (recheck() is { } early) { return early; }
        if (publish() is { } failure) { return failure; }
        if (recheck() is { } veto) { return veto; }
        // SendInput only queues the shortcut. No render callback or timer proves that the editor consumed it.
        // Leave the text available for asynchronous readers instead of restoring unrelated clipboard content.
        return send();
    }

    private static TextDeliveryOutcome? PublishClipboardText(nint window, string text)
    {
        var formats = new[]
        {
            RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing"),
            RegisterClipboardFormatW("CanIncludeInClipboardHistory"),
            RegisterClipboardFormatW("CanUploadToCloudClipboard"), CfUnicodeText
        };
        var handles = new nint[formats.Length];
        try
        {
            uint zero = 0;
            for (var index = 0; index < formats.Length; index++)
            {
                if (formats[index] == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not register a clipboard privacy format.");
                handles[index] = AllocateClipboardBytes(index == formats.Length - 1
                    ? MemoryMarshal.AsBytes((text + "\0").AsSpan())
                    : new ReadOnlySpan<byte>(&zero, sizeof(uint)));
            }
            var clock = Stopwatch.StartNew();
            while (!OpenClipboard(window))
            {
                if (clock.ElapsedMilliseconds >= 1000)
                    return TextDeliveryOutcome.Rejected("Another app is using the clipboard. No paste shortcut was sent. Try again, or choose Type characters in Settings.", TextDeliveryReason.ClipboardBusy);
                Thread.Sleep(10);
            }
            TextDeliveryOutcome? failure = null;
            try
            {
                if (!EmptyClipboard())
                    failure = TextDeliveryOutcome.Rejected($"Windows could not prepare the clipboard (error {Marshal.GetLastPInvokeError()}). No paste shortcut was sent.", TextDeliveryReason.ClipboardPreparationFailed);
                else
                {
                    for (var index = 0; index < formats.Length; index++)
                    {
                        if (SetClipboardData(formats[index], handles[index]) == 0)
                        {
                            failure = TextDeliveryOutcome.Rejected($"Windows could not publish the dictated text and clipboard privacy formats (error {Marshal.GetLastPInvokeError()}). The previous clipboard content was replaced, but no paste shortcut was sent. Use Copy transcript instead.", TextDeliveryReason.ClipboardPublishFailed);
                            break;
                        }
                        handles[index] = 0;
                    }
                }
            }
            finally
            {
                if (!CloseClipboard())
                    failure = TextDeliveryOutcome.Rejected($"Windows could not close the clipboard (error {Marshal.GetLastPInvokeError()}). No paste shortcut was sent. Use Copy transcript instead.", TextDeliveryReason.ClipboardCloseFailed);
            }
            return failure;
        }
        finally
        {
            foreach (var handle in handles)
                if (handle != 0) { GlobalFree(handle); }
        }
    }

    private static nint AllocateClipboardBytes(ReadOnlySpan<byte> data)
    {
        var handle = GlobalAlloc(GlobalMoveable, (nuint)data.Length);
        if (handle == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not allocate clipboard memory.");
        var target = GlobalLock(handle);
        if (target == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            GlobalFree(handle);
            throw new Win32Exception(error, "Windows could not lock clipboard memory.");
        }
        data.CopyTo(new Span<byte>((void*)target, data.Length));
        GlobalUnlock(handle);
        return handle;
    }

    private static nint CreatePasteWindow()
    {
        var instance = GetModuleHandleW(null);
        fixed (char* name = PasteWindowClass)
        {
            lock (s_classLock)
            {
                if (!s_classRegistered)
                {
                    var windowClass = new WindowClass
                    {
                        Size = (uint)sizeof(WindowClass),
                        Procedure = (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&PasteWindowProcedure,
                        Instance = instance,
                        ClassName = (nint)name
                    };
                    if (RegisterClassExW(&windowClass) == 0 && Marshal.GetLastPInvokeError() != 1410)
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not register the clipboard paste window.");
                    s_classRegistered = true;
                }
            }
            var window = CreateWindowExW(0, name, null, 0, 0, 0, 0, 0, -3, 0, instance, 0);
            if (window == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not create the clipboard paste window.");
            return window;
        }
    }

    [UnmanagedCallersOnly]
    private static nint PasteWindowProcedure(nint window, uint message, nuint wParam, nint lParam) =>
        DefWindowProcW(window, message, wParam, lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background, MenuName, ClassName, SmallIcon;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetClipboardData(uint format, nint data);
    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardOwner();
    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();
    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string name);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalAlloc(uint flags, nuint size);
    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint memory);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassExW(WindowClass* windowClass);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreateWindowExW(uint extendedStyle, char* className, char* windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);
    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);
}
