using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

internal static unsafe partial class Clipboard
{
    internal static readonly string[] Privacy = ["ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard"];
    internal const uint UnicodeText = 13, FileDrop = 15, DibV5 = 17;
    internal static uint Format(string name) => Check(RegisterClipboardFormatW(name), "register clipboard format");
    internal static byte[] Text(string text) => Encoding.Unicode.GetBytes(text + '\0');
    internal static byte[] Files(string[] paths)
    {
        var names = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var drop = new byte[20 + names.Length];
        BinaryPrimitives.WriteInt32LittleEndian(drop, 20);
        BinaryPrimitives.WriteInt32LittleEndian(drop.AsSpan(16), 1);
        names.CopyTo(drop, 20);
        return drop;
    }

    internal static void Publish(Dictionary<uint, byte[]> payload)
    {
        var publication = Privacy.ToDictionary(Format, _ => new byte[4]);
        foreach (var pair in payload) publication.Add(pair.Key, pair.Value);
        nint owner = CreateWindowExW(0, "STATIC", "Ansible clipboard spike", 0, 0, 0, 0, 0, -3, 0, 0, 0);
        if (owner == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot create clipboard owner.");
        try
        {
            Open(owner);
            try
            {
                if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot empty clipboard.");
                foreach (var (format, bytes) in publication)
                {
                    nint memory = GlobalAlloc(2, (nuint)bytes.Length);
                    if (memory == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                    try
                    {
                        nint pointer = GlobalLock(memory);
                        if (pointer == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                        try { bytes.CopyTo(new Span<byte>((void*)pointer, bytes.Length)); }
                        finally { GlobalUnlock(memory); }
                        if (SetClipboardData(format, memory) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Clipboard publication failed. No input sent.");
                        memory = 0;
                    }
                    finally { if (memory != 0) GlobalFree(memory); }
                }
            }
            finally { if (!CloseClipboard()) throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        }
        finally { DestroyWindow(owner); }
        // Fully rendered data stays available. Never restore earlier clipboard content.
    }

    internal static void Verify(Dictionary<uint, byte[]> expected, string[]? files = null)
    {
        foreach (string name in Privacy) expected.TryAdd(Format(name), new byte[4]);
        Open(0);
        try
        {
            foreach (var (format, bytes) in expected)
            {
                nint data = GetClipboardData(format);
                if (data == 0 || GlobalSize(data) < (nuint)bytes.Length) throw new InvalidOperationException($"Missing clipboard format {format}.");
                nint pointer = GlobalLock(data);
                if (pointer == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                try
                {
                    if (!new ReadOnlySpan<byte>((void*)pointer, bytes.Length).SequenceEqual(bytes))
                        throw new InvalidOperationException($"Clipboard bytes differ for {format}.");
                }
                finally { GlobalUnlock(data); }
            }
            if (files is not null)
            {
                nint data = GetClipboardData(FileDrop);
                if (DragQueryFileW(data, uint.MaxValue, null, 0) != files.Length) throw new InvalidOperationException("Wrong file count.");
                char* name = stackalloc char[1024];
                for (uint i = 0; i < files.Length; i++)
                {
                    uint length = DragQueryFileW(data, i, name, 1024);
                    if (new string(name, 0, (int)length) != files[i]) throw new InvalidOperationException("Wrong file path.");
                }
            }
        }
        finally { CloseClipboard(); }
    }

    private static void Open(nint owner)
    {
        var timer = Stopwatch.StartNew();
        while (!OpenClipboard(owner))
        {
            if (timer.ElapsedMilliseconds >= 1000) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Clipboard busy.");
            Thread.Sleep(10);
        }
    }

    internal static uint Sequence => GetClipboardSequenceNumber();
    internal static nint Foreground => GetForegroundWindow();
    internal static void Paste(nint target, uint sequence)
    {
        if (target == 0 || Foreground != target || Sequence != sequence)
            throw new InvalidOperationException("Focus or clipboard changed. No paste was sent.");
        for (int key = 1; key < 256; key++)
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
                throw new InvalidOperationException("Release all keys and mouse buttons before paste.");
        Input* inputs = stackalloc Input[4];
        inputs[0] = new() { Type = 1, Key = 0x11 };
        inputs[1] = new() { Type = 1, Key = 0x56 };
        inputs[2] = new() { Type = 1, Key = 0x56, Flags = 2 };
        inputs[3] = new() { Type = 1, Key = 0x11, Flags = 2 };
        uint sent = SendInput(4, inputs, sizeof(Input));
        if (sent != 4)
        {
            // Release only keys whose keydowns were successfully queued. Do not replay paste.
            if (sent >= 2) { Input up = new() { Type = 1, Key = 0x56, Flags = 2 }; SendInput(1, &up, sizeof(Input)); }
            if (sent >= 1) { Input up = new() { Type = 1, Key = 0x11, Flags = 2 }; SendInput(1, &up, sizeof(Input)); }
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Only {sent}/4 input events queued. Check target manually.");
        }
    }

    private static uint Check(uint value, string operation) => value != 0 ? value : throw new Win32Exception(Marshal.GetLastPInvokeError(), operation);
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)] private static partial nint CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyWindow(nint window);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenClipboard(nint owner);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseClipboard();
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EmptyClipboard();
    [LibraryImport("user32.dll", SetLastError = true)] private static partial nint SetClipboardData(uint format, nint data);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial nint GetClipboardData(uint format);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)] private static partial uint RegisterClipboardFormatW(string name);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GlobalUnlock(nint memory);
    [LibraryImport("kernel32.dll")] private static partial nint GlobalFree(nint memory);
    [LibraryImport("kernel32.dll")] private static partial nuint GlobalSize(nint memory);
    [LibraryImport("shell32.dll")] private static partial uint DragQueryFileW(nint data, uint index, char* path, uint length);
    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial uint GetClipboardSequenceNumber();
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int key);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial uint SendInput(uint count, Input* inputs, int size);
}
