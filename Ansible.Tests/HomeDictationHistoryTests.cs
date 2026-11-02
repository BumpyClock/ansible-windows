using Ansible.Core;

namespace Ansible;

public sealed class HomeDictationHistoryTests : IDisposable
{
    private readonly HomeDictationHistory _history = new();

    public void Dispose() => _history.Dispose();

    [Fact]
    public async Task HistoryDiagnosticsNeverContainTranscriptOrPath()
    {
        var path = TemporaryPath();
        var directory = Path.GetDirectoryName(path)!;
        var log = new LocalDiagnosticLog(Path.Combine(directory, "diagnostics"));
        log.SetEnabled(true);
        try
        {
            await using (var history = new HomeDictationHistory(path, log))
            {
                await history.InitializeAsync();
                Assert.True(history.Add(Completion(1, "PRIVATE-HISTORY-TEXT")));
            }
            await log.DisposeAsync();
            var text = await File.ReadAllTextAsync(Path.Combine(log.DirectoryPath, "local-diagnostics.jsonl"));
            Assert.DoesNotContain("PRIVATE-HISTORY-TEXT", text);
            Assert.DoesNotContain(directory, text);
            var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            var entries = lines.Select(line => System.Text.Json.JsonDocument.Parse(line)).ToArray();
            try
            {
                Assert.Contains(entries, entry => entry.RootElement.GetProperty("Family").GetString() == "Storage" &&
                    entry.RootElement.GetProperty("Category").GetString() == "History" &&
                    entry.RootElement.GetProperty("Stage").GetString() == "Load");
                Assert.Contains(entries, entry => entry.RootElement.GetProperty("Category").GetString() == "History" &&
                    entry.RootElement.GetProperty("Stage").GetString() == "Save" &&
                    entry.RootElement.GetProperty("Outcome").GetString() == "Completed");
            }
            finally { foreach (var entry in entries) entry.Dispose(); }
        }
        finally
        {
            await log.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void AddIsIdempotentAndNewestItemsComeFirst()
    {
        var first = Completion(1, "first");
        var second = Completion(2, "second");

        Assert.True(_history.Add(first));
        Assert.False(_history.Add(first));
        Assert.True(_history.Add(second));

        var items = _history.Snapshot();
        Assert.Equal(2, items.Count);
        Assert.Equal(second.OperationId, items[0].OperationId);
        Assert.Equal(first.OperationId, items[1].OperationId);
    }

    [Fact]
    public void InstancesDoNotShareHistory()
    {
        using var other = new HomeDictationHistory();

        Assert.True(_history.Add(Completion(3, "only in first instance")));

        Assert.Empty(other.Snapshot());
    }

    [Fact]
    public void HistoryIsBoundedByCountAndTranscriptCharacters()
    {
        for (var index = 0; index < 40; index++)
            Assert.True(_history.Add(Completion(index, new string('x', 20_000))));

        var items = _history.Snapshot();
        Assert.InRange(items.Count, 1, 100);
        Assert.True(items.Sum(item => item.Text.Length) <= 500_000);
        Assert.Equal((byte)39, items[0].OperationId.ToByteArray()[15]);
    }

    [Fact]
    public void HistoryKeepsOnlyTheNewestHundredItems()
    {
        for (var index = 0; index < 120; index++)
            Assert.True(_history.Add(Completion(index, "short")));

        var items = _history.Snapshot();
        Assert.Equal(100, items.Count);
        Assert.Equal((byte)119, items[0].OperationId.ToByteArray()[15]);
        Assert.Equal((byte)20, items[^1].OperationId.ToByteArray()[15]);
    }

    [Fact]
    public void OversizedTranscriptIsRejectedWithoutChangingItsText()
    {
        var item = Completion(7, new string('x', HomeDictationHistory.MaximumCharacters + 1));

        Assert.False(_history.Add(item));
        Assert.Empty(_history.Snapshot());
        Assert.True(_history.OversizedCurrentResult);
        Assert.False(_history.Contains(item.OperationId));
    }

    [Fact]
    public void RemoveDropsTheItemOnceAndNotifies()
    {
        var first = Completion(1, "first");
        var second = Completion(2, "second");
        _history.Add(first);
        _history.Add(second);
        var changes = 0;
        _history.Changed += () => changes++;

        Assert.True(_history.Remove(first.OperationId));
        Assert.False(_history.Remove(first.OperationId));

        Assert.Equal(1, changes);
        Assert.Equal(second.OperationId, Assert.Single(_history.Snapshot()).OperationId);
        Assert.False(_history.Contains(first.OperationId));
    }

    [Fact]
    public void RecordDeliveryUpdatesTheRetainedItemAndNotifiesOnlyOnChange()
    {
        var item = Completion(4, "delivered");
        _history.Add(item);
        var changes = 0;
        _history.Changed += () => changes++;

        Assert.True(_history.RecordDelivery(item.OperationId, TextDeliveryStatus.Sent));
        Assert.False(_history.RecordDelivery(item.OperationId, TextDeliveryStatus.Sent));
        Assert.True(_history.RecordDelivery(item.OperationId, TextDeliveryStatus.Verified));

        Assert.Equal(2, changes);
        Assert.Equal(TextDeliveryStatus.Verified, Assert.Single(_history.Snapshot()).Delivery);
    }

    [Fact]
    public void RecordDeliveryIgnoresDictationsThatHistoryDoesNotHold()
    {
        var oversized = Completion(5, new string('x', HomeDictationHistory.MaximumCharacters + 1));
        _history.Add(oversized);
        var changes = 0;
        _history.Changed += () => changes++;

        Assert.False(_history.RecordDelivery(oversized.OperationId, TextDeliveryStatus.Verified));
        Assert.False(_history.RecordDelivery(GuidFrom(6), TextDeliveryStatus.Verified));

        Assert.Equal(0, changes);
        Assert.Empty(_history.Snapshot());
    }

    [Fact]
    public void NewItemsHaveNoDeliveryUntilOneIsRecorded()
    {
        Assert.True(_history.Add(Completion(8, "in app")));

        Assert.Null(Assert.Single(_history.Snapshot()).Delivery);
    }

    [Fact]
    public async Task HistorySurvivesRestartWithOlderDatesAndDeliveryStatus()
    {
        var path = TemporaryPath();
        try
        {
            var completion = Completion(1, "saved transcription") with
            {
                CompletedAt = DateTimeOffset.Now.AddDays(-3),
                Result = new RecognitionResult("saved transcription", "saved transcription")
            };
            await using (var history = new HomeDictationHistory(path))
            {
                await history.InitializeAsync();
                history.Add(completion);
                history.RecordDelivery(completion.OperationId, TextDeliveryStatus.Verified);
            }

            await using var reloaded = new HomeDictationHistory(path);
            await reloaded.InitializeAsync();
            var saved = Assert.Single(reloaded.Snapshot());
            Assert.Equal(completion.OperationId, saved.OperationId);
            Assert.Equal(completion.CompletedAt, saved.CompletedAt);
            Assert.Equal("saved transcription", saved.Text);
            Assert.Equal(2, saved.Words);
            Assert.Equal(TextDeliveryStatus.Verified, saved.Delivery);
            Assert.Null(reloaded.StorageError);
            var projection = HomeHistoryProjection.Create(reloaded.Snapshot(), "saved", false, _ => null, DateTime.Today);
            Assert.True(projection.HasMatches);
            Assert.Equal(0, projection.TodayCount);
            Assert.Single(Assert.Single(projection.Groups).Entries);
        }
        finally { DeleteTemporary(path); }
    }

    [Fact]
    public async Task RestartRetainsNewestHundredAndNewEntriesEvictOldest()
    {
        var path = TemporaryPath();
        try
        {
            await using (var history = new HomeDictationHistory(path))
            {
                await history.InitializeAsync();
                for (var index = 1; index <= 120; index++) history.Add(Completion(index, $"transcription {index}"));
            }
            await using (var reloaded = new HomeDictationHistory(path))
            {
                await reloaded.InitializeAsync();
                Assert.Equal(100, reloaded.Snapshot().Count);
                Assert.Equal(GuidFrom(120), reloaded.Snapshot()[0].OperationId);
                Assert.Equal(GuidFrom(21), reloaded.Snapshot()[^1].OperationId);
                reloaded.Add(Completion(121, "newest"));
            }
            await using var final = new HomeDictationHistory(path);
            await final.InitializeAsync();
            Assert.Equal(100, final.Snapshot().Count);
            Assert.Equal(GuidFrom(121), final.Snapshot()[0].OperationId);
            Assert.Equal(GuidFrom(22), final.Snapshot()[^1].OperationId);
        }
        finally { DeleteTemporary(path); }
    }

    [Fact]
    public async Task RemovedTranscriptionDoesNotReturnAfterRestart()
    {
        var path = TemporaryPath();
        try
        {
            await using (var history = new HomeDictationHistory(path))
            {
                await history.InitializeAsync();
                history.Add(Completion(1, "remove me"));
                history.Add(Completion(2, "keep me"));
            }
            await using (var reloaded = new HomeDictationHistory(path))
            {
                await reloaded.InitializeAsync();
                Assert.True(reloaded.Remove(GuidFrom(1)));
            }
            await using var final = new HomeDictationHistory(path);
            await final.InitializeAsync();
            Assert.Equal("keep me", Assert.Single(final.Snapshot()).Text);
        }
        finally { DeleteTemporary(path); }
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"SchemaVersion\":99,\"Items\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"Items\":[null]}")]
    public async Task UnreadableHistoryIsReportedAndNeverOverwritten(string original)
    {
        var path = TemporaryPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, original);
            await using (var history = new HomeDictationHistory(path))
            {
                await history.InitializeAsync();
                Assert.NotNull(history.StorageError);
                Assert.True(history.Add(Completion(1, "still usable in memory")));
                Assert.Single(history.Snapshot());
            }
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally { DeleteTemporary(path); }
    }

