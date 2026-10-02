using System.Text.Json;

namespace DictationPoc.Core;

public sealed class UsageStore(string path) : IUsageStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public async Task<UsageDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path))
        {
            return new UsageDocument();
        }
        await using var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var document = await JsonSerializer.DeserializeAsync(file, UsageJsonContext.Default.UsageDocument, cancellationToken);
        if (document is null || document.SchemaVersion != 2 || document.Entries is null)
        {
            throw new InvalidDataException("The local usage file has an unsupported or invalid format.");
        }
        foreach (var entry in document.Entries)
        {
            if (entry is null) { throw new InvalidDataException("The local usage file contains an empty session."); }
            entry.Validate();
        }
        if (document.Entries.Select(entry => entry.Id).Distinct().Count() != document.Entries.Count)
        {
            throw new InvalidDataException("The local usage file contains duplicate session identifiers.");
        }
        return document;
    }

    public Task<UsageDocument> RecordAsync(UsageEntry entry, CancellationToken cancellationToken = default)
    {
        entry.Validate();
        return UpdateAsync(document =>
        {
            if (document.Enabled && document.Entries.All(existing => existing.Id != entry.Id))
            {
                return document with { Entries = document.Entries.Append(entry).ToArray() };
            }
            return document;
        }, cancellationToken);
    }

    public Task<UsageDocument> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        UpdateAsync(document => document with { Enabled = enabled }, cancellationToken);

    private async Task<UsageDocument> UpdateAsync(
        Func<UsageDocument, UsageDocument> update, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            using var lockTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lockTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using var lease = await AcquireLeaseAsync(lockTimeout.Token);
            var document = update(await LoadAsync(cancellationToken));
            temporaryPath = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, document, UsageJsonContext.Default.UsageDocument, cancellationToken);
                await file.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, Path, true);
            temporaryPath = null;
            return document;
        }
        finally
        {
            try
            {
                if (temporaryPath is not null && File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            finally { _gate.Release(); }
        }
    }

    private async Task<FileStream> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }
}
