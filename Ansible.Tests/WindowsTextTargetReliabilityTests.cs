using System.Runtime.InteropServices;
using Ansible.Core;
using static Ansible.WindowsTextTarget;

namespace Ansible.Tests;

public sealed class WindowsTextTargetReliabilityTests
{
    private static readonly CapturedTextTarget Target = new(1, 2, [3, 4]);
    private static FocusState? Focus() => new(1, 2);
    private static FocusedField Field() => new([3, 4], false, false, true);

    [Theory]
    [InlineData(TextInsertionMethod.Paste, true, false, false)]
    [InlineData(TextInsertionMethod.Paste, false, true, false)]
    [InlineData(TextInsertionMethod.Paste, true, true, true)]
    [InlineData(TextInsertionMethod.Type, true, false, true)]
    [InlineData(TextInsertionMethod.Type, false, true, false)]
    public void FinalKeyGuardDoesNotTakeOwnershipOfPhysicallyHeldPasteKeys(
        TextInsertionMethod method, bool modifiersReleased, bool triggerReleased, bool expected)
    {
        Assert.Equal(expected, InsertionKeysReleased(method, modifiersReleased, triggerReleased));
    }

    [Fact]
    public void SameWindowFieldSwitchIsRejected()
    {
        Assert.NotEmpty(RecheckTarget(Target, Focus, () => new([3, 5], false, false, true)));
        Assert.Empty(RecheckTarget(Target, Focus, Field));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginallyIdentifiableFieldCannotLoseIdentity(bool unavailable)
    {
        Assert.NotEmpty(RecheckTarget(Target, Focus, () => unavailable
            ? throw new COMException("Provider gone.", unchecked((int)0x80040201))
            : new(null, false, false, true)));
        Assert.Empty(RecheckTarget(new(1, 2, null), Focus, () => unavailable
            ? throw new COMException("Provider gone.", unchecked((int)0x80040201))
            : new(null, false, false, true)));
    }

    [Theory]
    [InlineData(unchecked((int)0x80004005))]
    [InlineData(unchecked((int)0x80070005))]
    public void KnownFocusChangeOrAccessFailureCannotBecomeWindowOnlyFallback(int error)
    {
        Assert.False(IsAccessibilityUnavailable(error));
        Assert.Throws<COMException>(() => RecheckTarget(new(1, 2, null), Focus,
            () => throw new COMException("Known validation failure.", error)));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void FreshSafetyPropertiesVetoInsertion(bool password, bool readOnly, bool enabled)
    {
        var expected = password ? "Automatic insertion is disabled for password fields." :
            readOnly ? "The selected field is read-only." : "The selected field is disabled.";
        Assert.Equal(expected, RecheckTarget(Target, Focus, () => new([3, 4], password, readOnly, enabled)));
        Assert.Equal(expected, RecheckTarget(new(1, 2, null), Focus, () => new(null, password, readOnly, enabled)));
    }

    [Fact]
    public void HandlesMustStayStableAcrossAccessibilityRead()
    {
        var reads = 0;
        Assert.NotEmpty(RecheckTarget(Target, () => ++reads == 1 ? new(1, 2) : new(1, 9), Field));
        reads = 0;
        Assert.NotEmpty(RecheckTarget(Target, () => ++reads == 1 ? new(1, 2) : null, Field));
    }

    [Theory]
    [InlineData("left right", 5, 5, "speech ", "left speech right", 12)]
    [InlineData("left old right", 5, 8, "new", "left new right", 8)]
    [InlineData("ab", 1, 1, "😀", "a😀b", 3)]
    public void VerifiesOnlyExactInsertionOrSelectionReplacement(string before, int start, int end,
        string text, string after, int caret)
    {
        Assert.True(ExactInsertion(new(before, start, end), new(after, caret, caret), text));
        Assert.Equal(TextDeliveryStatus.Verified,
            ObserveDelivery(new(before, start, end), text, () => new(after, caret, caret),
                () => true, _ => throw new InvalidOperationException("No polling needed."), () => 0).Status);
    }

    [Fact]
    public void ExistingIdenticalTextDoesNotProveDelivery()
    {
        Assert.False(ExactInsertion(new("speech", 6, 6), new("speech", 6, 6), "speech"));
        Assert.False(ExactInsertion(new("speech", 0, 6), new("speech", 6, 6), "speech"));
        Assert.False(ExactInsertion(new("ab", 0, 0), new("axb", 2, 2), "x"));
    }

    [Theory]
    [InlineData(false, 1, 2, 2)]
    [InlineData(true, 2, 2, 2)]
    [InlineData(true, 1, 1, 2)]
    [InlineData(true, 1, -1, -1)]
    [InlineData(true, 1, 9, 9)]
    public void AmbiguousTruncatedOrInvalidObservationIsNeverVerified(bool complete, int selectionCount, int start, int end)
    {
        Assert.False(ExactInsertion(new("ab", 1, 1), new("axb", start, end, complete, selectionCount), "x"));
    }

    [Fact]
    public void OversizedDocumentsAreNeverVerified()
    {
        var before = new string('a', VerificationTextBudget);
        Assert.False(ExactInsertion(new(before, 0, 0), new("x" + before, 1, 1), "x"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FocusLossOrUnsupportedProviderRemainsQueuedUnverified(bool lostFocus)
    {
        var observations = 0;
        var result = ObserveDelivery(new("ab", 1, 1), "x",
            () => { observations++; return null; }, () => !lostFocus,
            _ => throw new InvalidOperationException("Must not poll unavailable target."), () => 0);
        Assert.Equal(TextDeliveryStatus.Sent, result.Status);
        Assert.Equal(TextDeliveryReason.SentUnverified, result.Reason);
        Assert.Equal(lostFocus ? 0 : 1, observations);
    }

    [Fact]
    public void SupersessionDuringObservationCannotBeVerified()
    {
        var active = true;
        long elapsed = 0;
        var result = ObserveDelivery(new("ab", 1, 1), "x",
            () => { active = false; return new("axb", 2, 2); },
            () => active, milliseconds => elapsed += milliseconds, () => elapsed);
        Assert.Equal(TextDeliveryStatus.Sent, result.Status);
    }

    [Fact]
    public void DeadlineAfterObservationCannotBeVerified()
    {
        long elapsed = 0;
        var result = ObserveDelivery(new("ab", 1, 1), "x",
            () => { elapsed = 601; return new("axb", 2, 2); },
            () => true, milliseconds => elapsed += milliseconds, () => elapsed);
        Assert.Equal(TextDeliveryStatus.Sent, result.Status);
    }

    [Fact]
    public void UnchangedTextIsPolledOnlyWithinObservationBudget()
    {
        long elapsed = 0;
        var reads = 0;
        var result = ObserveDelivery(new("ab", 1, 1), "x",
            () => { reads++; return new("ab", 1, 1); },
            () => true, milliseconds => elapsed += milliseconds, () => elapsed);
        Assert.Equal(TextDeliveryStatus.Sent, result.Status);
        Assert.Equal(24, reads);
    }

    [Theory]
    [InlineData(PasteShortcut.ControlV, new ushort[] { 0x11, 0x56 })]
    [InlineData(PasteShortcut.ShiftInsert, new ushort[] { 0x10, 0x2D })]
    [InlineData(PasteShortcut.ControlShiftV, new ushort[] { 0x11, 0x10, 0x56 })]
    public void PasteChordsHoldThenReleaseInReverseOrder(PasteShortcut shortcut, ushort[] keys)
    {
        var calls = new List<KeyboardEvent[]>();
        var held = false;
        var result = SendPasteChord(shortcut, events =>
        {
            if (calls.Count == 1) { Assert.True(held); }
            calls.Add(events);
            return (uint)events.Length;
        }, milliseconds => { Assert.Equal(50, milliseconds); held = true; }, () => false);
        Assert.Null(result);
        Assert.Equal(keys, calls[0].Select(key => key.Key.VirtualKey));
        Assert.Equal(keys.Reverse(), calls[1].Select(key => key.Key.VirtualKey));
        foreach (var key in calls[0])
            Assert.Equal(key.Key.VirtualKey == 0x2D ? 1u : 0u, key.Key.Flags);
        foreach (var key in calls[1])
            Assert.Equal(key.Key.VirtualKey == 0x2D ? 3u : 2u, key.Key.Flags);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void PartialKeyDownReleasesOnlyOwnedKeysAndNeverReplays(int count)
    {
        var calls = new List<KeyboardEvent[]>();
        var result = SendPasteChord(PasteShortcut.ControlShiftV, events =>
        {
            calls.Add(events);
            return calls.Count == 1 ? (uint)count : (uint)events.Length;
        }, _ => throw new InvalidOperationException("Incomplete chord must not be held."), () => false);
        Assert.Equal(TextDeliveryStatus.Partial, result!.Value.Status);
        Assert.Equal(TextDeliveryReason.InputPartial, result.Value.Reason);
        Assert.Equal(2, calls.Count);
        Assert.Equal(count == 1 ? new ushort[] { 0x11 } : [0x10, 0x11],
            calls[1].Select(key => key.Key.VirtualKey));
        Assert.All(calls[1], key => Assert.Equal(2u, key.Key.Flags));
    }

    [Fact]
    public void PartialKeyUpRetriesOnlyRemainingReleases()
    {
        var calls = new List<KeyboardEvent[]>();
        var result = SendPasteChord(PasteShortcut.ControlShiftV, events =>
        {
            calls.Add(events);
            return calls.Count == 2 ? 1u : (uint)events.Length;
        }, _ => { }, () => false);
        Assert.Null(result);
        Assert.Equal(3, calls.Count);
        Assert.Equal(new ushort[] { 0x10, 0x11 }, calls[2].Select(key => key.Key.VirtualKey));
        Assert.All(calls.Skip(1).SelectMany(call => call), key => Assert.Equal(2u, key.Key.Flags));
    }

    [Fact]
    public void FailedReleasesAreBoundedAndReportedWithoutPasteReplay()
    {
        var calls = new List<KeyboardEvent[]>();
        var result = SendPasteChord(PasteShortcut.ControlV, events =>
        {
            calls.Add(events);
            return calls.Count == 1 ? (uint)events.Length : 0;
        }, _ => { }, () => false);
        Assert.Equal(TextDeliveryStatus.CleanupFailed, result!.Value.Status);
        Assert.Equal(TextDeliveryReason.KeyReleaseFailed, result.Value.Reason);
        Assert.Equal(4, calls.Count);
        Assert.All(calls.Skip(1).SelectMany(call => call), key => Assert.Equal(2u, key.Key.Flags));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterKeyDownAlwaysCleansAndKeepsQueuedOutcome(bool throwDuringHold)
    {
        var calls = new List<KeyboardEvent[]>();
        var cancelled = false;
        var result = SendPasteChord(PasteShortcut.ControlV, events =>
        {
            calls.Add(events);
            return (uint)events.Length;
        }, _ =>
        {
            cancelled = true;
            if (throwDuringHold) { throw new OperationCanceledException(); }
        }, () => cancelled);
        Assert.Equal(TextDeliveryStatus.Sent, result!.Value.Status);
        Assert.Equal(TextDeliveryReason.CancelledAfterInput, result.Value.Reason);
        Assert.Equal(2, calls.Count);
        Assert.All(calls[1], key => Assert.Equal(2u, key.Key.Flags));
    }

    [Fact]
    public void ZeroQueuedKeysDoNotInventOwnedModifiers()
    {
        var calls = 0;
        Assert.Equal(TextDeliveryStatus.Rejected, SendPasteChord(PasteShortcut.ControlV,
            _ => { calls++; return 0; }, _ => { }, () => false)!.Value.Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CancellationBeforeKeyDownSendsNothing()
    {
        Assert.Equal(TextDeliveryStatus.Rejected, SendPasteChord(PasteShortcut.ControlV,
            _ => throw new InvalidOperationException("Must not send."), _ => { }, () => true)!.Value.Status);
    }

    [Fact]
    public void PartialReleaseFollowedByFailureNeverRetriesSuccessfulKeys()
    {
        var calls = new List<KeyboardEvent[]>();
        var result = SendPasteChord(PasteShortcut.ControlShiftV, events =>
        {
            calls.Add(events);
            return calls.Count == 1 ? (uint)events.Length : calls.Count == 2 ? 2u : 0u;
        }, _ => { }, () => false);
        Assert.Equal(TextDeliveryStatus.CleanupFailed, result!.Value.Status);
        Assert.Equal(4, calls.Count);
        Assert.All(calls.Skip(2), events =>
        {
            Assert.Single(events);
            Assert.Equal((ushort)0x11, events[0].Key.VirtualKey);
            Assert.Equal(2u, events[0].Key.Flags);
        });
    }

    [Theory]
    [InlineData(0, TextDeliveryStatus.Rejected)]
    [InlineData(2, TextDeliveryStatus.Partial)]
    public void UnicodeZeroOrPairedPartialNeedsNoAdditionalRelease(uint count, TextDeliveryStatus status)
    {
        var calls = 0;
        var input = new[] { KeyboardEvent.Unicode('x', 4), KeyboardEvent.Unicode('x', 6),
            KeyboardEvent.Unicode('y', 4), KeyboardEvent.Unicode('y', 6) };
        var result = SendUnicodeEvents(input, _ => { calls++; return count; });
        Assert.Equal(status, result!.Value.Status);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PartialUnicodeBatchCleansOnlyUnpairedKeyUp(bool cleanupSucceeds)
    {
        var events = new[] { KeyboardEvent.Unicode('x', 4), KeyboardEvent.Unicode('x', 6),
            KeyboardEvent.Unicode('y', 4), KeyboardEvent.Unicode('y', 6) };
        var calls = new List<KeyboardEvent[]>();
        var result = SendUnicodeEvents(events, keys =>
        {
            calls.Add(keys);
            return calls.Count == 1 ? 3u : cleanupSucceeds ? 1u : 0u;
        });
        Assert.Equal(cleanupSucceeds ? TextDeliveryStatus.Partial : TextDeliveryStatus.CleanupFailed, result!.Value.Status);
        Assert.All(calls.Skip(1), keys =>
        {
            Assert.Single(keys);
            Assert.Equal((ushort)'y', keys[0].Key.ScanCode);
            Assert.Equal(6u, keys[0].Key.Flags);
        });
        Assert.Equal(cleanupSucceeds ? 2 : 4, calls.Count);
    }
}
