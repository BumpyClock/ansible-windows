using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Ansible.Core;

namespace Ansible;

internal static unsafe partial class WindowsTextTarget
{
    internal static TextDeliveryOutcome InsertBundle(CapturedTextTarget target, BundlePayload payload,
        CancellationToken cancellationToken, Func<bool> stillCurrent)
    {
        uint textSequence = 0;
        var text = Insert(target, payload.Text, TextInsertionMethod.Paste, 0, PasteShortcut.ControlV,
            cancellationToken, stillCurrent, sequence => textSequence = sequence);
        return DeliverBundlePictures(text, payload.Frames.Count > 0, () =>
        {
            var expectedSequence = textSequence;
            TextDeliveryOutcome? Recheck()
            {
                if (cancellationToken.IsCancellationRequested || !stillCurrent())
                    return TextDeliveryOutcome.Rejected("The take ended before pictures could be pasted.");
                if (expectedSequence == 0 || GetClipboardSequenceNumber() != expectedSequence)
                    return TextDeliveryOutcome.Rejected("The clipboard changed before pictures could be pasted.");
                if (!TryRecheckTarget(target, out var reason))
                    return TextDeliveryOutcome.Rejected(reason);
                if (!ShortcutModifiersReleased() || !PasteTriggerReleased(PasteShortcut.ControlV))
                    return TextDeliveryOutcome.Deferred("Release the keys before copying pictures.");
                return null;
            }

            var window = CreatePasteWindow();
            try
            {
                return PasteWithClipboard(() =>
                {
                    var failure = PublishPictureFiles(window, payload.Frames, privatePublication: true);
                    expectedSequence = GetClipboardSequenceNumber();
                    return failure;
                }, Recheck, () =>
                {
                    if (Recheck() is { } veto) return veto;
                    if (GetClipboardOwner() != window)
                        return TextDeliveryOutcome.Rejected("The picture clipboard owner changed.");
                    return SendPasteChord(PasteShortcut.ControlV, NativeSend, Thread.Sleep,
                        () => cancellationToken.IsCancellationRequested || !stillCurrent());
                }) ?? TextDeliveryOutcome.Sent("The picture paste shortcut was queued. Check the attachments.");
            }
            finally { DestroyWindow(window); }
        });
    }

    // Text is committed independently. A picture-stage failure must never replay that text.
    internal static TextDeliveryOutcome DeliverBundlePictures(TextDeliveryOutcome text, bool hasPictures,
        Func<TextDeliveryOutcome> pastePictures)
    {
        if (!hasPictures) return text;
        if (text.Status != TextDeliveryStatus.Verified)
            return text.Status == TextDeliveryStatus.Deferred ? text : text with
            {
                Pictures = PictureDeliveryStatus.ManualRequired,
                Message = text.Message + " Pictures remain in Dictation. Use Copy pictures after checking the text."
            };
        try
        {
            var pictures = pastePictures();
            if (pictures.Status is TextDeliveryStatus.Sent or TextDeliveryStatus.Verified)
                return text with
                {
                    Pictures = PictureDeliveryStatus.Queued,
                    Message = "Text insertion was verified. Picture paste was queued; attachment insertion is unverified."
                };
            return text with
            {
                Pictures = PictureDeliveryStatus.ManualRequired,
                Message = $"Text insertion was verified. Pictures were not confirmed. {pictures.Message} Use Copy pictures in Dictation."
            };
        }
        catch (Exception error)
        {
            return text with
            {
                Pictures = PictureDeliveryStatus.Failed,
                Message = $"Text insertion was verified. Pictures could not be pasted. {error.Message} Use Copy pictures in Dictation."
            };
        }
    }

    /// <summary>Explicit manual Copy uses the ordinary clipboard and sends no input.</summary>
    internal static void CopyPictures(IReadOnlyList<RecordFrame> frames)
    {
        var window = CreatePasteWindow();
        try
        {
            if (PublishPictureFiles(window, frames, privatePublication: false) is { } failure)
                throw new IOException(failure.Message);
        }
        finally { DestroyWindow(window); }
    }

    internal static byte[] PictureFileDrop(IReadOnlyList<RecordFrame> frames)
    {
        if (frames.Count is < 1 or > 8) throw new InvalidDataException("Copy requires one to eight pictures.");
        foreach (var frame in frames)
            if (!Path.IsPathFullyQualified(frame.Path) || frame.Path.Contains('\0') ||
                !string.Equals(Path.GetExtension(frame.Path), ".png", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The picture bundle contains an invalid PNG path.");
        var names = Encoding.Unicode.GetBytes(string.Join('\0', frames.Select(frame => frame.Path)) + "\0\0");
        var bytes = new byte[checked(20 + names.Length)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 20);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 1);
        names.CopyTo(bytes, 20);
        return bytes;
    }

    private static TextDeliveryOutcome? PublishPictureFiles(nint window, IReadOnlyList<RecordFrame> frames,
        bool privatePublication)
    {
        var files = PictureFileDrop(frames);
        if (frames.Any(frame => !File.Exists(frame.Path)))
            return TextDeliveryOutcome.Rejected("A capture picture is no longer available. No picture paste was sent.");
        var lease = WindowsRecordTakeFactory.ProtectPictureFilesAsync(frames).GetAwaiter().GetResult();
        var filesPublished = false;
        var formats = privatePublication ? new[]
        {
            RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing"),
            RegisterClipboardFormatW("CanIncludeInClipboardHistory"),
            RegisterClipboardFormatW("CanUploadToCloudClipboard"), 15u
        } : [15u];
        var handles = new nint[formats.Length];
        try
        {
            for (var index = 0; index < formats.Length; index++)
            {
                if (formats[index] == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not register a clipboard privacy format.");
                handles[index] = AllocateClipboardBytes(index == formats.Length - 1 ? files : new byte[4]);
            }
            var clock = Stopwatch.StartNew();
            while (!OpenClipboard(window))
            {
                if (clock.ElapsedMilliseconds >= 1000)
                    return TextDeliveryOutcome.Rejected("Another app is using the clipboard. No picture paste was sent.", TextDeliveryReason.ClipboardBusy);
                Thread.Sleep(10);
            }
            TextDeliveryOutcome? failure = null;
            try
            {
                if (!EmptyClipboard())
                    failure = TextDeliveryOutcome.Rejected("Windows could not prepare the picture clipboard.", TextDeliveryReason.ClipboardPreparationFailed);
                else
                    for (var index = 0; index < formats.Length; index++)
                    {
                        if (SetClipboardData(formats[index], handles[index]) == 0)
                        {
                            failure = TextDeliveryOutcome.Rejected("Windows could not publish pictures and clipboard exclusions. No picture paste was sent.", TextDeliveryReason.ClipboardPublishFailed);
                            break;
                        }
                        handles[index] = 0;
                        if (index == formats.Length - 1) filesPublished = true;
                    }
            }
            finally
            {
                if (!CloseClipboard())
                    failure = TextDeliveryOutcome.Rejected("Windows could not close the picture clipboard. No picture paste was sent.", TextDeliveryReason.ClipboardCloseFailed);
            }
            return failure;
        }
        finally
        {
            foreach (var handle in handles)
                if (handle != 0) GlobalFree(handle);
            if (filesPublished)
                WindowsRecordTakeFactory.RememberPictureClipboardAsync(lease, GetClipboardSequenceNumber()).GetAwaiter().GetResult();
            else lease?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
