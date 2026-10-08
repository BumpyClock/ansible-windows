using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Ansible.Core;

namespace Ansible;

// Clipboard paste: back up every clipboard format, offer the text through delayed rendering, press Ctrl+V,
// wait until the destination process reads the text, then restore the backup. Delayed rendering is the
// positive signal that the paste consumed the text; restoring earlier would paste the user's old clipboard.
internal static unsafe partial class WindowsTextTarget
{
    internal enum ClipboardFormatKind { Memory, EnhancedMetafile, Synthesized, Uncopyable }

    private const uint CfBitmap = 2, CfMetafilePicture = 3, CfDib = 8, CfPalette = 9, CfUnicodeText = 13,
        CfEnhancedMetafile = 14, CfDibV5 = 17, CfOwnerDisplay = 0x80, CfDisplayBitmap = 0x82,
        CfDisplayMetafilePicture = 0x83, CfDisplayEnhancedMetafile = 0x8E, CfPrivateFirst = 0x200, CfGdiObjectLast = 0x3FF;
    private const long MaxClipboardBackupBytes = 256L * 1024 * 1024;
    private const int ClipboardOpenTimeoutMs = 1000;
    // Guessed bound: long enough for a busy editor to process Ctrl+V, short enough to keep dictation responsive.
    private const int PasteReadTimeoutMs = 3000;
    private const int ClipboardRestoreTimeoutMs = 5000;
    private const uint WmRenderFormat = 0x0305, WmRenderAllFormats = 0x0306, WmDestroyClipboard = 0x0307;
    private const ushort VkControl = 0x11, VkV = 0x56;
    private const uint GlobalMoveable = 0x0002;
    private const string PasteWindowClass = "AnsibleClipboardPaste";
    private static readonly Lock s_classLock = new();
    private static bool s_classRegistered;

    // The paste window belongs to the inserting thread, so its window procedure reads this thread's session.
    [ThreadStatic] private static PasteSession? t_paste;

    private sealed class PasteSession(string text, uint[] readers)
    {
        public string Text { get; } = text;
        public uint[] Readers { get; } = readers;
        public List<ClipboardItem>? Backup { get; set; }
        /// <summary>The destination (or an unidentified reader) received the text.</summary>
        public bool Read { get; set; }
        /// <summary>The clipboard holds the offered text instead of the backup.</summary>
        public bool Replaced { get; set; }
        public bool Restoring { get; set; }
    }

    private sealed record ClipboardItem(uint Format, ClipboardFormatKind Kind, byte[] Data);

    /// <summary>
    /// Classifies a format for backup. Windows regenerates synthesized formats from a restored source format.
    /// Handle-based formats that the clipboard owner manages cannot be copied.
    /// </summary>
    internal static ClipboardFormatKind ClassifyClipboardFormat(uint format, bool hasDib, bool hasEnhancedMetafile) => format switch
    {
        CfBitmap or CfPalette => hasDib ? ClipboardFormatKind.Synthesized : ClipboardFormatKind.Uncopyable,
        CfMetafilePicture => hasEnhancedMetafile ? ClipboardFormatKind.Synthesized : ClipboardFormatKind.Uncopyable,
        CfEnhancedMetafile => ClipboardFormatKind.EnhancedMetafile,
        CfOwnerDisplay or CfDisplayBitmap or CfDisplayMetafilePicture or CfDisplayEnhancedMetafile => ClipboardFormatKind.Uncopyable,
        >= CfPrivateFirst and <= CfGdiObjectLast => ClipboardFormatKind.Uncopyable,
        _ => ClipboardFormatKind.Memory
    };

    private static TextDeliveryOutcome? Paste(string text, uint[] readers, Func<TextDeliveryOutcome?> recheck)
    {
        if (recheck() is { } early) { return early; }
        var window = CreatePasteWindow();
        var session = new PasteSession(text, readers);
        t_paste = session;
        TextDeliveryOutcome? outcome = null;
        var completed = false;
        try
        {
            outcome = PasteWithBackup(window, session, recheck);
            completed = true;
        }
        finally
        {
            var restoreProblem = session.Replaced ? RestoreClipboard(window, session) : null;
            t_paste = null;
            DestroyWindow(window);
            if (restoreProblem is not null)
            {
                Debug.WriteLine($"[WindowsTextTarget] {restoreProblem}");
                if (completed)
                    outcome = TextDeliveryOutcome.Rejected(outcome is { } failed
                        ? $"{failed.Message} Also, {restoreProblem}"
                        : $"The text was pasted, but {restoreProblem}");
            }
        }
        return outcome;
    }

