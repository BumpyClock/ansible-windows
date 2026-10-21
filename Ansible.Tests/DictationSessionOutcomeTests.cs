using Ansible.Core;

namespace Ansible.Tests;

public sealed partial class DictationSessionTests
{
    [Fact]
    public async Task HistoryCanDistinguishCompletionFromCancellationAndANewOperation()
    {
        var session = Create(new FakeEngine());
        try
        {
            await session.InitializeAsync();
            var completed = session.StartDictationAsync();
            await WaitForStateAsync(session, DictationPhase.Recording);
            Assert.Null(session.State.Outcome);
            await session.FinishAsync();
            Assert.Equal(SessionOutcomeKind.Completed, (await completed).Kind);
            Assert.Equal(SessionOutcomeKind.Completed, session.State.Outcome);
            Assert.NotNull(session.State.CompletedAt);
            Assert.Equal(session.Elapsed, session.State.Elapsed);

            var cancelled = session.StartDictationAsync();
            await WaitForStateAsync(session, DictationPhase.Recording);
            Assert.Null(session.State.Outcome);
            Assert.Null(session.State.CompletedAt);
            Assert.Equal(TimeSpan.Zero, session.State.Elapsed);
            await session.CancelAsync();
            Assert.Equal(SessionOutcomeKind.Cancelled, (await cancelled).Kind);
            Assert.Equal(SessionOutcomeKind.Cancelled, session.State.Outcome);
        }
        finally { await session.CloseAsync(); }
    }

    [Fact]
    public async Task CompletedOutcomeWaitsForTerminalSnapshotPublicationBeforeNextOperation()
    {
        var session = Create(new FakeEngine());
        try
        {
            await session.InitializeAsync();
            Task<SessionOutcome>? completion = null;
            var terminalPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completedBeforePublication = false;
            session.StateChanged += state =>
            {
                if (state.Activity == SessionActivity.Dictation && state.Outcome == SessionOutcomeKind.Completed)
                {
                    if (terminalPublished.TrySetResult())
                        completedBeforePublication = completion?.IsCompleted == true;
                }
            };

            completion = session.StartDictationAsync();
            await WaitForStateAsync(session, DictationPhase.Recording);
            await session.FinishAsync();
            await terminalPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(completedBeforePublication);
            Assert.Equal(SessionOutcomeKind.Completed, (await completion).Kind);
            Assert.Equal(SessionOutcomeKind.Completed, session.State.Outcome);

            var next = session.StartDictationAsync();
            await WaitForStateAsync(session, DictationPhase.Recording);
            Assert.Null(session.State.Outcome);
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
            var operation = session.StartDictationAsync();
            await WaitForStateAsync(session, DictationPhase.Recording);
            await session.FinishAsync();
            Assert.Equal(SessionOutcomeKind.Failed, (await operation).Kind);
            Assert.Equal(SessionOutcomeKind.Failed, session.State.Outcome);
        }
        finally
        {
            capture.FailDisposal = false;
            await session.CloseAsync();
        }
    }
}
