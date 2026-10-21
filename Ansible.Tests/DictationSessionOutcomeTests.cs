using Ansible.Core;

namespace Ansible.Tests;

public sealed partial class DictationSessionTests
{
    [Theory]
    [InlineData(SessionActivity.Dictation)]
    [InlineData(SessionActivity.Replay)]
    [InlineData(SessionActivity.File)]
    public async Task IncompatibleSavedKrokoHintsAreRejectedBeforeOperationOrCapture(SessionActivity activity)
    {
        var model = new AudioModel { Id = "kroko", Family = "kroko_asr", Mode = "streaming", SupportsCustomDictionary = true };
        var capture = new FakeCaptureFactory();
        var settings = new FakeSettingsStore { Settings = new() { ModelId = model.Id, CustomDictionary = "AC/DC\nGood hint" } };
        var engine = new FakeEngine { Connecting = _ => Task.FromResult<IReadOnlyList<AudioModel>>([model]) };
        var session = Create(engine, capture, settings: settings);
        try
        {
            await session.InitializeAsync();
            var previous = session.State;
            await Assert.ThrowsAsync<InvalidDataException>(() => activity switch
            {
                SessionActivity.Dictation => session.StartDictationAsync(),
                SessionActivity.Replay => session.ReplayAsync("sample.wav"),
                _ => session.TranscribeFileAsync("sample.wav")
            });
            Assert.Equal(0, capture.Starts);
            Assert.Equal(previous.OperationId, session.State.OperationId);
            Assert.Equal("AC/DC\nGood hint", session.State.Settings.CustomDictionary);
            session.SetCustomDictionary("Good hint");
            Assert.Equal("Good hint", session.State.Settings.CustomDictionary);
        }
        finally { await session.CloseAsync(); }
    }

    [Fact]
    public async Task FaultingCompletionObserverDoesNotSkipLaterObserversOrTerminalAudioReset()
    {
        var session = Create(new FakeEngine());
        try
        {
            await session.InitializeAsync();
            var delivered = false;
            var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.DictationCompleted += _ => throw new InvalidOperationException("Observer failure.");
            session.DictationCompleted += _ => delivered = true;
            session.AudioLevelChanged += level => { if (level == 0 && session.State.IsIdle) reset.TrySetResult(); };
            var operation = session.StartDictationAsync();
            await WaitForStateAsync(session, DictationPhase.Recording);
            await session.FinishAsync();
            Assert.Equal(SessionOutcomeKind.Completed, (await operation).Kind);
            await reset.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(delivered);
        }
        finally { await session.CloseAsync(); }
    }

    [Fact]
    public async Task SuccessfulDictationDeliversOneTypedCompletionAndCancellationDeliversNone()
    {
        var session = Create(new FakeEngine());
        try
        {
            await session.InitializeAsync();
            var completions = new List<DictationCompletion>();
            session.DictationCompleted += completions.Add;
            var completed = session.StartDictationAsync();
            await WaitForStateAsync(session, DictationPhase.Recording);
            await session.FinishAsync();
            Assert.Equal(SessionOutcomeKind.Completed, (await completed).Kind);
            var completion = Assert.Single(completions);
            Assert.Equal(session.State.OperationId, completion.OperationId);
            Assert.Equal("test", completion.ModelId);
            Assert.Equal(session.Elapsed, completion.Elapsed);
            Assert.NotEqual(default, completion.CompletedAt);

            var cancelled = session.StartDictationAsync();
            await WaitForStateAsync(session, DictationPhase.Recording);
            await session.CancelAsync();
            Assert.Equal(SessionOutcomeKind.Cancelled, (await cancelled).Kind);
            Assert.Single(completions);
        }
        finally { await session.CloseAsync(); }
    }

    [Fact]
    public async Task CompletionIsDeliveredAfterTerminalSnapshotPublication()
    {
        var session = Create(new FakeEngine());
        try
        {
            await session.InitializeAsync();
            var terminalPublished = false;
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.DictationCompleted += _ =>
            {
                Assert.True(terminalPublished);
                delivered.TrySetResult();
            };
            session.StateChanged += state =>
            {
                if (state.Activity == SessionActivity.Dictation && state.Phase == DictationPhase.Ready && state.Result is not null)
                    terminalPublished = true;
            };

            var completion = session.StartDictationAsync();
            await WaitForStateAsync(session, DictationPhase.Recording);
            await session.FinishAsync();
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(SessionOutcomeKind.Completed, (await completion).Kind);

            var next = session.StartDictationAsync();
            await WaitForStateAsync(session, DictationPhase.Recording);
            await session.CancelAsync();
            Assert.Equal(SessionOutcomeKind.Cancelled, (await next).Kind);
        }
        finally { await session.CloseAsync(); }
    }

    [Fact]
    public async Task CleanupFailureDoesNotPublishASuccessfulHistoryOutcome()
    {
        var capture = new FakeCapture { FailDisposal = true };
        var session = Create(new FakeEngine(), new FakeCaptureFactory
        {
            Starting = _ => Task.FromResult<IAudioCapture>(capture)
        });
        try
        {
            await session.InitializeAsync();
            var completions = 0;
            session.DictationCompleted += _ => completions++;
            var operation = session.StartDictationAsync();
            await WaitForStateAsync(session, DictationPhase.Recording);
            await session.FinishAsync();
            Assert.Equal(SessionOutcomeKind.Failed, (await operation).Kind);
            Assert.Equal(0, completions);
        }
        finally
        {
            capture.FailDisposal = false;
            await session.CloseAsync();
        }
    }

    [Fact]
    public async Task TranscriptProvenanceSurvivesReconnectAndModelMaintenance()
    {
        var first = new FakeEngine { Result = new("first transcript", "first transcript") };
        var second = new FakeEngine();
        var third = new FakeEngine();
        var engines = new Queue<IRecognitionEngine>([first, second, third]);
        var session = new DictationSession(_ => engines.Dequeue(), new FakeCaptureFactory(), new FakeInput(),
            new FakeUsageStore(), "models", new FakeSettingsStore());
        try
        {
            await session.InitializeAsync();
            var operation = session.StartDictationAsync();
            await WaitForStateAsync(session, DictationPhase.Recording);
            await session.FinishAsync();
            Assert.Equal(SessionOutcomeKind.Completed, (await operation).Kind);

            var transcriptId = session.State.TranscriptOperationId;
            Assert.Equal(session.State.OperationId, transcriptId);
            Assert.Equal("first transcript", session.State.Transcript);

            await session.ConnectAsync("models");
            Assert.Equal("first transcript", session.State.Transcript);
            Assert.Equal(transcriptId, session.State.TranscriptOperationId);
            Assert.NotEqual(transcriptId, session.State.OperationId);

            await session.MaintainModelsAsync(_ => Task.CompletedTask);
            Assert.Equal("first transcript", session.State.Transcript);
            Assert.Equal(transcriptId, session.State.TranscriptOperationId);
            Assert.NotEqual(transcriptId, session.State.OperationId);
        }
        finally { await session.CloseAsync(); }
    }
}
