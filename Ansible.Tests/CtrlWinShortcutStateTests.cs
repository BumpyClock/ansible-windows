using Ansible.Core;

namespace Ansible.Tests;

public sealed class CtrlWinShortcutStateTests
{
    [Theory]
    [InlineData(0xA2u, 0x5Bu)]
    [InlineData(0xA2u, 0x5Cu)]
    [InlineData(0xA3u, 0x5Bu)]
    [InlineData(0xA3u, 0x5Cu)]
    [InlineData(0x5Bu, 0xA2u)]
    [InlineData(0x5Cu, 0xA3u)]
    public void EitherSideAndPressOrderQualifiesOnceAndEitherReleaseEnds(uint first, uint second)
    {
        foreach (var release in new[] { first, second })
        {
            var state = new CtrlWinShortcutState();
            Assert.False(state.Process(first, true, false, 0));
            Assert.False(state.Process(second, true, false, 10));
            Assert.False(state.Advance(209));
            Assert.True(state.Advance(210));
            Assert.True(state.IsPressed);
            Assert.False(state.Advance(500));
            state.Process(second, true, false, 510);
            Assert.True(state.IsPressed);
            state.Process(release, false, false, 520);
            Assert.False(state.IsPressed);
            Assert.False(state.IsPending);
            state.Process(release == first ? second : first, false, false, 530);
            Assert.False(state.Advance(1000));
        }
    }

    [Theory]
    [InlineData(0x25u)]
    [InlineData(0x44u)]
    [InlineData(0xA0u)]
    [InlineData(0xA4u)]
    public void OrdinaryChordsNeverQualifyAndAreNotSwallowed(uint other)
    {
        var state = new CtrlWinShortcutState();
        state.Process(0xA2, true, false, 0);
        state.Process(0x5B, true, false, 10);
        Assert.False(state.Process(other, true, false, 20));
        Assert.False(state.Advance(500));
        Assert.False(state.Process(other, false, false, 510));
        Assert.False(state.Process(0x5B, false, false, 520));
        state.Process(0x5B, true, false, 530);
        Assert.False(state.Advance(1000));
        state.Process(0xA2, false, false, 1010);
        state.Process(0x5B, false, false, 1020);
        state.Process(0x5B, true, false, 1030);
        state.Process(0xA2, true, false, 1040);
        Assert.True(state.Advance(1240));
    }

    [Fact]
    public void ExistingPrimaryKeyBlocksTheChord()
    {
        var state = new CtrlWinShortcutState();
        state.Process(0x44, true, false, 0);
        state.Process(0xA2, true, false, 10);
        state.Process(0x5B, true, false, 20);
        Assert.False(state.Advance(500));
    }

    [Fact]
    public void InjectedKeysNeitherStartNorReleaseTheHold()
    {
        var state = new CtrlWinShortcutState();
        state.Process(0xA2, true, true, 0);
        state.Process(0x5B, true, true, 10);
        Assert.False(state.Advance(500));
        state.Process(0xA2, true, false, 510);
        state.Process(0x5B, true, false, 520);
        Assert.True(state.Advance(720));
        Assert.False(state.Process(0xA2, false, true, 730));
        Assert.False(state.Process(0x44, true, true, 740));
        Assert.True(state.IsPressed);
    }