    private static TextDeliveryOutcome? PasteWithBackup(nint window, PasteSession session, Func<TextDeliveryOutcome?> recheck)
    {
        if (!OpenClipboardWithin(window, ClipboardOpenTimeoutMs))
            return TextDeliveryOutcome.Rejected("Another app is using the clipboard, so the text was not pasted. Try again, or choose Type characters in Settings.");
        try
        {
            if (BackUpClipboard(out var backup) is { } problem)
                return TextDeliveryOutcome.Rejected($"The clipboard holds {problem}, which cannot be backed up, so the text was not pasted and the clipboard was not changed. Copy something else first, or choose Type characters in Settings.");
            session.Backup = backup;
            if (!EmptyClipboard())
                return TextDeliveryOutcome.Rejected($"Windows could not prepare the clipboard (error {Marshal.GetLastPInvokeError()}). The text was not pasted.");
            session.Replaced = true;
            OfferText();
        }
        finally { CloseClipboard(); }

        if (recheck() is { } veto) { return veto; }
        var keys = new[]
        {
            KeyboardEvent.Virtual(VkControl, 0), KeyboardEvent.Virtual(VkV, 0),
            KeyboardEvent.Virtual(VkV, KeyUp), KeyboardEvent.Virtual(VkControl, KeyUp)
        };
        var sent = SendInput((uint)keys.Length, keys, Marshal.SizeOf<KeyboardEvent>());
        if (sent != keys.Length)
        {
            var error = Marshal.GetLastPInvokeError();
            if (sent > 0) { _ = SendInput(2, [keys[2], keys[3]], Marshal.SizeOf<KeyboardEvent>()); }
            return TextDeliveryOutcome.Rejected($"Windows sent {sent} of {keys.Length} paste key events (error {error}). Check the field; your clipboard was restored.");
        }
        // Cancellation is not observed while waiting: restoring before the read would paste the old clipboard.
        var clock = Stopwatch.StartNew();
        while (!session.Read && session.Replaced && clock.ElapsedMilliseconds < PasteReadTimeoutMs)
            PumpMessages(PasteReadTimeoutMs - (int)clock.ElapsedMilliseconds);
        if (session.Read) { return null; }
        if (!session.Replaced)
            return TextDeliveryOutcome.Rejected("Another app replaced the clipboard before the paste finished. Check the field; the new clipboard content was kept.");
        return TextDeliveryOutcome.Rejected($"The app did not read the pasted text within {PasteReadTimeoutMs / 1000} seconds, so your clipboard was restored. If the app pastes later, it inserts the restored content. Choose Type characters in Settings for this app.");
    }

