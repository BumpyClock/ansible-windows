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
        Assert.Empty((await store.ListAsync()).Documents);
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
        Assert.Equal(new[] { recent.Id, old.Id }, (await store.ListAsync()).Documents.Select(document => document.Id));
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
    public async Task LoadAndRecoverMeetingSavedBeforeAutomaticSpeakerNames()
    {
        var store = new MeetingStore(_directory);
        var id = Guid.NewGuid();
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, id.ToString("N") + ".json"), $$"""
            {
              "SchemaVersion": 1,
              "Id": "{{id}}",
              "Title": "Earlier meeting",
              "CreatedAt": "2026-10-09T14:58:30+00:00",
              "UpdatedAt": "2026-10-09T14:58:55+00:00",
              "Status": 5,
              "Turns": [{ "SpeakerId": "speaker_0", "StartSeconds": 0, "EndSeconds": 2, "Text": "Saved speech", "IsProvisional": false }],
              "SpeakerNames": { "speaker_0": "Alex" }
            }
            """);

        var loaded = await store.LoadAsync(id);
        Assert.Empty(loaded.AutoSpeakerNames);
        Assert.Equal("Alex", loaded.GetSpeakerName("speaker_0"));
        Assert.Equal("Saved speech", Assert.Single(loaded.Turns).Text);
        await store.MarkInterruptedSessionsAsync();
        var recovered = Assert.Single((await store.ListAsync()).Documents);
        Assert.Equal(MeetingStatus.Interrupted, recovered.Status);
        Assert.Equal(loaded.Turns, recovered.Turns);
        Assert.Equal("Alex", recovered.GetSpeakerName("speaker_0"));
        Assert.Empty(recovered.AutoSpeakerNames);
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

    [Fact]
    public async Task InvalidMeetingsDoNotHideHealthyMeetingsOrPreventStartupRecovery()
    {
        var store = new MeetingStore(_directory);
        var healthy = new MeetingDocument { Status = MeetingStatus.Recording, Turns = [new("speaker_0", 0, 1, "Keep this speech")] };
        await store.SaveAsync(healthy);
        var invalidId = Guid.NewGuid();
        var malformedId = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(_directory, invalidId.ToString("N") + ".json"), "{\"SchemaVersion\":99}");
        await File.WriteAllTextAsync(Path.Combine(_directory, malformedId.ToString("N") + ".json"), "private speech: malformed JSON");

        var listed = await store.ListAsync();
        Assert.Equal(healthy.Id, Assert.Single(listed.Documents).Id);
        Assert.Equal(new[] { invalidId, malformedId }.Order(), listed.Failures.Select(failure => failure.Id).Order());
        Assert.All(listed.Failures, failure => Assert.DoesNotContain("private speech", failure.Message));
        var failures = await store.MarkInterruptedSessionsAsync();
        Assert.Equal(2, failures.Count);
        var recovered = await store.LoadAsync(healthy.Id);
        Assert.Equal(MeetingStatus.Interrupted, recovered.Status);
        Assert.Equal(healthy.Turns, recovered.Turns);
        Assert.Equal(3, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public async Task LockedMeetingIsReportedWhileHealthyMeetingRemainsAvailable()
    {
        var store = new MeetingStore(_directory);
        var healthy = new MeetingDocument();
        var locked = new MeetingDocument();
        await store.SaveAsync(healthy);
        await store.SaveAsync(locked);
        using var handle = new FileStream(Path.Combine(_directory, locked.Id.ToString("N") + ".json"), FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await store.ListAsync();
        Assert.Equal(healthy.Id, Assert.Single(result.Documents).Id);
        Assert.Equal(locked.Id, Assert.Single(result.Failures).Id);
    }

    [Fact]
    public async Task ExportAndRemovePreservesExactMalformedBytesAndReplacesChosenDestination()
    {
        var store = new MeetingStore(_directory);
        var id = Guid.NewGuid();
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(_directory + "-exports");
        var source = Path.Combine(_directory, id.ToString("N") + ".json");
        var destination = Path.Combine(_directory + "-exports", "recovered.json");
        byte[] original = [0xff, 0xfe, 0, 123, 10, 34, 65];
        await File.WriteAllBytesAsync(source, original);
        await File.WriteAllTextAsync(destination, "replace this file");

        await store.ExportAndRemoveAsync(id, destination);

        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(source));
        Assert.Single(Directory.GetFiles(_directory + "-exports"));
        Assert.Empty((await store.ListAsync()).Failures);
    }

    [Fact]
    public async Task FailedExportPreservesSourceAndRemovesTemporaryCopy()
    {
        var store = new MeetingStore(_directory);
        var meeting = new MeetingDocument();
        await store.SaveAsync(meeting);
        var source = Path.Combine(_directory, meeting.Id.ToString("N") + ".json");
        var original = await File.ReadAllBytesAsync(source);
        var destination = Path.Combine(_directory + "-exports", "occupied.json");
        Directory.CreateDirectory(destination);

        var error = await Record.ExceptionAsync(() => store.ExportAndRemoveAsync(meeting.Id, destination));
        Assert.True(error is IOException or UnauthorizedAccessException);

        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        Assert.Empty(Directory.GetFiles(_directory + "-exports"));
    }

    [Fact]
    public async Task CanceledExportPreservesSourceAndDestination()
    {
        var store = new MeetingStore(_directory);
        var meeting = new MeetingDocument();
        await store.SaveAsync(meeting);
        Directory.CreateDirectory(_directory + "-exports");
        var destination = Path.Combine(_directory + "-exports", "existing.json");
        await File.WriteAllTextAsync(destination, "existing export");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ExportAndRemoveAsync(meeting.Id, destination, canceled.Token));

        Assert.Equal(meeting.Id, (await store.LoadAsync(meeting.Id)).Id);
        Assert.Equal("existing export", await File.ReadAllTextAsync(destination));
        Assert.Single(Directory.GetFiles(_directory + "-exports"));
    }

    [Theory]
    [InlineData("same")]
    [InlineData("sibling")]
    [InlineData("nested")]
    [InlineData("relative")]
    public async Task ExportRejectsEveryDestinationInsideMeetingStorage(string location)
    {
        var store = new MeetingStore(_directory);
        var meeting = new MeetingDocument();
        await store.SaveAsync(meeting);
        var destination = location switch
        {
            "same" => Path.Combine(_directory, meeting.Id.ToString("N") + ".json"),
            "sibling" => Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json"),
            "nested" => Path.Combine(_directory, "exports", "copy.json"),
            _ => Path.Combine(_directory, "exports", "..", "copy.json")
        };
        await Assert.ThrowsAsync<ArgumentException>(() => store.ExportAndRemoveAsync(meeting.Id, destination));
        Assert.Equal(meeting.Id, (await store.LoadAsync(meeting.Id)).Id);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task CleanupAndRevertPreserveOriginalWordsSpeakerAndTiming()
    {
        var store = new MeetingStore(_directory);
        var meeting = new MeetingDocument { Status = MeetingStatus.Completed, Turns =
            [new("Speaker 0", 1, 5, "raw words") { Source = MeetingAudioSource.Playback, TimingQuality = MeetingTimingQuality.Aligned }] };
        await store.SaveAsync(meeting);
        var first = await store.ApplyCleanupAsync(meeting.Id, ["Raw words."]);
        var second = await store.ApplyCleanupAsync(meeting.Id, ["Raw words!"]);
        Assert.Equal("raw words", first.Turns[0].OriginalText);
        Assert.Equal("raw words", second.Turns[0].OriginalText);
        Assert.Equal(meeting.Turns[0] with { Text = "Raw words!", OriginalText = "raw words" }, second.Turns[0]);
        var reverted = await store.RevertCleanupAsync(meeting.Id);
        Assert.Equal(meeting.Turns, reverted.Turns);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyCleanupAsync(meeting.Id, []));
        Assert.Equal(meeting.Turns, (await store.LoadAsync(meeting.Id)).Turns);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        if (Directory.Exists(_directory + "-exports")) Directory.Delete(_directory + "-exports", recursive: true);
    }
}
