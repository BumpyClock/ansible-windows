using System.Threading.Channels;
using Ansible.Core;

namespace Ansible.Tests;

public sealed partial class DictationSessionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BlockedUsageDoesNotDelayRecognitionOrFinalDeliveryButCloseJoinsIt(
        bool dictation, bool synchronousStore)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var synchronousRelease = new ManualResetEventSlim();
        var usage = new ControlledUsageStore
        {
            BeforeRecording = synchronousStore ? () => synchronousRelease.Wait() : null,
            Recording = synchronousStore ? null : (_, _) => release.Task
        };
        var capture = new FakeCapture { Seconds = 10 };
        var session = CreateWithUsage(usage, capture: capture);
        var inserted = new List<string>();
        var delivery = new FinalTextDelivery((text, _) =>
        {
            Assert.True(!dictation || capture.IsReleased);
            inserted.Add(text);
            return Task.FromResult(TextDeliveryOutcome.Sent("sent"));
        }, () => true, () => false);
        var closed = false;
        var postClosedSnapshots = 0;
        session.StateChanged += snapshot =>
        {
            if (closed) { Interlocked.Increment(ref postClosedSnapshots); }
            if (snapshot.Phase == DictationPhase.Closed) { closed = true; }
        };
        try
        {
            await session.InitializeAsync();
            var operation = dictation ? session.StartDictationAsync() : session.TranscribeFileAsync("sample.wav");
            var delivered = delivery.RunAsync(operation, CancellationToken.None);
            if (dictation)
            {
                await WaitForStateAsync(session, DictationPhase.Recording);
                await session.FinishAsync();
            }
            await usage.Attempts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            var outcome = await delivered.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(SessionOutcomeKind.Completed, outcome.Kind);
            Assert.Equal("hello world", outcome.Result!.SpeechText);
            Assert.Equal(["hello world"], inserted);
            Assert.True(delivery.Sent);
            Assert.Equal(DictationPhase.Ready, session.State.Phase);
            Assert.Empty(usage.Document.Entries);

            var closing = session.CloseAsync();
            Assert.Same(closing, session.CloseAsync());
            Assert.False(closing.IsCompleted);
            Assert.Equal(DictationPhase.Closing, session.State.Phase);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.TranscribeFileAsync("late.wav"));
            release.TrySetResult();
            synchronousRelease.Set();
            await closing.WaitAsync(TimeSpan.FromSeconds(2));

            var entry = Assert.Single(usage.Document.Entries);
            Assert.Equal(dictation ? UsageSource.Dictation : UsageSource.File, entry.Source);
            Assert.Equal(dictation ? 10 : 0, entry.RecordingSeconds);
            Assert.Equal(2, entry.Words);
            Assert.Equal(DictationPhase.Closed, session.State.Phase);
            Assert.Equal(0, postClosedSnapshots);
        }
        finally
        {
            release.TrySetResult();
            synchronousRelease.Set();
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task PendingUsageRemainsOrderedExactlyOnceAndKeepsItsAdmissionDecision()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var usage = new ControlledUsageStore { Recording = (_, _) => release.Task };
        var clock = new ManualTime();
        var engine = new FakeEngine
        {
            Connecting = _ => Task.FromResult<IReadOnlyList<AudioModel>>([Model, Model with { Id = "second" }])
        };
        var session = CreateWithUsage(usage, engine, time: clock);
        var ids = new List<Guid>();
        var completedAt = new List<DateTimeOffset>();
        try
        {
            await session.InitializeAsync();
            for (var index = 0; index < 4; index++)
            {
                completedAt.Add(clock.GetUtcNow());
                Assert.Equal(SessionOutcomeKind.Completed,
                    (await session.TranscribeFileAsync("sample.wav").WaitAsync(TimeSpan.FromSeconds(2))).Kind);
                ids.Add(session.State.OperationId!.Value);
                if (index == 0)
                {
                    await usage.Attempts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                    session.SelectModel(1);
                }
                clock.Advance(TimeSpan.FromSeconds(1));
            }
            session.SetUsageEnabled(false);
            await session.TranscribeFileAsync("not-collected.wav").WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(usage.Document.Entries);
            clock.Advance(TimeSpan.FromHours(1));
            release.SetResult();
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(ids, usage.Document.Entries.Select(entry => entry.Id));
            Assert.Equal(4, usage.Document.Entries.Select(entry => entry.Id).Distinct().Count());
            Assert.Equal(4, usage.AttemptCount);
            Assert.Equal(completedAt, usage.Document.Entries.Select(entry => entry.CompletedAt));
            Assert.Equal([Model.Id, "second", "second", "second"],
                usage.Document.Entries.Select(entry => entry.ModelId));
            Assert.All(usage.Document.Entries, entry =>
            {
                Assert.Equal(2, entry.Words);
                Assert.Equal(UsageSource.File, entry.Source);
                entry.Validate();
            });
            Assert.Equal(ids, session.State.Usage!.Entries.Select(entry => entry.Id));
        }
        finally
        {
            release.TrySetResult();
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task UsageAdmissionAndFinalDeliveryWaitForCaptureCleanup()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new FakeCapture();
        capture.Stopping = () =>
        {
            capture.Packets.Writer.TryComplete();
            return release.Task;
        };
        var usage = new ControlledUsageStore();
        var engine = new FakeEngine();
        var session = CreateWithUsage(usage, engine, capture);
        var inserted = new List<string>();
        var delivery = new FinalTextDelivery((text, _) =>
        {
            Assert.True(capture.IsReleased);
            inserted.Add(text);
            return Task.FromResult(TextDeliveryOutcome.Sent("sent"));
        }, () => true, () => false);
        try
        {
            await session.InitializeAsync();
            var operation = session.StartDictationAsync();
            var delivered = delivery.RunAsync(operation, CancellationToken.None);
            await WaitForStateAsync(session, DictationPhase.Recording);
            var finishing = session.FinishAsync();
            await engine.StreamCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(operation.IsCompleted);
            Assert.Empty(inserted);
            Assert.Equal(0, usage.AttemptCount);
            release.SetResult();
            await finishing;
            Assert.Equal(SessionOutcomeKind.Completed, (await delivered.WaitAsync(TimeSpan.FromSeconds(2))).Kind);
            Assert.Equal(["hello world"], inserted);
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Single(usage.Document.Entries);
        }
        finally
        {
            release.TrySetResult();
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsageFaultRemainsAvailableToInsightsWithoutOpeningADictationBanner(bool synchronousFailure)
    {
        var usage = new ControlledUsageStore
        {
            BeforeRecording = synchronousFailure ? () => throw new IOException("usage disk failed") : null,
            Recording = synchronousFailure ? null : (_, _) => Task.FromException(new IOException("usage disk failed"))
        };
        var session = CreateWithUsage(usage);
        var reported = UsageFailureAsync(session);
        try
        {
            await session.InitializeAsync();
            var outcome = await session.TranscribeFileAsync("sample.wav").WaitAsync(TimeSpan.FromSeconds(2));
            await reported.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(SessionOutcomeKind.Completed, outcome.Kind);
            Assert.Null(outcome.Error);
            Assert.Equal("hello world", session.State.Transcript);
            Assert.Contains("usage disk failed", session.State.UsageError);
            Assert.False(session.State.Notice.ShouldDisplay);
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(DictationPhase.Closed, session.State.Phase);
            Assert.Contains("usage disk failed", session.State.UsageError);
            Assert.Empty(usage.Document.Entries);
        }
        finally { await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DelayedUsageFaultPreservesNewerOperationOrDeliveryNotice(bool activeOperation)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var usage = new ControlledUsageStore
        {
            Recording = async (_, _) =>
            {
                await release.Task;
                throw new IOException("delayed usage failure");
            }
        };
        var session = CreateWithUsage(usage);
        try
        {
            await session.InitializeAsync();
            await session.TranscribeFileAsync("first.wav").WaitAsync(TimeSpan.FromSeconds(2));
            await usage.Attempts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Task<SessionOutcome>? next = null;
            if (activeOperation)
            {
                next = session.StartDictationAsync();
                await WaitForStateAsync(session, DictationPhase.Recording);
            }
            else
            {
                await session.TranscribeFileAsync("second.wav").WaitAsync(TimeSpan.FromSeconds(2));
                session.Notify(NoticeKind.Warning, "Insertion stopped", "The destination changed.");
            }
            var before = session.State;
            var reported = UsageFailureAsync(session);
            release.SetResult();
            await reported.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(before.OperationId, session.State.OperationId);
            Assert.Equal(before.Phase, session.State.Phase);
            Assert.Equal(before.Transcript, session.State.Transcript);
            Assert.Same(before.Notice, session.State.Notice);
            Assert.Contains("delayed usage failure", session.State.UsageError);
            if (next is not null)
            {
                await session.CancelAsync();
                Assert.Equal(SessionOutcomeKind.Cancelled, (await next).Kind);
            }
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.TrySetResult();
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task FullUsageQueueReportsLostMetadataAndSuccessfulDrainDoesNotEraseTheError()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var usage = new ControlledUsageStore { Recording = (_, _) => release.Task };
        var session = CreateWithUsage(usage);
        var admitted = new List<Guid>();
        try
        {
            await session.InitializeAsync();
            await session.TranscribeFileAsync("first.wav").WaitAsync(TimeSpan.FromSeconds(2));
            admitted.Add(session.State.OperationId!.Value);
            await usage.Attempts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            // Observe saturation through the public error, without depending on the exact queue capacity.
            for (var index = 0; index < 128 && session.State.UsageError is null; index++)
            {
                Assert.Equal(SessionOutcomeKind.Completed,
                    (await session.TranscribeFileAsync("next.wav").WaitAsync(TimeSpan.FromSeconds(2))).Kind);
                if (session.State.UsageError is null) { admitted.Add(session.State.OperationId!.Value); }
            }
            Assert.NotNull(session.State.UsageError);
            Assert.Contains("queue", session.State.UsageError);
            Assert.False(session.State.Notice.ShouldDisplay);
            Assert.Equal("hello world", session.State.Transcript);
            var error = session.State.UsageError;
            release.SetResult();
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(admitted, usage.Document.Entries.Select(entry => entry.Id));
            Assert.Equal(admitted.Count, usage.AttemptCount);
            Assert.Equal(error, session.State.UsageError);
        }
        finally
        {
            release.TrySetResult();
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task CloseObservesWriteFailureAndDrainsLaterEntriesBeforePublishingClosed()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var usage = new ControlledUsageStore
        {
            Recording = async (_, _) =>
            {
                await release.Task;
                if (++attempts == 1) { throw new IOException("first write failed"); }
            }
        };
        var session = CreateWithUsage(usage);
        try
        {
            await session.InitializeAsync();
            await session.TranscribeFileAsync("first.wav").WaitAsync(TimeSpan.FromSeconds(2));
            await usage.Attempts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await session.TranscribeFileAsync("second.wav").WaitAsync(TimeSpan.FromSeconds(2));
            var secondId = session.State.OperationId;
            var closing = session.CloseAsync();
            var closingNotice = session.State.Notice;
            var closed = new TaskCompletionSource<SessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.StateChanged += state =>
            {
                if (state.Phase == DictationPhase.Closed) { closed.TrySetResult(state); }
            };
            Assert.False(closing.IsCompleted);
            release.SetResult();
            await closing.WaitAsync(TimeSpan.FromSeconds(2));
            var final = await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("first write failed", final.UsageError);
            Assert.Equal(secondId, Assert.Single(final.Usage!.Entries).Id);
            Assert.Same(closingNotice, final.Notice);
            Assert.Equal(2, usage.AttemptCount);
            Assert.Equal("hello world", final.Transcript);
        }
        finally
        {
            release.TrySetResult();
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task CloseJoinsUsagePublicationSoItCannotPublishAfterClosed()
    {
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePublication = new ManualResetEventSlim();
        var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var usage = new ControlledUsageStore { Recording = (_, _) => releaseWrite.Task };
        var session = CreateWithUsage(usage);
        var intercepted = 0;
        var published = new System.Collections.Concurrent.ConcurrentQueue<DictationPhase>();
        session.StateChanged += state =>
        {
            if (state.Usage?.Entries.Count == 1 && Interlocked.CompareExchange(ref intercepted, 1, 0) == 0)
            {
                publishing.TrySetResult();
                releasePublication.Wait();
            }
            published.Enqueue(state.Phase);
        };
        try
        {
            await session.InitializeAsync();
            await session.TranscribeFileAsync("sample.wav").WaitAsync(TimeSpan.FromSeconds(2));
            await usage.Attempts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            releaseWrite.SetResult();
            await publishing.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var closing = session.CloseAsync();
            Assert.False(closing.IsCompleted);
            Assert.Equal(DictationPhase.Closing, session.State.Phase);
            releasePublication.Set();
            await closing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(DictationPhase.Closed, published.Last());
            Assert.Equal(1, published.Count(phase => phase == DictationPhase.Closed));
        }
        finally
        {
            releaseWrite.TrySetResult();
            releasePublication.Set();
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledCollectionOrLoadFailureSkipsUsageWrites(bool loadFailure)
    {
        var usage = new ControlledUsageStore { FailLoading = loadFailure };
        var session = CreateWithUsage(usage);
        await session.InitializeAsync();
        if (!loadFailure) { session.SetUsageEnabled(false); }
        Assert.Equal(SessionOutcomeKind.Completed, (await session.TranscribeFileAsync("sample.wav")).Kind);
        await session.CloseAsync();
        Assert.Equal(0, usage.AttemptCount);
        Assert.Empty(usage.Document.Entries);
        if (loadFailure) { Assert.Contains("usage load failed", session.State.UsageError); }
    }

    [Fact]
    public async Task UnknownSpeechCountStaysUnknownInPersistedMetadata()
    {
        var usage = new ControlledUsageStore();
        var session = CreateWithUsage(usage, new FakeEngine { Result = new("display only", null) });
        await session.InitializeAsync();
        Assert.Equal(SessionOutcomeKind.Completed, (await session.TranscribeFileAsync("sample.wav")).Kind);
        await session.CloseAsync();
        Assert.Null(Assert.Single(usage.Document.Entries).Words);
        Assert.Null(session.State.Words);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrFailedRecognitionNeverQueuesUsage(bool fail)
    {
        var usage = new ControlledUsageStore();
        var engine = new FakeEngine
        {
            Streaming = async (_, _, token, ready) =>
            {
                ready?.Invoke();
                if (fail) { throw new IOException("recognition failed"); }
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new("unreachable", "unreachable");
            }
        };
        var session = CreateWithUsage(usage, engine);
        await session.InitializeAsync();
        var operation = session.StartDictationAsync();
        await engine.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (!fail) { await session.CancelAsync(); }
        Assert.Equal(fail ? SessionOutcomeKind.Failed : SessionOutcomeKind.Cancelled, (await operation).Kind);
        await session.CloseAsync();
        Assert.Equal(0, usage.AttemptCount);
        Assert.Empty(usage.Document.Entries);
    }

    private static DictationSession CreateWithUsage(
        IUsageStore usage, FakeEngine? engine = null, FakeCapture? capture = null, TimeProvider? time = null) =>
        new(_ => engine ?? new FakeEngine(),
            new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(capture ?? new FakeCapture()) },
            new FakeInput(), usage, "models", new FakeSettingsStore(), time);

    private static Task UsageFailureAsync(DictationSession session)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(SessionSnapshot state)
        {
            if (state.UsageError is not null)
            {
                session.StateChanged -= Changed;
                completion.TrySetResult();
            }
        }
        session.StateChanged += Changed;
        if (session.State.UsageError is not null) { Changed(session.State); }
        return completion.Task;
    }

    private sealed class ControlledUsageStore : IUsageStore
    {
        public string Path => "usage.json";
        public UsageDocument Document { get; private set; } = new();
        public Channel<UsageEntry> Attempts { get; } = Channel.CreateUnbounded<UsageEntry>();
        public int AttemptCount;
        public bool FailLoading { get; init; }
        public Action? BeforeRecording { get; init; }
        public Func<UsageEntry, CancellationToken, Task>? Recording { get; init; }

        public Task<UsageDocument> LoadAsync(CancellationToken token = default) => FailLoading
            ? Task.FromException<UsageDocument>(new IOException("usage load failed"))
            : Task.FromResult(Document);

        public Task<UsageDocument> RecordAsync(UsageEntry entry, CancellationToken token = default)
        {
            Interlocked.Increment(ref AttemptCount);
            Attempts.Writer.TryWrite(entry);
            BeforeRecording?.Invoke();
            return RecordCoreAsync(entry, token);
        }

        private async Task<UsageDocument> RecordCoreAsync(UsageEntry entry, CancellationToken token)
        {
            if (Recording is not null) { await Recording(entry, token); }
            Document = Document with { Entries = Document.Entries.Append(entry).ToArray() };
            return Document;
        }
    }
}
