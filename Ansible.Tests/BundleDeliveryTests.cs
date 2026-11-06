using System.Buffers.Binary;
using System.Text;
using Ansible.Core;

namespace Ansible;

public sealed class BundleDeliveryTests
{
    [Theory]
    [InlineData(TextDeliveryStatus.Sent)]
    [InlineData(TextDeliveryStatus.Rejected)]
    [InlineData(TextDeliveryStatus.Partial)]
    public void UnverifiedTextKeepsItsClipboardAvailable(TextDeliveryStatus status)
    {
        var result = WindowsTextTarget.DeliverBundlePictures(new(status, "text"), true,
            () => throw new InvalidOperationException("Must not replace text publication"));
        Assert.Equal(status, result.Status);
        Assert.Equal(PictureDeliveryStatus.ManualRequired, result.Pictures);
    }

    [Theory]
    [InlineData(TextDeliveryStatus.Deferred)]
    [InlineData(TextDeliveryStatus.Rejected)]
    [InlineData(TextDeliveryStatus.Partial)]
    [InlineData(TextDeliveryStatus.CleanupFailed)]
    public void PictureFailureCannotTurnCommittedTextIntoRetry(TextDeliveryStatus pictureStatus)
    {
        var result = WindowsTextTarget.DeliverBundlePictures(TextDeliveryOutcome.Verified("text"), true,
            () => new(pictureStatus, "pictures"));
        Assert.Equal(TextDeliveryStatus.Verified, result.Status);
        Assert.Equal(PictureDeliveryStatus.ManualRequired, result.Pictures);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    public void HeldActivationPrimaryBlocksPicturesWithoutRetryingVerifiedText(int blockedGate, int publications)
    {
        var gate = 0;
        var published = 0;
        var sent = 0;
        TextDeliveryOutcome? Recheck() => WindowsTextTarget.RecheckInsertionKeys(
            TextInsertionMethod.Paste, true, true, 0x57,
            _ => ++gate >= blockedGate ? short.MinValue : (short)0);

        var result = WindowsTextTarget.DeliverBundlePictures(TextDeliveryOutcome.Verified("text"), true,
            () => WindowsTextTarget.PasteWithClipboard(
                () => { published++; return null; }, Recheck, () =>
                {
                    if (Recheck() is { } veto) return veto;
                    sent++;
                    return null;
                }) ?? TextDeliveryOutcome.Sent("pictures"));

        Assert.Equal(TextDeliveryStatus.Verified, result.Status);
        Assert.Equal(PictureDeliveryStatus.ManualRequired, result.Pictures);
        Assert.Equal(publications, published);
        Assert.Equal(0, sent);
    }

    [Fact]
    public void HeldActivationBeforeTextKeepsBundleRetryableWithoutStartingPictures()
    {
        var text = WindowsTextTarget.RecheckInsertionKeys(TextInsertionMethod.Paste, true, true, 0x57,
            _ => short.MinValue)!.Value;
        var result = WindowsTextTarget.DeliverBundlePictures(text, true,
            () => throw new InvalidOperationException("Pictures must wait for text."));
        Assert.Equal(text, result);
        Assert.Equal(TextDeliveryStatus.Deferred, result.Status);
        Assert.NotEqual(PictureDeliveryStatus.ManualRequired, result.Pictures);
    }

    [Fact]
    public void CancelledPictureStageKeepsTextEvidence()
    {
        var result = WindowsTextTarget.DeliverBundlePictures(TextDeliveryOutcome.Verified("text"), true,
            () => throw new OperationCanceledException());
        Assert.Equal(TextDeliveryStatus.Verified, result.Status);
        Assert.Equal(PictureDeliveryStatus.Failed, result.Pictures);
    }

    [Fact]
    public void QueuedPicturesAreNotCalledVerified()
    {
        var result = WindowsTextTarget.DeliverBundlePictures(TextDeliveryOutcome.Verified("text"), true,
            () => TextDeliveryOutcome.Sent("queued"));
        Assert.Equal(TextDeliveryStatus.Verified, result.Status);
        Assert.Equal(PictureDeliveryStatus.Queued, result.Pictures);
        Assert.Contains("unverified", result.Message);
    }

    [Fact]
    public void FileDropKeepsNumberedPicturesInOrderWithWideDoubleTerminator()
    {
        var first = Path.GetFullPath(Path.Combine("captures", "1.png"));
        var summary = Path.GetFullPath(Path.Combine("captures", "summary.png"));
        RecordFrame[] frames = [new(first, 1, TimeSpan.Zero),
            new(summary, null, TimeSpan.FromSeconds(7), true)];
        var bytes = WindowsTextTarget.PictureFileDrop(frames);
        Assert.Equal(20, BinaryPrimitives.ReadInt32LittleEndian(bytes));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16)));
        Assert.Equal(first + '\0' + summary + "\0\0", Encoding.Unicode.GetString(bytes.AsSpan(20)));
    }

    [Fact]
    public void VideoCannotEnterPictureClipboard()
    {
        Assert.Throws<InvalidDataException>(() => WindowsTextTarget.PictureFileDrop(
            [new(Path.GetFullPath(Path.Combine("captures", "take.mp4")), null, TimeSpan.Zero)]));
    }
}