    [Fact]
    public async Task SaveFailureIsVisibleAndDoesNotLoseInMemoryTranscription()
    {
        var path = TemporaryPath();
        try
        {
            await using var history = new HomeDictationHistory(path);
            await history.InitializeAsync();
            Directory.CreateDirectory(path);
            history.Add(Completion(1, "unsaved"));
            await history.DisposeAsync();
            Assert.NotNull(history.StorageError);
            Assert.Equal("unsaved", Assert.Single(history.Snapshot()).Text);
        }
        finally { DeleteTemporary(path); }
    }

    [Fact]
    public async Task MissingHistoryStartsEmptyAndSavesWithoutAudio()
    {
        var path = TemporaryPath();
        try
        {
            await using (var history = new HomeDictationHistory(path))
            {
                await history.InitializeAsync();
                Assert.Empty(history.Snapshot());
                Assert.Null(history.StorageError);
                history.Add(Completion(1, "plain local text") with { Result = new RecognitionResult("plain local text", null) });
            }
            Assert.Equal("dictation-history.json", Path.GetFileName(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!))));
            await using var reloaded = new HomeDictationHistory(path);
            await reloaded.InitializeAsync();
            Assert.Null(Assert.Single(reloaded.Snapshot()).Words);
        }
        finally { DeleteTemporary(path); }
    }

    private static string TemporaryPath() =>
        Path.Combine(Path.GetTempPath(), "Ansible.History.Tests", Guid.NewGuid().ToString("N"), "dictation-history.json");

    [Fact]
    public async Task CloseDuringLoadJoinsMergedHistoryAndKeepsEarlyDelivery()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loaded = new TaskCompletionSource<HomeHistoryDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<HomeHistoryDocument>();
        await using var history = new HomeDictationHistory(TemporaryPath(), readDocument: () =>
        {
            entered.SetResult();
            return loaded.Task;
        }, writeDocument: document => { writes.Add(document); return Task.CompletedTask; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var early = Completion(2, "early speech");
        Task closing;
        try
        {
            Assert.True(history.Add(early));
            Assert.True(history.RecordDelivery(early.OperationId, TextDeliveryStatus.Verified));
            closing = history.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            Assert.Empty(writes);
            Assert.False(history.Add(Completion(3, "too late")));
        }
        finally
        {
            loaded.TrySetResult(new(1, [SavedItem(Completion(1, "saved speech")), SavedItem(early)]));
        }
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = Assert.Single(writes);
        Assert.Equal(new[] { "early speech", "saved speech" }, saved.Items.Select(item => item.Text));
        Assert.Equal(TextDeliveryStatus.Verified, saved.Items[0].Delivery);
        Assert.Null(history.StorageError);
    }

    [Fact]
    public async Task FailedBlockedLoadNeverSavesEarlyChangesEvenWhenClosing()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loaded = new TaskCompletionSource<HomeHistoryDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        await using var history = new HomeDictationHistory(TemporaryPath(), readDocument: () =>
        {
            entered.SetResult();
            return loaded.Task;
        }, writeDocument: _ => { Interlocked.Increment(ref writes); return Task.CompletedTask; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task closing;
        try
        {
            history.Add(Completion(1, "memory only"));
            closing = history.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
        }
        finally { loaded.TrySetException(new System.Text.Json.JsonException("Unreadable history")); }
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, writes);
        Assert.Equal("memory only", Assert.Single(history.Snapshot()).Text);
        Assert.NotNull(history.StorageError);
    }

    [Fact]
    public async Task SlowHistoryWriteConvergesOnLatestRemovalAndDeliveryBeforeClose()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<HomeHistoryDocument>();
        await using var history = new HomeDictationHistory(TemporaryPath(),
            readDocument: () => Task.FromResult<HomeHistoryDocument?>(new(1, [])), writeDocument: async document =>
            {
                writes.Add(document);
                if (writes.Count == 1) { entered.SetResult(); await release.Task; }
            });
        await history.InitializeAsync();
        Task closing;
        try
        {
            history.Add(Completion(1, "removed"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            history.Add(Completion(2, "delivered"));
            history.RecordDelivery(GuidFrom(2), TextDeliveryStatus.Verified);
            history.Remove(GuidFrom(1));
            history.Add(Completion(3, "latest"));
            closing = history.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            Assert.Single(writes);
        }
        finally { release.TrySetResult(); }
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, writes.Count);
        Assert.Equal(new[] { "latest", "delivered" }, writes[1].Items.Select(item => item.Text));
        Assert.Equal(TextDeliveryStatus.Verified, writes[1].Items[1].Delivery);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulSnapshotRecoversAllChangesAfterWriteFailure(bool asynchronous)
    {
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        HomeHistoryDocument? saved = null;
        await using var history = new HomeDictationHistory(TemporaryPath(),
            readDocument: () => Task.FromResult<HomeHistoryDocument?>(new(1, [])), writeDocument: document =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    if (asynchronous) return Task.FromException(new IOException("Disk unavailable"));
                    throw new IOException("Disk unavailable");
                }
                saved = document;
                return Task.CompletedTask;
            });
        history.Changed += () =>
        {
            if (history.StorageError is not null) throw new InvalidOperationException("Failure observer failed");
        };
        history.Changed += () => { if (history.StorageError is not null) failed.TrySetResult(); };
        await history.InitializeAsync();
        history.Add(Completion(1, "first"));
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(history.StorageError);
        history.Add(Completion(2, "second"));
        await history.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, attempts);
        Assert.NotNull(saved);
        Assert.Equal(new[] { "second", "first" }, saved.Items.Select(item => item.Text));
        Assert.Null(history.StorageError);
    }

    [Fact]
    public async Task ThrowingHistoryObserverDoesNotStrandInitializationOrLaterObservers()
    {
        var loaded = new TaskCompletionSource<HomeHistoryDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = 0;
        HomeHistoryDocument? saved = null;
        await using var history = new HomeDictationHistory(TemporaryPath(), readDocument: () => loaded.Task,
            writeDocument: document => { saved = document; return Task.CompletedTask; });
        history.Changed += () => throw new InvalidOperationException("Observer failed");
        history.Changed += () => Interlocked.Increment(ref notifications);
        loaded.SetResult(new(1, []));
        await history.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(history.Add(Completion(1, "preserved")));
        await history.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(saved);
        Assert.Equal("preserved", Assert.Single(saved.Items).Text);
        Assert.True(notifications >= 2);
    }

    [Fact]
    public async Task ReplacementFailureKeepsOldBytesAndRemovesTemporaryFile()
    {
        var path = TemporaryPath();
        try
        {
            await using (var original = new HomeDictationHistory(path))
            {
                await original.InitializeAsync();
                original.Add(Completion(1, "saved"));
            }
            var oldBytes = await File.ReadAllBytesAsync(path);
            await using var history = new HomeDictationHistory(path);
            await history.InitializeAsync();
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                history.Add(Completion(2, "unsaved"));
                await history.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.NotNull(history.StorageError);
            Assert.Equal(oldBytes, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally { DeleteTemporary(path); }
    }

    [Fact]
    public async Task DeferredSnapshotKeepsItsAdmissionDiagnosticGeneration()
    {
        var path = TemporaryPath();
        var loaded = new TaskCompletionSource<HomeHistoryDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var log = new LocalDiagnosticLog(Path.Combine(Path.GetDirectoryName(path)!, "diagnostics"));
        log.SetEnabled(true);
        try
        {
            await using (var history = new HomeDictationHistory(path, log, readDocument: () => loaded.Task,
                writeDocument: _ => { saved.TrySetResult(); return Task.CompletedTask; }))
            {
                history.Add(Completion(1, "previous consent generation"));
                log.SetEnabled(false);
                log.SetEnabled(true);
                loaded.SetResult(new(1, []));
                await history.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                history.Add(Completion(2, "current consent generation"));
            }
            await log.DisposeAsync();
            var lines = await File.ReadAllLinesAsync(Path.Combine(log.DirectoryPath, "local-diagnostics.jsonl"));
            var entries = lines.Select(line => System.Text.Json.JsonDocument.Parse(line)).ToArray();
            try
            {
                Assert.Single(entries, entry => entry.RootElement.GetProperty("Category").GetString() == "History" &&
                    entry.RootElement.GetProperty("Stage").GetString() == "Save");
            }
            finally { foreach (var entry in entries) entry.Dispose(); }
        }
        finally
        {
            loaded.TrySetResult(new(1, []));
            await log.DisposeAsync();
            DeleteTemporary(path);
        }
    }

    private static HomeHistoryItem SavedItem(DictationCompletion completion) => new(
        completion.OperationId, completion.CompletedAt, completion.Result.SpeechText!,
        completion.ModelId, completion.Result.SpokenWords, completion.Elapsed);

    private static void DeleteTemporary(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static DictationCompletion Completion(int seed, string text) => new(
        GuidFrom(seed), "model", new RecognitionResult(text, text), DateTimeOffset.UtcNow.AddSeconds(seed), TimeSpan.FromSeconds(seed));

    private static Guid GuidFrom(int seed)
    {
        var bytes = new byte[16];
        bytes[15] = checked((byte)seed);
        return new Guid(bytes);
    }
}
