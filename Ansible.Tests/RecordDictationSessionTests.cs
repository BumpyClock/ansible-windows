using Ansible.Core;

namespace Ansible.Tests;

public sealed partial class DictationSessionTests
{
    [Fact]
    public async Task RecordWithoutAnEditableDestinationPreparesOneBundleBeforeCompletion()
    {
        var media = new RecordTakeSessionTests.TakeFactory
        {
            Capture = new() { Media = new([new("final.png", null, TimeSpan.Zero)], null, []) }
        };
        var session = Create(new FakeEngine(), recordTakeFactory: media);
        var completions = new List<DictationCompletion>();
        session.DictationCompleted += completion =>
        {
            Assert.True(media.Capture.IsReleased);
            Assert.Same(session.State.Payload, completion.Result.Payload);
            completions.Add(completion);
        };
        try
        {
            await session.InitializeAsync();
            var operation = session.StartShortcutDictationAsync(null,
                Task.FromException(new InvalidOperationException("No editable destination")), session.SnapshotRecordTake());
            await WaitForStateAsync(session, DictationPhase.Recording);
            Assert.True(session.State.IsRecordTake);
            await session.FinishAsync();
            var result = await operation;
            Assert.Equal(SessionOutcomeKind.Completed, result.Kind);
            Assert.Equal("hello world", result.Result!.SpeechText);
            Assert.Equal(2, result.Result.SpokenWords);
            Assert.Equal("hello world\nFrames in order: final screen.", result.Result.Payload!.Text);
            Assert.Same(result.Result.Payload, Assert.Single(completions).Result.Payload);
            Assert.False(session.State.IsRecordTake);
        }
        finally { await session.CloseAsync(); }
    }

