using Ansible.Core;

namespace Ansible.Tests;

public sealed class ShortcutCaptureCoordinatorTests
{
    [Fact]
    public async Task ReleaseDoesNotFinishUntilTheFullFiveHundredMillisecondTail()
    {
        var elapsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ShortcutCaptureCoordinator((duration, _) =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(500), duration);
            return elapsed.Task;
        });
        var release = coordinator.NoteReleased();

        var waiting = coordinator.WaitForReleaseTailAsync(release, CancellationToken.None);

        Assert.False(waiting.IsCompleted);
        elapsed.SetResult();
        Assert.True(await waiting);
    }

    [Fact]
    public async Task RepressInvalidatesTheOldTailAndTheNextReleaseGetsItsOwnTail()
    {
        var timers = new List<TaskCompletionSource>();
        var coordinator = new ShortcutCaptureCoordinator((duration, _) =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(500), duration);
            var timer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            timers.Add(timer);
            return timer.Task;
        });
        var first = coordinator.WaitForReleaseTailAsync(coordinator.NoteReleased(), CancellationToken.None);
        coordinator.NotePressed();
        var second = coordinator.WaitForReleaseTailAsync(coordinator.NoteReleased(), CancellationToken.None);

        timers[0].SetResult();
        Assert.False(await first);
        Assert.False(second.IsCompleted);
        timers[1].SetResult();
        Assert.True(await second);
    }

    [Fact]
    public async Task ShutdownCancellationDoesNotWaitForTheReleaseTail()
    {
        var coordinator = new ShortcutCaptureCoordinator((_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        using var cancellation = new CancellationTokenSource();
        var waiting = coordinator.WaitForReleaseTailAsync(coordinator.NoteReleased(), cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task CancellationAfterTheTimerCannotFinishCapture()
    {
        var timer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ShortcutCaptureCoordinator((_, _) => timer.Task);
        using var cancellation = new CancellationTokenSource();
        var waiting = coordinator.WaitForReleaseTailAsync(coordinator.NoteReleased(), cancellation.Token);

        cancellation.Cancel();
        timer.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task QueuedPressThenImmediateReleaseChecksOwnershipBeforeCompletedInspectionStarts(
        bool pushToTalk, bool expectedStart)
    {
        var coordinator = new ShortcutCaptureCoordinator();
        var capture = coordinator.TryBeginCapture(pushToTalk, deliveryActive: false)!;
        var resumeInspection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspection = Task.FromResult(true);

        async Task<bool> StartAfterDispatchAsync()
        {
            await resumeInspection.Task;
            return await inspection && coordinator.TryStartDelivery(capture);
        }

        var start = StartAfterDispatchAsync();
        coordinator.NoteReleased();
        resumeInspection.SetResult();

        Assert.Equal(expectedStart, await start);
        coordinator.Abandon(capture);
        Assert.False(coordinator.HasPendingCapture);
    }

    [Fact]
    public void AdmitsOneCaptureAndRejectsASecondWhileInspectionPending()
    {
        var coordinator = new ShortcutCaptureCoordinator();

        var first = coordinator.TryBeginCapture(pushToTalk: true, deliveryActive: false);

        Assert.NotNull(first);
        Assert.True(coordinator.HasPendingCapture);
        Assert.Null(coordinator.TryBeginCapture(pushToTalk: true, deliveryActive: false));
    }

    [Fact]
    public void RejectsCaptureWhileADeliveryIsActive()
    {
        var coordinator = new ShortcutCaptureCoordinator();

        Assert.Null(coordinator.TryBeginCapture(pushToTalk: false, deliveryActive: true));
        Assert.False(coordinator.HasPendingCapture);
    }

    [Fact]
    public void PushToTalkReleaseMakesThePendingCaptureStaleSoItCannotStart()
    {
        var coordinator = new ShortcutCaptureCoordinator();
        var capture = coordinator.TryBeginCapture(pushToTalk: true, deliveryActive: false)!;

        coordinator.NoteReleased();

        Assert.False(coordinator.IsCurrent(capture));
        Assert.False(coordinator.TryStartDelivery(capture));
    }

    [Fact]
    public void ToggleReleaseLeavesThePendingCaptureAbleToStart()
    {
        var coordinator = new ShortcutCaptureCoordinator();
        var capture = coordinator.TryBeginCapture(pushToTalk: false, deliveryActive: false)!;

        coordinator.NoteReleased();

        Assert.True(coordinator.IsCurrent(capture));
        Assert.True(coordinator.TryStartDelivery(capture));
        Assert.False(coordinator.HasPendingCapture);
    }

    [Fact]
    public void StartingDeliveryTransfersOwnershipAndAdmitsTheNextPress()
    {
        var coordinator = new ShortcutCaptureCoordinator();
        var first = coordinator.TryBeginCapture(pushToTalk: false, deliveryActive: false)!;

        Assert.True(coordinator.TryStartDelivery(first));

        var second = coordinator.TryBeginCapture(pushToTalk: false, deliveryActive: false);
        Assert.NotNull(second);
    }

    [Fact]
    public void AStaleAbandonCannotClearANewerOwner()
    {
        var coordinator = new ShortcutCaptureCoordinator();
        var first = coordinator.TryBeginCapture(pushToTalk: false, deliveryActive: false)!;
        Assert.True(coordinator.TryStartDelivery(first)); // ownership released to delivery
        var second = coordinator.TryBeginCapture(pushToTalk: false, deliveryActive: false)!;

        // A late callback from the first interaction must not clear the second interaction's capture.
        coordinator.Abandon(first);

        Assert.True(coordinator.IsCurrent(second));
        Assert.True(coordinator.HasPendingCapture);
    }

    [Fact]
    public void TheOwnerAbandonClearsItsOwnCapture()
    {
        var coordinator = new ShortcutCaptureCoordinator();
        var capture = coordinator.TryBeginCapture(pushToTalk: false, deliveryActive: false)!;

        coordinator.Abandon(capture);

        Assert.False(coordinator.HasPendingCapture);
    }

    [Fact]
    public void TryStartDeliveryFailsForAStaleCaptureAfterPushToTalkRelease()
    {
        var coordinator = new ShortcutCaptureCoordinator();
        var capture = coordinator.TryBeginCapture(pushToTalk: true, deliveryActive: false)!;
        coordinator.NoteReleased();

        Assert.False(coordinator.TryStartDelivery(capture));
        // The stale capture still owns the slot until its own callback abandons it.
        Assert.True(coordinator.HasPendingCapture);
        coordinator.Abandon(capture);
        Assert.False(coordinator.HasPendingCapture);
    }
}
