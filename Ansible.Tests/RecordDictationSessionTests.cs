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
            Assert.Equal("hello world\n\nFrames in order: final screen.", result.Result.Payload!.Text);
            Assert.Same(result.Result.Payload, Assert.Single(completions).Result.Payload);
            Assert.False(session.State.IsRecordTake);
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
        }
        finally { source.KeepOwnershipOnDispose = false; await session.CloseAsync(); }
    }
}
