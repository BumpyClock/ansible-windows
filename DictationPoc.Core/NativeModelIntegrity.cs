using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace DictationPoc.Core;

internal sealed partial class NativeModelIntegrity
{
    private readonly Dictionary<string, VerifiedIdentity> _cache = new(StringComparer.OrdinalIgnoreCase);

    public FileStream OpenVerified(string path, NativeModelEntry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        RejectLinks(path);
        // Keep the read lease through native loading and residency, excluding writes and replacement.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        try
        {
            var identity = ReadIdentity(stream);
            if (identity.Bytes != entry.Bytes)
                throw new InvalidDataException($"Model '{entry.Id}' has an unexpected size. Re-run the verified model setup.");
            if (!_cache.TryGetValue(path, out var cached) ||
                cached.Identity != identity || !string.Equals(cached.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _cache.Remove(path);
                Verify(stream, entry, token);
                if (ReadIdentity(stream) != identity)
                    throw new IOException("The model changed during integrity verification.");
                _cache[path] = new VerifiedIdentity(identity, entry.Sha256);
            }
            token.ThrowIfCancellationRequested();
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static void Verify(FileStream stream, NativeModelEntry entry, CancellationToken token)
    {
        if (stream.Length != entry.Bytes)
            throw new InvalidDataException($"Model '{entry.Id}' has an unexpected size.");
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            int count;
            while ((count = stream.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, count);
            }
            token.ThrowIfCancellationRequested();
            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Model '{entry.Id}' failed SHA-256 verification.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    internal static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Native models must be ordinary files in a directory without reparse points.");
        }
    }

    private static FileIdentity ReadIdentity(FileStream stream)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Model identity verification requires Windows file identity.");
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect native model identity.");
        return new FileIdentity(info.Volume, info.IndexHigh, info.IndexLow,
            ((long)info.SizeHigh << 32) | info.SizeLow,
            ((long)info.WriteTimeHigh << 32) | info.WriteTimeLow,
            ((long)info.CreationTimeHigh << 32) | info.CreationTimeLow);
    }

    private sealed record VerifiedIdentity(FileIdentity Identity, string Sha256);
    private readonly record struct FileIdentity(
        uint Volume, uint IndexHigh, uint IndexLow, long Bytes, long WriteTime, long CreationTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public uint CreationTimeLow, CreationTimeHigh;
        public uint AccessTimeLow, AccessTimeHigh;
        public uint WriteTimeLow, WriteTimeHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
