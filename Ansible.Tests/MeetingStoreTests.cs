using Ansible.Core;

namespace Ansible.Tests;

public sealed class MeetingStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Ansible-meetings-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveReloadRenameExportAndDeletePreserveSpeakerIdentity()
    {
        var store = new MeetingStore(_directory);
        var meeting = new MeetingDocument
        {
            Title = "Planning",
            Status = MeetingStatus.Completed,
            Turns = [new("speaker_0", 1, 3, "First point"), new("speaker_1", 4, 5, "Reply"), new("speaker_0", 3661, 3662, "Next point", true)]
        };
        await store.SaveAsync(meeting);
        await store.RenameAsync(meeting.Id, "Weekly planning");
        await store.RenameSpeakerAsync(meeting.Id, "speaker_0", "Alex");
        var loaded = await store.LoadAsync(meeting.Id);
        Assert.Equal("Weekly planning", loaded.Title);
        Assert.Equal("Alex", loaded.GetSpeakerName("speaker_0"));
        Assert.Equal("speaker_1", loaded.GetSpeakerName("speaker_1"));
        Assert.Equal(meeting.Turns, loaded.Turns);
        var exported = MeetingStore.ExportText(loaded);
        Assert.Contains("[00:00:01] Alex: First point", exported);
        Assert.Contains("[01:01:01] Alex (provisional): Next point", exported);
        Assert.Single(Directory.GetFiles(_directory));
        await store.DeleteAsync(meeting.Id);
        await store.DeleteAsync(meeting.Id);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task ListOrdersByMeetingDateAndDoesNotIncludeTemporaryFiles()
    {
        var store = new MeetingStore(_directory);
        var oldDate = DateTimeOffset.UtcNow.AddDays(-1);
        var old = new MeetingDocument { Title = "Older", CreatedAt = oldDate, UpdatedAt = oldDate };
        var recent = new MeetingDocument { Title = "Recent" };
        await store.SaveAsync(old);
        await store.SaveAsync(recent);
        await File.WriteAllTextAsync(Path.Combine(_directory, "unfinished.tmp"), "partial data");
        Assert.Equal(new[] { recent.Id, old.Id }, (await store.ListAsync()).Select(document => document.Id));
    }

    [Fact]
    public async Task InvalidReplacementLeavesSavedTranscriptIntact()
    {
        var store = new MeetingStore(_directory);
        var meeting = new MeetingDocument { Title = "Preserve me" };
        await store.SaveAsync(meeting);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(meeting with { Turns = [new("speaker", double.NaN, 2, "Invalid")] }));
        Assert.Equal("Preserve me", (await store.LoadAsync(meeting.Id)).Title);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task LoadRejectsIdentityMismatchAndUnknownSchema()
    {
        var store = new MeetingStore(_directory);
        var meeting = new MeetingDocument();
        await store.SaveAsync(meeting);
        var path = Directory.GetFiles(_directory).Single();
        var json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace(meeting.Id.ToString(), Guid.NewGuid().ToString()));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(meeting.Id));
        await File.WriteAllTextAsync(path, json.Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 99"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(meeting.Id));
    }

    [Fact]
    public async Task CanceledSaveDoesNotReplaceTranscript()
    {
        var store = new MeetingStore(_directory);
        var meeting = new MeetingDocument { Title = "Saved" };
        await store.SaveAsync(meeting);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(meeting with { Title = "Unsaved" }, canceled.Token));
        Assert.Equal("Saved", (await store.LoadAsync(meeting.Id)).Title);
    }

    [Fact]
    public async Task StartupRecoveryInterruptsOnlyUnfinishedMeetingsAndPreservesSpeech()
    {
        var store = new MeetingStore(_directory);
        var documents = Enum.GetValues<MeetingStatus>().Select(status => new MeetingDocument
        {
            Title = status.ToString(), Status = status, Turns = [new("Speaker 0", 0, 1, "Recovered speech", true)]
        }).ToArray();
        foreach (var document in documents) await store.SaveAsync(document);
        await store.MarkInterruptedSessionsAsync();
        await store.MarkInterruptedSessionsAsync();
        foreach (var original in documents)
        {
            var saved = await store.LoadAsync(original.Id);
            var unfinished = original.Status is MeetingStatus.Starting or MeetingStatus.Recording or MeetingStatus.Paused or MeetingStatus.Finishing;
            Assert.Equal(unfinished ? MeetingStatus.Interrupted : original.Status, saved.Status);
            Assert.Equal(original.Turns, saved.Turns);
            Assert.True(saved.UpdatedAt >= original.UpdatedAt);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