    [Fact]
    public void QuickReleaseDoesNotStartAndBareWindowsStillOpensStart()
    {
        var state = new CtrlWinShortcutState();
        state.Process(0x5B, true, false, 0);
        Assert.False(state.Process(0x5B, false, false, 10));
        state.Process(0xA2, true, false, 20);
        state.Process(0x5B, true, false, 30);
        Assert.False(state.Process(0x5B, false, false, 40));
        Assert.False(state.Advance(500));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QualifiedWindowsReleaseIsForwardedRegardlessOfReleaseOrder(bool windowsFirst)
    {
        var state = Active();
        var first = windowsFirst ? 0x5Bu : 0xA2u;
        var second = windowsFirst ? 0xA2u : 0x5Bu;
        Assert.False(state.Process(first, false, false, 300));
        Assert.False(state.Process(second, false, false, 310));
        state.Process(0x5B, true, false, 320);
        Assert.False(state.Process(0x5B, false, false, 330));
    }

    [Theory]
    [InlineData(0xA2u, 0x5Bu)]
    [InlineData(0xA2u, 0x5Cu)]
    [InlineData(0xA3u, 0x5Bu)]
    [InlineData(0xA3u, 0x5Cu)]
    public void ForwardedPhysicalAndMaskEventsLeaveNoWindowsVisibleKeysHeld(uint control, uint windows)
    {
        foreach (var windowsFirst in new[] { false, true })
        foreach (var windowsReleasedFirst in new[] { false, true })
        {
            var keyboard = new ForwardedKeyboard();
            keyboard.Input(windowsFirst ? windows : control, true, false, 0);
            keyboard.Input(windowsFirst ? control : windows, true, false, 10);
            Assert.True(keyboard.State.Advance(210));

            // The adapter masks Start with an unassigned down/up pair, not by eating a physical release.
            keyboard.Input(0xE8, true, true, 211);
            keyboard.Input(0xE8, false, true, 212);
            Assert.True(keyboard.State.IsPressed);
            Assert.DoesNotContain(0xE8u, keyboard.WindowsVisibleDown);
            Assert.Contains(control, keyboard.WindowsVisibleDown);
            Assert.Contains(windows, keyboard.WindowsVisibleDown);

            var firstRelease = windowsReleasedFirst ? windows : control;
            keyboard.Input(firstRelease, false, false, 300);
            Assert.DoesNotContain(firstRelease, keyboard.WindowsVisibleDown);
            Assert.False(keyboard.State.IsPressed);
            keyboard.Input(windowsReleasedFirst ? control : windows, false, false, 310);
            Assert.Empty(keyboard.WindowsVisibleDown);

            keyboard.Input(0x41, true, false, 320);
            Assert.Equal(0x41u, Assert.Single(keyboard.WindowsVisibleDown));
            keyboard.Input(0x41, false, false, 330);
            Assert.Empty(keyboard.WindowsVisibleDown);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetDuringHoldCannotStrandAWindowsVisibleModifier(bool resetBeforeFirstRelease)
    {
        var keyboard = new ForwardedKeyboard();
        keyboard.Input(0x5B, true, false, 0);
        keyboard.Input(0xA2, true, false, 10);
        Assert.True(keyboard.State.Advance(210));
        if (resetBeforeFirstRelease) keyboard.State.Reset();
        keyboard.Input(0xA2, false, false, 300);
        if (!resetBeforeFirstRelease) keyboard.State.Reset();
        keyboard.Input(0x5B, false, false, 310);
        Assert.Empty(keyboard.WindowsVisibleDown);
    }

    [Fact]
    public void LaterPrimaryEndsActiveHoldButPreservesItsWindowsShortcut()
    {
        var state = Active();
        Assert.False(state.Process(0x25, true, false, 300));
        Assert.False(state.IsPressed);
        Assert.False(state.Process(0x5B, false, false, 310));
    }

    [Fact]
    public void SuspendClearsPendingOrActiveStateAndResumeWaitsForHeldKeys()
    {
        var state = Active();
        state.Reset();
        Assert.False(state.IsPressed);
        Assert.False(state.Advance(500));
        state.Reset([0xA2, 0x5B]);
        state.Process(0x5B, true, false, 510);
        Assert.False(state.Advance(1000));
        state.Process(0xA2, false, false, 1010);
        state.Process(0x5B, false, false, 1020);
        state.Process(0xA2, true, false, 1030);
        state.Process(0x5B, true, false, 1040);
        Assert.True(state.Advance(1240));
        state.Reset();
        Assert.False(state.IsPending);
        Assert.False(state.Advance(2000));
    }

    private static CtrlWinShortcutState Active()
    {
        var state = new CtrlWinShortcutState();
        state.Process(0xA2, true, false, 0);
        state.Process(0x5B, true, false, 10);
        Assert.True(state.Advance(210));
        return state;
    }

    private sealed class ForwardedKeyboard
    {
        public CtrlWinShortcutState State { get; } = new();
        public HashSet<uint> WindowsVisibleDown { get; } = [];

        public void Input(uint key, bool down, bool injected, long milliseconds)
        {
            if (State.Process(key, down, injected, milliseconds)) return;
            if (down) WindowsVisibleDown.Add(key);
            else WindowsVisibleDown.Remove(key);
        }
    }
}