    [Fact]
    public async Task RecordOperationDeadlineStopsMicrophoneAndMediaTogether()
    {
        var clock = new ManualTime();
        var source = new FakeCapture();
        var media = new RecordTakeSessionTests.TakeFactory();
        var session = new DictationSession(_ => new FakeEngine(),
            new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(source) },
            new FakeInput(), new FakeUsageStore(), "models", new FakeSettingsStore(), clock,
            TimeSpan.FromMinutes(5), recordTakeFactory: media);
        var completions = new List<DictationCompletion>();
        session.DictationCompleted += completions.Add;
        try
        {
            await session.InitializeAsync();
            var origin = System.Diagnostics.Stopwatch.GetTimestamp() - 20 * System.Diagnostics.Stopwatch.Frequency;
            var operation = session.StartShortcutDictationAsync(null, recordTakeOptions: new(1, origin, true));
            await WaitForStateAsync(session, DictationPhase.Recording);
            await media.Started.Task;
            clock.Advance(TimeSpan.FromMinutes(4));
            Assert.False(operation.IsCompleted);
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(SessionOutcomeKind.TimedOut, (await operation).Kind);
            Assert.True(source.IsReleased);
            Assert.True(media.Capture.IsReleased);
            Assert.True(media.Capture.Discarded);
            Assert.Empty(completions);
        }
        finally { await session.CloseAsync(); }
    }

    [Fact]
    public async Task RecordMediaFailureStillCompletesAuthoritativeSpeech()
    {
        var media = new RecordTakeSessionTests.TakeFactory { Capture = new() { StopError = new IOException("test encoder failure") } };
        var session = Create(new FakeEngine(), recordTakeFactory: media);
        try
        {
            await session.InitializeAsync();
            var operation = session.StartShortcutDictationAsync(null, recordTakeOptions: session.SnapshotRecordTake());
            await WaitForStateAsync(session, DictationPhase.Recording);
            await session.FinishAsync();
            var outcome = await operation;
            Assert.Equal(SessionOutcomeKind.Completed, outcome.Kind);
            Assert.Equal("hello world", outcome.Result!.Payload!.Text);
            Assert.Empty(outcome.Result.Payload.Frames);
            Assert.Null(outcome.Result.Payload.VideoPath);
            Assert.Equal(NoticeKind.Warning, session.State.Notice.Kind);
            Assert.Contains("test encoder failure", session.State.Notice.Message);
        }
        finally { await session.CloseAsync(); }
    }

    [Fact]
    public async Task RetainedScreenResourcesBlockAdmissionButPreserveTheSuccessfulBundle()
    {
        var media = new RecordTakeSessionTests.TakeFactory { Capture = new() { FailRelease = true } };
        var session = Create(new FakeEngine(), recordTakeFactory: media);
        var completions = new List<DictationCompletion>();
        session.DictationCompleted += completions.Add;
        try
        {
            await session.InitializeAsync();
            var operation = session.StartShortcutDictationAsync(null, recordTakeOptions: session.SnapshotRecordTake());
            await WaitForStateAsync(session, DictationPhase.Recording);
            await session.FinishAsync();
            var outcome = await operation;
            Assert.Equal(SessionOutcomeKind.Completed, outcome.Kind);
            Assert.True(outcome.MediaResourcesRetained);
            Assert.Equal(DictationPhase.RecoveryRequired, session.State.Phase);
            Assert.Equal("hello world", outcome.Result!.Payload!.Text);
            Assert.Single(completions);
            Assert.False(session.State.CanStart);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartDictationAsync());
        }
        finally { media.Capture.FailRelease = false; await session.CloseAsync(); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n")]
    public async Task RecordWithoutAuthoritativeSpeechDiscardsFinishedMedia(string? speech)
    {
        var media = new RecordTakeSessionTests.TakeFactory();
        var session = Create(new FakeEngine { Result = new("display only", speech) }, recordTakeFactory: media);
        var completions = new List<DictationCompletion>();
        session.DictationCompleted += completions.Add;
        try
        {
            await session.InitializeAsync();
            var operation = session.StartShortcutDictationAsync(null, recordTakeOptions: session.SnapshotRecordTake());
            await WaitForStateAsync(session, DictationPhase.Recording);
            await session.FinishAsync();
            Assert.Equal(SessionOutcomeKind.Failed, (await operation).Kind);
            Assert.True(media.Capture.Discarded);
            Assert.True(media.Capture.IsReleased);
            Assert.Empty(completions);
        }
        finally { await session.CloseAsync(); }
    }

    [Fact]
    public async Task ProducerFailureAfterFinalRecognitionDiscardsFinishedMedia()
    {
        var final = new TaskCompletionSource<RecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeCapture();
        var media = new RecordTakeSessionTests.TakeFactory();
        var session = Create(new FakeEngine
        {
            Streaming = async (_, _, _, ready) => { ready?.Invoke(); return await final.Task; }
        }, new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(source) }, recordTakeFactory: media);
        var completions = new List<DictationCompletion>();
        session.DictationCompleted += completions.Add;
        try
        {
            await session.InitializeAsync();
            var operation = session.StartShortcutDictationAsync(null, recordTakeOptions: session.SnapshotRecordTake());
            await WaitForStateAsync(session, DictationPhase.Recording);
            source.Packets.Writer.TryComplete(new IOException("microphone delivery failed"));
            await session.FinishAsync();
            final.SetResult(new("hello world", "hello world"));
            var outcome = await operation;
            Assert.Equal(SessionOutcomeKind.Failed, outcome.Kind);
            Assert.Equal("hello world", outcome.Result!.SpeechText);
            Assert.Contains("microphone delivery failed", outcome.Error!.ToString());
            Assert.True(media.Capture.Discarded);
            Assert.True(media.Capture.IsReleased);
            Assert.Empty(completions);
        }
        finally { final.TrySetResult(new("", "")); await session.CloseAsync(); }
    }

    [Fact]
    public async Task MicrophoneCleanupFailureRetractsPreparedMedia()
    {
        var source = new FakeCapture { FailDisposal = true };
        var media = new RecordTakeSessionTests.TakeFactory
        {
            Capture = new() { Media = new([new(Path.Combine(Path.GetTempPath(), "summary.png"), null, TimeSpan.Zero)],
                Path.Combine(Path.GetTempPath(), "take.mp4"), []) }
        };
        var session = Create(new FakeEngine(),
            new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(source) }, recordTakeFactory: media);
        string? prepared = null;
        session.StateChanged += state => { if (state.Payload is not null) prepared = state.Transcript; };
        try
        {
            await session.InitializeAsync();
            var operation = session.StartShortcutDictationAsync(null, recordTakeOptions: session.SnapshotRecordTake());
            await WaitForStateAsync(session, DictationPhase.Recording);
            await session.FinishAsync();
            var outcome = await operation;
            Assert.Equal(SessionOutcomeKind.Failed, outcome.Kind);
            Assert.Contains("Frames in order:", prepared);
            Assert.Contains("Video: file:", prepared);
            Assert.Null(outcome.Result!.Payload);
            Assert.Null(session.State.Payload);
            Assert.Equal("hello world", session.State.Transcript);
            Assert.True(media.Capture.Discarded);
        }
        finally { source.FailDisposal = false; await session.CloseAsync(); }
    }

    [Fact]
    public async Task EscDuringCaptureStartupSuppressesCompletionAndJoinsThatStartup()
    {
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var media = new RecordTakeSessionTests.TakeFactory { Startup = startup.Task };
        var session = Create(new FakeEngine(), recordTakeFactory: media);
        var completions = new List<DictationCompletion>();
        session.DictationCompleted += completions.Add;
        try
        {
            await session.InitializeAsync();
            var operation = session.StartShortcutDictationAsync(null, recordTakeOptions: session.SnapshotRecordTake());
            await WaitForStateAsync(session, DictationPhase.Recording);
            await media.Started.Task;
            media.Discard!();
            Assert.False(operation.IsCompleted);
            startup.SetResult();
            Assert.Equal(SessionOutcomeKind.Cancelled, (await operation).Kind);
            Assert.True(media.Capture.IsReleased);
            Assert.Empty(completions);
        }
        finally { startup.TrySetResult(); await session.CloseAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MicrophoneOwnershipFailureNeverBecomesACompletedDictationProjection(bool record)
    {
        var source = new FakeCapture { KeepOwnershipOnDispose = true };
        var media = new RecordTakeSessionTests.TakeFactory();
        var session = Create(new FakeEngine(), new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(source) },
            recordTakeFactory: media);
        var completions = new List<DictationCompletion>();
        session.DictationCompleted += completions.Add;
        try
        {
            await session.InitializeAsync();
            var operation = record
                ? session.StartShortcutDictationAsync(null, recordTakeOptions: session.SnapshotRecordTake())
                : session.StartDictationAsync();
            await WaitForStateAsync(session, DictationPhase.Recording);
            await session.FinishAsync();
            await operation;
            Assert.Equal(DictationPhase.RecoveryRequired, session.State.Phase);
            Assert.Empty(completions);
            if (record) Assert.True(media.Capture.Discarded);
        }
        finally { source.KeepOwnershipOnDispose = false; await session.CloseAsync(); }
    }
}