    // Requires the clipboard to be open. Returns a description of the first format that cannot be backed up.
    private static string? BackUpClipboard(out List<ClipboardItem> backup)
    {
        backup = [];
        var formats = new List<uint>();
        for (var format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format)) { formats.Add(format); }
        if (Marshal.GetLastPInvokeError() != 0) { return "content that Windows could not list"; }
        var hasDib = formats.Contains(CfDib) || formats.Contains(CfDibV5);
        var hasEnhancedMetafile = formats.Contains(CfEnhancedMetafile);
        long total = 0;
        foreach (var format in formats)
        {
            var kind = ClassifyClipboardFormat(format, hasDib, hasEnhancedMetafile);
            if (kind == ClipboardFormatKind.Synthesized) { continue; }
            if (kind == ClipboardFormatKind.Uncopyable) { return FormatName(format); }
            var handle = GetClipboardData(format);
            if (handle == 0) { return FormatName(format); }
            byte[] data;
            if (kind == ClipboardFormatKind.EnhancedMetafile)
            {
                var length = GetEnhMetaFileBits(handle, 0, null);
                if (length == 0 || total + length > MaxClipboardBackupBytes) { return FormatName(format); }
                data = new byte[length];
                fixed (byte* buffer = data)
                {
                    if (GetEnhMetaFileBits(handle, length, buffer) != length) { return FormatName(format); }
                }
            }
            else
            {
                var size = (long)GlobalSize(handle);
                if (total + size > MaxClipboardBackupBytes) { return "more than 256 MB of data"; }
                var source = GlobalLock(handle);
                if (source == 0) { return FormatName(format); }
                try
                {
                    data = new byte[size];
                    Marshal.Copy(source, data, 0, data.Length);
                }
                finally { GlobalUnlock(handle); }
            }
            total += data.Length;
            backup.Add(new ClipboardItem(format, kind, data));
        }
        return null;
    }

    // Requires the clipboard to be open and owned by the paste window after EmptyClipboard.
    private static void OfferText()
    {
        // Keep dictated text out of Windows clipboard history, cloud clipboard sync, and clipboard monitors.
        uint zero = 0;
        SetClipboardBytes(RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing"), new ReadOnlySpan<byte>(&zero, sizeof(uint)));
        SetClipboardBytes(RegisterClipboardFormatW("CanIncludeInClipboardHistory"), new ReadOnlySpan<byte>(&zero, sizeof(uint)));
        SetClipboardBytes(RegisterClipboardFormatW("CanUploadToCloudClipboard"), new ReadOnlySpan<byte>(&zero, sizeof(uint)));
        // A null handle offers the text by delayed rendering; WM_RENDERFORMAT reports who reads it.
        _ = SetClipboardData(CfUnicodeText, 0);
    }

    private static bool SetClipboardBytes(uint format, ReadOnlySpan<byte> data)
    {
        if (format == 0) { return false; }
        var handle = GlobalAlloc(GlobalMoveable, (nuint)data.Length);
        if (handle == 0) { return false; }
        var target = GlobalLock(handle);
        if (target == 0) { GlobalFree(handle); return false; }
        data.CopyTo(new Span<byte>((void*)target, data.Length));
        GlobalUnlock(handle);
        if (SetClipboardData(format, handle) != 0) { return true; }
        GlobalFree(handle);
        return false;
    }

    private static string? RestoreClipboard(nint window, PasteSession session)
    {
        var backup = session.Backup!;
        var clock = Stopwatch.StartNew();
        while (!OpenClipboard(window))
        {
            if (!session.Replaced) { return null; }
            if (clock.ElapsedMilliseconds >= ClipboardRestoreTimeoutMs)
                return "your previous clipboard content could not be restored because another app kept the clipboard open.";
            PumpMessages(15);
        }
        try
        {
            // Another app copied something after the paste; that newer content wins.
            if (!session.Replaced || GetClipboardOwner() != window) { return null; }
            session.Restoring = true;
            if (!EmptyClipboard())
                return $"Windows could not restore your previous clipboard content (error {Marshal.GetLastPInvokeError()}).";
            session.Replaced = false;
            var failed = 0;
            foreach (var item in backup)
            {
                if (!RestoreItem(item)) { failed++; }
            }
            return failed == 0 ? null : $"{failed} of {backup.Count} formats of your previous clipboard content could not be restored.";
        }
        finally
        {
            session.Restoring = false;
            CloseClipboard();
        }
    }

    private static bool RestoreItem(ClipboardItem item)
    {
        if (item.Kind == ClipboardFormatKind.Memory) { return SetClipboardBytes(item.Format, item.Data); }
        nint metafile;
        fixed (byte* data = item.Data) { metafile = SetEnhMetaFileBits((uint)item.Data.Length, data); }
        if (metafile == 0) { return false; }
        if (SetClipboardData(item.Format, metafile) != 0) { return true; }
        DeleteEnhMetaFile(metafile);
        return false;
    }

    private static string FormatName(uint format)
    {
        var buffer = stackalloc char[128];
        var length = GetClipboardFormatNameW(format, buffer, 128);
        return length > 0 ? $"\"{new string(buffer, 0, length)}\" data" : $"clipboard format {format}";
    }

    private static bool OpenClipboardWithin(nint window, int timeoutMilliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (!OpenClipboard(window))
        {
            if (clock.ElapsedMilliseconds >= timeoutMilliseconds) { return false; }
            PumpMessages(10);
        }
        return true;
    }

    // Waits for posted or sent messages and dispatches them; cross-thread WM_RENDERFORMAT arrives this way.
    private static void PumpMessages(int timeoutMilliseconds)
    {
        _ = MsgWaitForMultipleObjectsEx(0, null, (uint)Math.Max(0, timeoutMilliseconds), 0x04FF, 0x0004);
        Message message;
        while (PeekMessageW(&message, 0, 0, 0, 1))
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
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
    private static nint PasteWindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        var session = t_paste;
        switch (message)
        {
            case WmRenderFormat when session is not null && wParam == CfUnicodeText:
                // Render only for the destination, or a reader Windows cannot attribute. Other readers, such as
                // clipboard monitors, get no text and do not count as the paste.
                var reader = GetOpenClipboardWindow();
                uint process = 0;
                if (reader != 0) { _ = GetWindowThreadProcessId(reader, out process); }
                if (reader == 0 || session.Readers.Contains(process))
                {
                    var bytes = MemoryMarshal.AsBytes((session.Text + "\0").AsSpan());
                    if (SetClipboardBytes(CfUnicodeText, bytes)) { session.Read = true; }
                }
                return 0;
            case WmRenderAllFormats:
                // The window closes only after restore or failure; never leave dictated text behind.
                return 0;
            case WmDestroyClipboard when session is not null && !session.Restoring:
                session.Replaced = false;
                return 0;
            default:
                return DefWindowProcW(window, message, wParam, lParam);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background, MenuName, ClassName, SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
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
    private static partial uint EnumClipboardFormats(uint format);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetClipboardData(uint format);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetClipboardData(uint format, nint data);
    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardOwner();
    [LibraryImport("user32.dll")]
    private static partial nint GetOpenClipboardWindow();
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string name);
    [LibraryImport("user32.dll")]
    private static partial int GetClipboardFormatNameW(uint format, char* name, int length);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalAlloc(uint flags, nuint size);
    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint memory);
    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);
    [LibraryImport("kernel32.dll")]
    private static partial nuint GlobalSize(nint memory);
    [LibraryImport("gdi32.dll")]
    private static partial uint GetEnhMetaFileBits(nint metafile, uint size, byte* data);
    [LibraryImport("gdi32.dll")]
    private static partial nint SetEnhMetaFileBits(uint size, byte* data);
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteEnhMetaFile(nint metafile);
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
    [LibraryImport("user32.dll")]
    private static partial uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint milliseconds, uint wakeMask, uint flags);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(Message* message, nint window, uint filterMin, uint filterMax, uint remove);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(Message* message);
    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(Message* message);
}
