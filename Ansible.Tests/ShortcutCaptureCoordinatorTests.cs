using Ansible.Core;

namespace Ansible.Tests;

public sealed class ShortcutCaptureCoordinatorTests
{
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
