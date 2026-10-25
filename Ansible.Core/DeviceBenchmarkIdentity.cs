using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Ansible.Core;

// Owned by one engine and accessed under its operation gate. File identities are rechecked on every
// selection; only unchanged file contents reuse their hash. Nothing is trusted across engine lifetimes.
internal sealed class DeviceBenchmarkIdentity(
    Func<string>? hardwareIdentity = null,
    Func<FileStream, CancellationToken, string>? hashFile = null)
{
    private Dictionary<string, HashedFile> _files = new(StringComparer.OrdinalIgnoreCase);

    internal string Read(string library, string sample, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        library = Path.GetFullPath(library);
        sample = Path.GetFullPath(sample);
        var current = new Dictionary<string, HashedFile>(StringComparer.OrdinalIgnoreCase);
        var identity = new StringBuilder((hardwareIdentity ?? DeviceBenchmark.ReadHardwareIdentity)());
        identity.Append('|').Append(ReadHash(library)).Append('|').Append(ReadHash(sample));
        foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(library)!, "*.dll")
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(path, library, StringComparison.OrdinalIgnoreCase)) continue;
            identity.Append('|').Append(Path.GetFileName(path)).Append(':').Append(ReadHash(path));
        }
        token.ThrowIfCancellationRequested();
        _files = current;
        return identity.ToString();

        string ReadHash(string path)
        {
            token.ThrowIfCancellationRequested();
            if (current.TryGetValue(path, out var existing)) return existing.Hash;
            NativeModelIntegrity.RejectLinks(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.SequentialScan);
            var stamp = NativeModelIntegrity.ReadIdentity(stream);
            if (!_files.TryGetValue(path, out var cached) || cached.Identity != stamp)
            {
                var hash = (hashFile ?? HashFile)(stream, token);
                if (NativeModelIntegrity.ReadIdentity(stream) != stamp)
                    throw new IOException("The native runtime changed during identity verification.");
                cached = new(stamp, hash);
            }
            current.Add(path, cached);
            return cached.Hash;
        }
    }

    internal static string HashFile(FileStream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            int count;
            while ((count = stream.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, count);
            }
            token.ThrowIfCancellationRequested();
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private sealed record HashedFile(NativeModelIntegrity.FileIdentity Identity, string Hash);
}
