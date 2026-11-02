using System.Runtime.InteropServices;
using Ansible.Core;

namespace Ansible;

internal static unsafe partial class WindowsTextTarget
{
    // A 50 ms hold is a delivery policy heuristic, not evidence that an editor consumed the chord.
    internal const int PasteHoldMilliseconds = 50;
    private const uint KeyExtended = 1;

    private static uint NativeSend(KeyboardEvent[] events) =>
        SendInput((uint)events.Length, events, Marshal.SizeOf<KeyboardEvent>());

    internal static KeyboardEvent[] PasteKeyDowns(PasteShortcut shortcut) => shortcut switch
    {
        PasteShortcut.ControlV => [KeyboardEvent.Virtual(VkControl, 0), KeyboardEvent.Virtual(VkV, 0)],
        PasteShortcut.ShiftInsert => [KeyboardEvent.Virtual(0x10, 0), KeyboardEvent.Virtual(0x2D, KeyExtended)],
        PasteShortcut.ControlShiftV => [KeyboardEvent.Virtual(VkControl, 0), KeyboardEvent.Virtual(0x10, 0), KeyboardEvent.Virtual(VkV, 0)],
        _ => throw new ArgumentOutOfRangeException(nameof(shortcut))
    };

    internal static TextDeliveryOutcome? SendPasteChord(PasteShortcut shortcut,
        Func<KeyboardEvent[], uint> send, Action<int> wait, Func<bool> interrupted)
    {
        if (interrupted())
            return TextDeliveryOutcome.Rejected("The operation ended before the paste shortcut was sent.", TextDeliveryReason.Superseded);
        var downs = PasteKeyDowns(shortcut);
        // Prepare every possible partial-keydown cleanup before taking ownership of injected keys.
        var partialReleases = new KeyboardEvent[downs.Length][];
        for (var count = 1; count <= downs.Length; count++)
            partialReleases[count - 1] = downs.Take(count).Reverse().Select(KeyRelease).ToArray();
        var sent = Math.Min(send(downs), (uint)downs.Length);
        if (sent == 0)
            return TextDeliveryOutcome.Rejected("Windows did not queue the paste shortcut. Dictated text remains on the clipboard.", TextDeliveryReason.InputNotQueued);
        var releases = partialReleases[sent - 1];
        var cleaned = false;
        var stopped = false;
        try
        {
            if (sent == downs.Length) { wait(PasteHoldMilliseconds); }
            stopped = interrupted();
        }
        catch (OperationCanceledException) { stopped = true; }
        finally
        {
            // Once we own a keydown, cancellation and focus loss cannot veto its release.
            cleaned = ReleaseOwnedKeys(releases, send);
        }
        if (!cleaned)
            return TextDeliveryOutcome.CleanupFailed("Windows could not release all injected paste keys. Check keyboard state and the field; no paste will be replayed.", TextDeliveryReason.KeyReleaseFailed);
        if (sent != downs.Length)
            return TextDeliveryOutcome.Partial("Windows queued only part of the paste chord. Owned keys were released; check the field. No paste will be replayed.", TextDeliveryReason.InputPartial);
        if (stopped)
            return TextDeliveryOutcome.Sent("The paste shortcut was queued before cancellation or supersession. Owned keys were released; insertion is unverified. No paste will be replayed.", TextDeliveryReason.CancelledAfterInput);
        return null;
    }

    private static KeyboardEvent KeyRelease(KeyboardEvent key)
    {
        key.Key.Flags |= KeyUp;
        return key;
    }

    internal static bool ReleaseOwnedKeys(KeyboardEvent[] releases, Func<KeyboardEvent[], uint> send)
    {
        var released = 0;
        for (var attempt = 0; attempt < 3 && released < releases.Length; attempt++)
        {
            released += (int)Math.Min(send(releases[released..]), (uint)(releases.Length - released));
        }
        return released == releases.Length;
    }
}
