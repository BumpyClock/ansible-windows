using Ansible.Core;

namespace Ansible.Tests;

public sealed partial class DictationSessionTests
{
    private static readonly DictationShortcut RecordChord = new(9, 0);

    [Fact]
    public Task OtherChordCannotReleaseOrStopTheHeldRecordTake() => UiThread.RunAsync(async () =>
    {
        var media = new RecordTakeSessionTests.TakeFactory();
        await using var harness = new ShortcutHarness(new AppSettings { PushToTalk = false, RecordToggleMode = false, RecordChordEnabled = true }, recordTakeFactory: media);
        await harness.InitializeAsync();
        await harness.Shortcut.PressedAsync(RecordChord, ShortcutActivation.Record);
        await WaitForStateAsync(harness.Session, DictationPhase.Recording);
        await harness.Shortcut.ReleasedAsync(ShortcutActivation.Voice);
        await harness.Shortcut.PressedAsync(PlainShortcut, ShortcutActivation.Voice);
        Assert.Equal(DictationPhase.Recording, harness.Session.State.Phase);
        Assert.Empty(harness.Tails);
        Assert.Equal(1, harness.Inspector.Inspections);
        var release = harness.Shortcut.ReleasedAsync(ShortcutActivation.Record);
        Assert.True(harness.Tails.TryDequeue(out var tail));
        tail.SetResult();
        await release;
        await harness.NextRecordAsync();
        await harness.Shortcut.JoinAsync();
        Assert.Equal("hello world", Assert.Single(harness.Inspector.Target.Inserts));
        Assert.Equal(1, media.Capture.Stops);
        Assert.False(harness.Shortcut.HasRecordAttempt);
    });

    [Fact]
    public Task LatchModeStopsOnlyForItsOwningChord() => UiThread.RunAsync(async () =>
    {
        var media = new RecordTakeSessionTests.TakeFactory();
        await using var harness = new ShortcutHarness(new AppSettings { PushToTalk = true, RecordToggleMode = true, RecordChordEnabled = true }, recordTakeFactory: media);
        await harness.InitializeAsync();
        await harness.Shortcut.PressedAsync(RecordChord, ShortcutActivation.Record);
        await harness.Shortcut.ReleasedAsync(ShortcutActivation.Record);
        await WaitForStateAsync(harness.Session, DictationPhase.Recording);
        Assert.Empty(harness.Tails);
        await harness.Shortcut.PressedAsync(PlainShortcut, ShortcutActivation.Voice);
        await harness.Shortcut.ReleasedAsync(ShortcutActivation.Voice);
        Assert.Equal(DictationPhase.Recording, harness.Session.State.Phase);
        await harness.Shortcut.PressedAsync(RecordChord, ShortcutActivation.Record);
        await harness.Shortcut.ReleasedAsync(ShortcutActivation.Record);
        await harness.NextRecordAsync();
        await harness.Shortcut.JoinAsync();
        Assert.Single(harness.Inspector.Target.Inserts);
        Assert.Equal(1, media.Capture.Stops);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task RecordToggleDoesNotChangeVoiceActivation(bool holdVoice) => UiThread.RunAsync(async () =>
    {
        var media = new RecordTakeSessionTests.TakeFactory();
        await using var harness = new ShortcutHarness(new AppSettings
        {
            PushToTalk = holdVoice, RecordToggleMode = holdVoice, RecordChordEnabled = true
        }, recordTakeFactory: media);
        await harness.InitializeAsync();
        await harness.Shortcut.PressedAsync(PlainShortcut);
        await WaitForStateAsync(harness.Session, DictationPhase.Recording);
        var release = harness.Shortcut.ReleasedAsync();
        if (holdVoice)
        {
            Assert.True(harness.Tails.TryDequeue(out var tail));
            tail.SetResult();
            await release;
        }
        else
        {
            await release;
            Assert.Empty(harness.Tails);
            Assert.Equal(DictationPhase.Recording, harness.Session.State.Phase);
            await harness.Shortcut.PressedAsync(PlainShortcut);
        }
        await harness.NextRecordAsync();
        await harness.Shortcut.JoinAsync();
        Assert.Single(harness.Inspector.Target.Inserts);
        Assert.False(media.Started.Task.IsCompleted);
    });

    [Fact]
    public Task EscAfterRecognitionBeforeInputRetractsTheResultAndSignalsHistoryRemoval() => UiThread.RunAsync(async () =>
    {
        var media = new RecordTakeSessionTests.TakeFactory();
        await using var harness = new ShortcutHarness(new AppSettings { RecordChordEnabled = true }, recordTakeFactory: media);
        var discarded = new List<Guid>();
        harness.Session.DictationDiscarded += discarded.Add;
        await harness.InitializeAsync();
        await harness.Shortcut.PressedAsync(RecordChord, ShortcutActivation.Record);
        await WaitForStateAsync(harness.Session, DictationPhase.Recording);
        await harness.Session.FinishAsync();
        await harness.CompletedAsync();
        var completion = Assert.Single(harness.Completions);
        Assert.NotNull(harness.Session.State.Payload);
        Assert.Empty(harness.Inspector.Target.Inserts);
        await harness.Shortcut.CancelRecordAsync();
        Assert.Equal(completion.OperationId, Assert.Single(discarded));
        Assert.Null(harness.Session.State.Payload);
        Assert.Empty(harness.Session.State.Transcript);
        Assert.Empty(harness.Inspector.Target.Inserts);
    });

    [Fact]
    public Task EscAfterInputWaitsForOwnedInputAndPreservesTheResult() => UiThread.RunAsync(async () =>
    {
        var media = new RecordTakeSessionTests.TakeFactory();
        await using var harness = new ShortcutHarness(new AppSettings { PushToTalk = false, RecordToggleMode = true, RecordChordEnabled = true }, recordTakeFactory: media);
        var finishInput = new TaskCompletionSource<TextDeliveryOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Inspector.Target.InsertingAsync = _ => finishInput.Task;
        var discarded = new List<Guid>();
        harness.Session.DictationDiscarded += discarded.Add;
        await harness.InitializeAsync();
        await harness.Shortcut.PressedAsync(RecordChord, ShortcutActivation.Record);
        await harness.Shortcut.ReleasedAsync(ShortcutActivation.Record);
        await WaitForStateAsync(harness.Session, DictationPhase.Recording);
        await harness.Shortcut.PressedAsync(RecordChord, ShortcutActivation.Record);
        await harness.Shortcut.ReleasedAsync(ShortcutActivation.Record);
        await harness.Inspector.Target.InsertEntered.Task;
        var cancel = harness.Shortcut.CancelRecordAsync();
        Assert.False(cancel.IsCompleted);
        finishInput.SetResult(TextDeliveryOutcome.Sent("Queued before cancellation.", TextDeliveryReason.CancelledAfterInput));
        await cancel;
        Assert.Single(harness.Inspector.Target.Inserts);
        Assert.Empty(discarded);
        Assert.NotNull(harness.Session.State.Payload);
        Assert.Contains(harness.Notices, notice => notice.Title == "Check the destination");
    });
}
