using Ansible.Core;

namespace Ansible.Tests;

public sealed class WindowsTextTargetTests
{
    [Fact]
    public void UsesFieldIdentityWhenBothProvidersExposeOne()
    {
        var target = new CapturedTextTarget(1, 2, [3, 4]);

        Assert.True(target.Matches(1, 2, [3, 4]));
        Assert.False(target.Matches(1, 2, [3, 5]));
    }

    [Fact]
    public void FallsBackToWindowAndFocusHandleWhenFieldIdentityIsUnavailable()
    {
        var target = new CapturedTextTarget(1, 2, null);

        Assert.True(target.Matches(1, 2, null));
        Assert.False(new CapturedTextTarget(1, 2, [3]).Matches(1, 2, null));
        Assert.False(target.Matches(5, 2, null));
        Assert.False(target.Matches(1, 6, null));
    }

    [Theory]
    [InlineData(unchecked((int)0x80040204))]
    [InlineData(unchecked((int)0x80040201))]
    [InlineData(unchecked((int)0x80131505))]
    [InlineData(unchecked((int)0x80010108))]
    public void TreatsUnavailableAccessibilityAsWindowOnlyFocus(int error) =>
        Assert.True(WindowsTextTarget.IsAccessibilityUnavailable(error));

    [Fact]
    public void QueuedPasteLeavesSpeechAvailableForDelayedAndRepeatedReaders()
    {
        var clipboard = "old clipboard";
        Action? queuedPaste = null;
        var reads = new List<string>();

        var result = WindowsTextTarget.PasteWithClipboard(
            () => { clipboard = "final speech"; return null; },
            () => null,
            () => { queuedPaste = () => reads.Add(clipboard); return null; });

        Assert.Null(result);
        Assert.NotNull(queuedPaste);
        Assert.Empty(reads);
        queuedPaste();
        queuedPaste();
        Assert.Equal(["final speech", "final speech"], reads);
        Assert.Equal("final speech", clipboard);
    }

    [Fact]
    public void DoesNotPublishOrPasteWhenTheTargetIsAlreadyLost()
    {
        var clipboard = "old clipboard";
        var sends = 0;
        var rejection = TextDeliveryOutcome.Rejected("Focus changed.");

        var result = WindowsTextTarget.PasteWithClipboard(
            () => { clipboard = "speech"; return null; },
            () => rejection,
            () => { sends++; return null; });

        Assert.Equal(rejection, result);
        Assert.Equal("old clipboard", clipboard);
        Assert.Equal(0, sends);
    }

    [Fact]
    public void DoesNotSendPasteWhenClipboardPublicationFails()
    {
        var sends = 0;
        var failure = TextDeliveryOutcome.Rejected("Clipboard publication failed.");

        var result = WindowsTextTarget.PasteWithClipboard(
            () => failure, () => null,
            () => { sends++; return null; });

        Assert.Equal(failure, result);
        Assert.Equal(0, sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TargetLossOrHeldModifiersAfterPublicationNeverSendsPaste(bool defer)
    {
        var published = false;
        var sends = 0;
        var veto = defer ? TextDeliveryOutcome.Deferred("Modifier held.") : TextDeliveryOutcome.Rejected("Focus changed.");

        var result = WindowsTextTarget.PasteWithClipboard(
            () => { published = true; return null; },
            () => published ? veto : null,
            () => { sends++; return null; });

        Assert.Equal(veto, result);
        Assert.True(published);
        Assert.Equal(0, sends);
    }

    [Fact]
    public void PartialSendNeverRestoresOldClipboard()
    {
        var clipboard = "old clipboard";
        var sends = 0;
        var failure = TextDeliveryOutcome.Partial("Partial send.");

        var result = WindowsTextTarget.PasteWithClipboard(
            () => { clipboard = "final speech"; return null; },
            () => null,
            () => { sends++; return failure; });

        Assert.Equal(failure, result);
        Assert.Equal(1, sends);
        Assert.Equal("final speech", clipboard);
    }

    [Fact]
    public void LaterClipboardChangesAreNotOverwrittenByCleanup()
    {
        var clipboard = "old clipboard";
        var result = WindowsTextTarget.PasteWithClipboard(
            () => { clipboard = "speech"; return null; },
            () => null,
            () => { clipboard = "new user copy"; return null; });

        Assert.Null(result);
        Assert.Equal("new user copy", clipboard);
    }

    [Fact]
    public void DoesNotHideUnexpectedAccessErrorsAsMissingAccessibility() =>
        Assert.False(WindowsTextTarget.IsAccessibilityUnavailable(unchecked((int)0x80070005)));
}
