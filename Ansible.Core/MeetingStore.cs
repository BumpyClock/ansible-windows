using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ansible.Core;

public sealed record MeetingReadFailure(Guid Id, string Message);

public sealed record MeetingListResult(IReadOnlyList<MeetingDocument> Documents, IReadOnlyList<MeetingReadFailure> Failures);

public sealed class MeetingStore(string directory)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    public async Task SaveAsync(MeetingDocument document, CancellationToken cancellationToken = default)
    {
        document.Validate();
        await _gate.WaitAsync(cancellationToken);
        try { await SaveCoreAsync(document, cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task<MeetingDocument> LoadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(GetPath(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var document = await JsonSerializer.DeserializeAsync(file, MeetingJsonContext.Default.MeetingDocument, cancellationToken)
            ?? throw new InvalidDataException("The meeting transcript is empty.");
        document.Validate();
        if (document.Id != id) throw new InvalidDataException("The meeting transcript identifier does not match its filename.");
        return document;
    }

    public async Task<MeetingListResult> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] paths;
        try { paths = Directory.GetFiles(DirectoryPath, "*.json"); }
        catch (DirectoryNotFoundException) { return new([], []); }
        var documents = new List<MeetingDocument>();
        var failures = new List<MeetingReadFailure>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)) continue;
            try { documents.Add(await LoadAsync(id, cancellationToken)); }
            catch (Exception error) when (error is InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
            {
                failures.Add(new(id, error is InvalidDataException or JsonException
                    ? "The saved meeting transcript has an unsupported or invalid format."
                    : "The saved meeting transcript could not be read."));
            }
        }
        return new(documents.OrderByDescending(document => document.CreatedAt).ToArray(), failures);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { File.Delete(GetPath(id)); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MeetingReadFailure>> MarkInterruptedSessionsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var result = await ListAsync(cancellationToken);
            foreach (var document in result.Documents)
            {
                if (document.Status is MeetingStatus.Starting or MeetingStatus.Recording or MeetingStatus.Paused or MeetingStatus.Finishing)
                {
                    var now = DateTimeOffset.UtcNow;
                    await SaveCoreAsync(document with { Status = MeetingStatus.Interrupted,
                        UpdatedAt = now > document.UpdatedAt ? now : document.UpdatedAt }, cancellationToken);
                }
            }
            return result.Failures;
        }
        finally { _gate.Release(); }
    }

    public async Task ExportAndRemoveAsync(Guid id, string destinationPath, CancellationToken cancellationToken = default)
    {
        var sourcePath = GetPath(id);
        var destination = Path.GetFullPath(destinationPath);
        var relative = Path.GetRelativePath(DirectoryPath, destination);
        if (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Choose an export location outside the app's meeting storage.", nameof(destinationPath));

        await _gate.WaitAsync(cancellationToken);
        var temporaryPath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await source.CopyToAsync(output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, destination, overwrite: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(sourcePath);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            finally { _gate.Release(); }
        }
    }

    public Task<MeetingDocument> RenameAsync(Guid id, string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return UpdateAsync(id, document => document with { Title = title.Trim() }, cancellationToken);
    }

    public Task<MeetingDocument> RenameSpeakerAsync(Guid id, string speakerId, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return UpdateAsync(id, document =>
        {
            if (!document.Turns.Any(turn => turn.SpeakerId == speakerId))
                throw new ArgumentException("The speaker does not appear in this meeting.", nameof(speakerId));
            var names = document.SpeakerNames.ToDictionary(pair => pair.Key, pair => pair.Value);
            names[speakerId] = name.Trim();
            return document with { SpeakerNames = names };
        }, cancellationToken);
    }

    public Task<MeetingDocument> ApplyCleanupAsync(Guid id, IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, document =>
        {
            ArgumentNullException.ThrowIfNull(texts);
            if (document.Status != MeetingStatus.Completed || document.Turns.Any(turn => turn.IsProvisional))
                throw new InvalidOperationException("End the meeting and finish transcription before cleaning the transcript.");
            if (texts.Count != document.Turns.Count || texts.Any(text => text is null || text.Contains('\0')) ||
                texts.Where((text, index) => !string.IsNullOrWhiteSpace(document.Turns[index].Text)).Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Cleanup must contain one text result for every original turn.", nameof(texts));
            return document with { Turns = document.Turns.Select((turn, index) => turn with
                { OriginalText = turn.OriginalText ?? turn.Text, Text = texts[index] }).ToArray() };
        }, cancellationToken);

    public Task<MeetingDocument> RevertCleanupAsync(Guid id, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, document => document with { Turns = document.Turns.Select(turn => turn.OriginalText is { } original
            ? turn with { Text = original, OriginalText = null } : turn).ToArray() }, cancellationToken);

    public static string ExportText(MeetingDocument document)
    {
        document.Validate();
        var text = new StringBuilder().AppendLine(document.Title).AppendLine(document.CreatedAt.ToString("u", CultureInfo.InvariantCulture)).AppendLine();
        foreach (var turn in document.Turns)
        {
            var timestamp = TimeSpan.FromSeconds(turn.StartSeconds);
            text.Append('[').Append(((int)timestamp.TotalHours).ToString("00", CultureInfo.InvariantCulture))
                .Append(':').Append(timestamp.Minutes.ToString("00", CultureInfo.InvariantCulture))
                .Append(':').Append(timestamp.Seconds.ToString("00", CultureInfo.InvariantCulture)).Append("] ")
                .Append(document.GetSpeakerName(turn.SpeakerId)).Append(turn.IsProvisional ? " (provisional): " : ": ")
                .AppendLine(turn.Text);
        }
        return text.ToString();
    }

    private async Task<MeetingDocument> UpdateAsync(Guid id, Func<MeetingDocument, MeetingDocument> update, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = update(await LoadAsync(id, cancellationToken)) with { UpdatedAt = DateTimeOffset.UtcNow };
            document.Validate();
            await SaveCoreAsync(document, cancellationToken);
            return document;
        }
        finally { _gate.Release(); }
    }

    private async Task SaveCoreAsync(MeetingDocument document, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = GetPath(document.Id);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, document, MeetingJsonContext.Default.MeetingDocument, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private string GetPath(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A meeting identifier is required.", nameof(id));
        return Path.Combine(DirectoryPath, id.ToString("N") + ".json");
    }
}
