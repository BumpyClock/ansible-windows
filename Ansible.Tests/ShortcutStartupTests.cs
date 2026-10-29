using System.Collections.Concurrent;
using Ansible.Core;

namespace Ansible.Tests;

public sealed partial class DictationSessionTests
{
    [Fact]
    public async Task ShortcutCapturesEarlyPacketsBeforeTargetInspectionCompletes()
    {
        var target = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new ConcurrentQueue<byte[]>();
        var engine = new FakeEngine
        {
            Streaming = async (_, audio, token, ready) =>
            {
                ready?.Invoke();
                await foreach (var packet in audio.ReadAllAsync(token)) received.Enqueue(packet);
                return new("first word retained", "first word retained");
            }
        };
        var capture = new FakeCapture();
        var session = Create(engine, new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(capture) });
        try
        {
            await session.InitializeAsync();
            var recording = session.StartShortcutDictationAsync(null, target.Task);
            await WaitForStateAsync(session, DictationPhase.Recording);
            Assert.False(target.Task.IsCompleted);
            Assert.True(session.State.IsCaptureOpen);
            Assert.True(capture.Packets.Writer.TryWrite([1, 2]));
            Assert.True(capture.Packets.Writer.TryWrite([3, 4]));
            await session.FinishAsync();
            Assert.Empty(received);
            Assert.False(recording.IsCompleted);
            target.SetResult();

            var outcome = await recording.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(SessionOutcomeKind.Completed, outcome.Kind);
            Assert.Equal("first word retained", outcome.Result?.SpeechText);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, received.SelectMany(packet => packet).ToArray());
            Assert.True(capture.IsReleased);
        }
        finally { await session.CloseAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedShortcutTargetDiscardsBufferedAudioAndNeverCompletesDictation(bool failedRelease)
    {
        var target = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new ConcurrentQueue<byte[]>();
        var engine = new FakeEngine
        {
            Streaming = async (_, audio, token, ready) =>
            {
                ready?.Invoke();
                await foreach (var packet in audio.ReadAllAsync(token)) received.Enqueue(packet);
                return new("must not be saved", "must not be saved");
            }
        };
        var capture = new FakeCapture { FailDisposal = failedRelease };
        var session = Create(engine, new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(capture) });
        var completions = new List<DictationCompletion>();
        session.DictationCompleted += completions.Add;
        try
        {
            await session.InitializeAsync();
            var recording = session.StartShortcutDictationAsync(null, target.Task);
            await WaitForStateAsync(session, DictationPhase.Recording);
            Assert.True(capture.Packets.Writer.TryWrite([1, 2]));
            target.SetException(new InvalidOperationException("The selected field is read-only."));

            var outcome = await recording.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(SessionOutcomeKind.Failed, outcome.Kind);
            Assert.NotNull(outcome.Error);
            Assert.Contains("read-only", outcome.Error.ToString());
            if (!failedRelease) Assert.Contains("read-only", session.State.Notice.Message);
            Assert.Null(outcome.Result);
            Assert.Empty(session.State.Transcript);
            Assert.Empty(received);
            Assert.Empty(completions);
            Assert.Equal(!failedRelease, capture.IsReleased);
            if (failedRelease)
            {
                Assert.Equal(DictationPhase.RecoveryRequired, session.State.Phase);
                Assert.Throws<InvalidOperationException>(() => { _ = session.StartDictationAsync(); });
            }
        }
        finally
        {
            capture.FailDisposal = false;
            await session.CloseAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelOrCloseReleasesShortcutCaptureWithoutWaitingForTargetInspection(bool close)
    {
        var target = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new FakeCapture();
        var session = Create(new FakeEngine(),
            new FakeCaptureFactory { Starting = _ => Task.FromResult<IAudioCapture>(capture) });
        try
        {
            await session.InitializeAsync();
            var recording = session.StartShortcutDictationAsync(null, target.Task);
            await WaitForStateAsync(session, DictationPhase.Recording);
            Assert.True(capture.Packets.Writer.TryWrite([1, 2]));

            if (close) await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));
            else await session.CancelAsync().WaitAsync(TimeSpan.FromSeconds(2));
            var outcome = await recording.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(SessionOutcomeKind.Cancelled, outcome.Kind);
            Assert.False(target.Task.IsCompleted);
            Assert.Null(outcome.Result);
            Assert.True(capture.IsReleased);
            Assert.Empty(session.State.Transcript);
        }
        finally { await session.CloseAsync(); }
    }
}
