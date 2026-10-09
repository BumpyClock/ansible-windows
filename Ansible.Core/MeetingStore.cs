using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ansible.Core;

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

    public async Task<IReadOnlyList<MeetingDocument>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        var documents = new List<MeetingDocument>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id))
                documents.Add(await LoadAsync(id, cancellationToken));
        }
        return documents.OrderByDescending(document => document.CreatedAt).ToArray();
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { File.Delete(GetPath(id)); }
        finally { _gate.Release(); }
    }

    public async Task MarkInterruptedSessionsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var document in await ListAsync(cancellationToken))
            {
                if (document.Status is MeetingStatus.Starting or MeetingStatus.Recording or MeetingStatus.Paused or MeetingStatus.Finishing)
                {
                    var now = DateTimeOffset.UtcNow;
                    await SaveCoreAsync(document with { Status = MeetingStatus.Interrupted,
                        UpdatedAt = now > document.UpdatedAt ? now : document.UpdatedAt }, cancellationToken);
                }
            }
        }
        finally { _gate.Release(); }
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
