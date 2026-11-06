using System.Runtime.InteropServices;
using Ansible.Core;

namespace Ansible;

internal interface ICaptureClipboard
{
    uint GetSequenceNumber();
    IReadOnlyList<string> ReadFileDrop();
}

/// <summary>Owns capture leases for native clipboard file references, including references left by a previous process.</summary>
internal sealed class CaptureClipboardProtection(CaptureStore store, ICaptureClipboard clipboard)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<CaptureLease> _leases = [];

    internal async Task RefreshAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        var next = new List<CaptureLease>();
        try
        {
            var sequence = clipboard.GetSequenceNumber();
            var files = await Task.Run(clipboard.ReadFileDrop).ConfigureAwait(false);
            var directories = files.Select(DirectTakeDirectory)
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in directories)
                next.Add(await store.ProtectAsync(directory).ConfigureAwait(false));
            // Never authorize cleanup with a partial or superseded view of the clipboard.
            if (sequence != clipboard.GetSequenceNumber())
                throw new IOException("The clipboard changed while capture files were inspected. Capture cleanup was deferred.");
            var old = _leases;
            _leases = next;
            next = old;
        }
        finally
        {
            foreach (var lease in next) await lease.DisposeAsync().ConfigureAwait(false);
            _gate.Release();
        }
    }

    internal async Task<CaptureLease?> ProtectPicturesAsync(IReadOnlyList<RecordFrame> frames) =>
        frames.Count > 0 && DirectTakeDirectory(frames[0].Path) is { } directory
            ? await store.ProtectAsync(directory).ConfigureAwait(false) : null;

    internal async Task RememberAsync(CaptureLease? lease)
    {
        if (lease is null) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var old in _leases) await old.DisposeAsync().ConfigureAwait(false);
            _leases = [lease];
        }
        finally { _gate.Release(); }
        // The publisher's sequence sample is not evidence of the current clipboard.
        // Refresh always inspects CF_HDROP again before any cleanup.
    }

    internal async Task PublishAsync(IReadOnlyList<RecordFrame> frames, Action<IReadOnlyList<RecordFrame>> publish)
    {
        var leases = new List<CaptureLease>();
        try
        {
            foreach (var directory in frames.Select(frame => DirectTakeDirectory(frame.Path))
                         .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                leases.Add(await store.ProtectAsync(directory).ConfigureAwait(false));
            await Task.Run(() => publish(frames)).ConfigureAwait(false);
        }
        finally
        {
            foreach (var lease in leases) await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private string? DirectTakeDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return null;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            return directory is not null && string.Equals(Path.GetDirectoryName(directory), store.Root,
                StringComparison.OrdinalIgnoreCase) ? directory : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

/// <summary>Reads only CF_HDROP paths, not file contents or unrelated formats. Sends no input.</summary>
internal sealed partial class WindowsCaptureClipboard : ICaptureClipboard
{
    private const uint FileDrop = 15;
    internal const int MaximumFiles = 256;
    internal const int MaximumPathCharacters = 32_767;
    internal const int MaximumTotalCharacters = 512 * 1024;

    public uint GetSequenceNumber()
    {
        var sequence = GetClipboardSequenceNumber();
        if (sequence == 0) throw new IOException("Windows could not inspect the clipboard. Capture cleanup was deferred.");
        return sequence;
    }

    public unsafe IReadOnlyList<string> ReadFileDrop()
    {
        if (!OpenClipboard(0))
            throw new IOException("The clipboard is busy. Capture cleanup was deferred.");
        try
        {
            if (!IsClipboardFormatAvailable(FileDrop)) return [];
            var drop = GetClipboardData(FileDrop);
            if (drop == 0) throw new IOException("Windows could not read clipboard file references. Capture cleanup was deferred.");
            var count = DragQueryFileW(drop, uint.MaxValue, null, 0);
            if (count > MaximumFiles)
                throw new IOException("The clipboard contains too many file references. Capture cleanup was deferred.");
            var paths = new List<string>((int)count);
            var totalCharacters = 0;
            for (uint index = 0; index < count; index++)
            {
                var length = DragQueryFileW(drop, index, null, 0);
                if (length == 0 || length > MaximumPathCharacters ||
                    length > MaximumTotalCharacters - totalCharacters)
                    throw new IOException("Clipboard file references exceed the inspection limit. Capture cleanup was deferred.");
                totalCharacters += (int)length;
                var buffer = new char[length + 1];
                fixed (char* characters = buffer)
                {
                    if (DragQueryFileW(drop, index, characters, (uint)buffer.Length) != length)
                        throw new IOException("Windows could not read a clipboard file reference. Capture cleanup was deferred.");
                }
                paths.Add(new string(buffer, 0, (int)length));
            }
            return paths;
        }
        finally
        {
            if (!CloseClipboard())
                throw new IOException("Windows could not close the clipboard. Capture cleanup was deferred.");
        }
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsClipboardFormatAvailable(uint format);
    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardData(uint format);
    [LibraryImport("shell32.dll")]
    private static unsafe partial uint DragQueryFileW(nint drop, uint index, char* path, uint characters);
}
