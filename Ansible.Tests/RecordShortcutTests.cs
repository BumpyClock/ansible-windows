using Ansible.Core;

namespace Ansible.Tests;

public sealed partial class DictationSessionTests
{
    private static readonly DictationShortcut RecordChord = new(9, 0);
    private static AppSettings RecordSettings => new()
    {
        PushToTalk = false, RecordToggleMode = true, RecordChordEnabled = true
    };

    [Fact]
    public Task OtherChordCannotReleaseOrStopTheHeldRecordTake() => UiThread.RunAsync(async () =>
    {
        var media = new RecordTakeSessionTests.TakeFactory();
        await using var harness = new ShortcutHarness(RecordSettings with { RecordToggleMode = false }, recordTakeFactory: media);
        await harness.InitializeAsync();
        await harness.Shortcut.PressedAsync(RecordChord, ShortcutActivation.Record);
        await WaitForStateAsync(harness.Session, DictationPhase.Recording);
        await harness.Shortcut.ReleasedAsync(ShortcutActivation.Voice);
        await harness.Shortcut.PressedAsync(PlainShortcut, ShortcutActivation.Voice);
        Assert.Equal(DictationPhase.Recording, harness.Session.State.Phase);
        Assert.Empty(harness.Tails);
        Assert.Equal(0, harness.Inspector.Inspections);
        var release = harness.Shortcut.ReleasedAsync(ShortcutActivation.Record);
        Assert.True(harness.Tails.TryDequeue(out var tail));
        tail.SetResult();
        await release;
        await harness.CompletedAsync();
        await harness.Shortcut.JoinAsync();
        Assert.Empty(harness.Inspector.Target.Inserts);
        Assert.Equal(1, media.Capture.Stops);
        Assert.False(harness.Shortcut.HasRecordAttempt);
        await PasteRecordAsync(harness);
        Assert.Equal("hello world", Assert.Single(harness.Inspector.Target.Inserts));
    });

    [Fact]
    public Task LatchModeStopsOnlyForItsOwningChord() => UiThread.RunAsync(async () =>
    {
        var media = new RecordTakeSessionTests.TakeFactory();
        await using var harness = new ShortcutHarness(RecordSettings with { PushToTalk = true }, recordTakeFactory: media);
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
        await harness.CompletedAsync();
        await harness.Shortcut.JoinAsync();
        Assert.Empty(harness.Inspector.Target.Inserts);
        Assert.Equal(0, harness.Inspector.Inspections);
        Assert.Equal(1, media.Capture.Stops);
        await PasteRecordAsync(harness);
        Assert.Single(harness.Inspector.Target.Inserts);
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
    public Task RecordNeverInspectsEvenWhenTheForegroundInspectorWouldThrow() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(RecordSettings, recordTakeFactory: new RecordTakeSessionTests.TakeFactory());
        harness.Inspector.Inspecting = () => throw new InvalidOperationException("The foreground is not editable.");
        await harness.InitializeAsync();
        await CompleteRecordAsync(harness);
        Assert.Equal(0, harness.Inspector.Inspections);
        Assert.Empty(harness.Inspector.Target.Inserts);
        Assert.Empty(harness.Recorded);
        Assert.NotNull(harness.Session.State.Payload);
        Assert.False(harness.Shortcut.HasRecordAttempt);
        Assert.False(harness.Shortcut.IsBusy);

        await harness.Shortcut.PasteRecordPressedAsync();
        harness.Shortcut.PasteRecordReleased();
        await harness.Shortcut.JoinAsync();
        Assert.Equal(1, harness.Inspector.Inspections);
        Assert.Contains(harness.Notices, notice => notice.Title == "Record paste unavailable" && notice.Message.Contains("not editable"));
        Assert.Empty(harness.Inspector.Target.Inserts);
        harness.Inspector.Inspecting = null;
        await PasteRecordAsync(harness);
        Assert.Single(harness.Inspector.Target.Inserts);
    });

    [Fact]
    public Task RecordPasteChoosesFreshDestinationAndWaitsForPasteChordRelease() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(RecordSettings, recordTakeFactory: new RecordTakeSessionTests.TakeFactory());
        await harness.InitializeAsync();
        await CompleteRecordAsync(harness);
        var currentTarget = new FakeShortcutTarget(new CaretBounds(200, 300, 2, 18));
        harness.Inspector.Inspecting = () => Task.FromResult<IShortcutTextTarget>(currentTarget);
        await harness.Shortcut.PasteRecordPressedAsync();
        await Task.Delay(100);
        Assert.True(harness.Shortcut.IsBusy);
        Assert.Empty(currentTarget.Inserts);
        Assert.Empty(harness.Inspector.Target.Inserts);
        Assert.Equal(1, harness.Capture.Starts);
        Assert.Equal(DictationPhase.Ready, harness.Session.State.Phase);

        harness.Shortcut.PasteRecordReleased();
        await harness.NextRecordAsync();
        await harness.Shortcut.JoinAsync();
        Assert.Equal("hello world", Assert.Single(currentTarget.Inserts));
        Assert.True(Assert.Single(currentTarget.CurrentAtInsert));
        Assert.Equal(1, harness.Inspector.Inspections);
        Assert.Empty(harness.Inspector.Target.Inserts);
    });

    [Fact]
    public Task RecordPayloadRemainsCompleteAndImmutableAcrossLaterVoiceResultsAndExplicitRepeatedPastes() => UiThread.RunAsync(async () =>
    {
        var frames = new List<RecordFrame>
        {
            new("ink.png", 3, TimeSpan.FromSeconds(2)), new("summary.png", null, TimeSpan.FromSeconds(5), true)
        };
        var video = Path.Combine(Path.GetTempPath(), "record-test.mp4");
        var media = new RecordTakeSessionTests.TakeFactory { Capture = new() { Media = new(frames, video, []) } };
        await using var harness = new ShortcutHarness(RecordSettings, recordTakeFactory: media);
        await harness.InitializeAsync();
        var record = await CompleteRecordAsync(harness);
        var expected = record.Result.Payload!;
        var retained = Assert.IsType<BundlePayload>(harness.Shortcut.RecordPayload);
        var expectedFrames = frames.ToArray();
        frames.Clear();

        await harness.DictateToggleAsync(PlainShortcut);
        var voiceDelivery = await harness.NextRecordAsync();
        await harness.CompletedAsync();
        await harness.Shortcut.JoinAsync();
        Assert.NotEqual(record.OperationId, voiceDelivery.OperationId);
        Assert.False(Assert.Single(harness.Inspector.Target.Payloads).IsRecordTake);
        Assert.Same(retained, harness.Shortcut.RecordPayload);
        Assert.Equal(expectedFrames, retained.Frames);
        Assert.Equal(video, retained.VideoPath);

        await PasteRecordAsync(harness);
        await PasteRecordAsync(harness);
        var payloads = harness.Inspector.Target.Payloads.ToArray();
        Assert.Equal(3, payloads.Length);
        Assert.Same(payloads[1], payloads[2]);
        Assert.Same(retained, payloads[1]);
        Assert.Equal(expected.Text, payloads[1].Text);
        Assert.Equal(expectedFrames, payloads[1].Frames);
        Assert.Equal(video, payloads[1].VideoPath);
        Assert.True(payloads[1].IsRecordTake);
        Assert.Contains("Frames in order:", payloads[1].Text);
        Assert.Equal(new[] { voiceDelivery.OperationId, record.OperationId, record.OperationId }, harness.Recorded.Select(item => item.OperationId));
        Assert.Equal(2, harness.Capture.Starts);
        Assert.Equal(3, harness.Inspector.Inspections);
    });

    [Fact]
    public Task RecordPasteIgnoresDuplicateHeldPressAndBusyPressWithoutQueuingAnotherDelivery() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(RecordSettings, recordTakeFactory: new RecordTakeSessionTests.TakeFactory());
        await harness.InitializeAsync();
        await CompleteRecordAsync(harness);
        var finishInput = new TaskCompletionSource<TextDeliveryOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Inspector.Target.InsertingAsync = _ => finishInput.Task;
        await harness.Shortcut.PasteRecordPressedAsync();
        await harness.Shortcut.PasteRecordPressedAsync();
        Assert.Equal(1, harness.Inspector.Inspections);
        harness.Shortcut.PasteRecordReleased();
        await harness.Inspector.Target.InsertEntered.Task;
        await harness.Shortcut.PasteRecordPressedAsync();
        harness.Shortcut.PasteRecordReleased();
        Assert.Contains(harness.Notices, notice => notice.Title == "Record paste busy");
        Assert.Equal(1, harness.Inspector.Inspections);
        finishInput.SetResult(TextDeliveryOutcome.Verified("Inserted.", TextDeliveryReason.Verified));
        await harness.NextRecordAsync();
        await harness.Shortcut.JoinAsync();
        Assert.Single(harness.Inspector.Target.Inserts);
        Assert.Single(harness.Recorded);
    });

    [Theory]
    [InlineData(TextDeliveryStatus.Rejected)]
    [InlineData(TextDeliveryStatus.Partial)]
    [InlineData(TextDeliveryStatus.CleanupFailed)]
    [InlineData(TextDeliveryStatus.Sent)]
    public Task RecordPasteRetriesOnlyDeferredPreInputResults(TextDeliveryStatus terminal) => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(RecordSettings, recordTakeFactory: new RecordTakeSessionTests.TakeFactory());
        await harness.InitializeAsync();
        await CompleteRecordAsync(harness);
        harness.Inspector.Target.Inserting = attempt => attempt < 3
            ? TextDeliveryOutcome.Deferred("Modifiers held.", TextDeliveryReason.ModifiersHeld)
            : new(terminal, "Check destination.");
        await PasteRecordAsync(harness);
        Assert.Equal(3, harness.Inspector.Target.Inserts.Count);
        Assert.Equal(terminal, Assert.Single(harness.Recorded).Status);
        Assert.Contains(harness.Notices, notice => notice.Title == (terminal == TextDeliveryStatus.Sent ? "Text sent; unverified" : "Insertion stopped"));
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task RecordPasteWithoutAnEnabledCompletedTakeWarnsAndNeverCaptures(bool enabled) => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(RecordSettings with { RecordChordEnabled = enabled }, recordTakeFactory: new RecordTakeSessionTests.TakeFactory());
        await harness.InitializeAsync();
        await harness.Shortcut.PasteRecordPressedAsync();
        harness.Shortcut.PasteRecordReleased();
        await harness.Shortcut.JoinAsync();
        Assert.Contains(harness.Notices, notice => notice.Title == (enabled ? "No record take" : "Record paste unavailable"));
        Assert.Equal(0, harness.Capture.Starts);
        Assert.Equal(0, harness.Inspector.Inspections);
    });

    [Fact]
    public Task DisabledRecordPasteDoesNotUseAnAlreadyRetainedTake() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(RecordSettings, recordTakeFactory: new RecordTakeSessionTests.TakeFactory());
        await harness.InitializeAsync();
        await CompleteRecordAsync(harness);
        harness.Session.SetRecordChordEnabled(false);
        await harness.Shortcut.PasteRecordPressedAsync();
        harness.Shortcut.PasteRecordReleased();
        await harness.Shortcut.JoinAsync();
        Assert.Contains(harness.Notices, notice => notice.Title == "Record paste unavailable");
        Assert.Equal(0, harness.Inspector.Inspections);
        Assert.Empty(harness.Inspector.Target.Inserts);
    });

    [Fact]
    public Task RecordPasteShortcutCannotCollideWithVoicePasteWhenEitherPreferenceChanges() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(RecordSettings);
        await harness.InitializeAsync();
        harness.Session.SetRecordPasteShortcut(new(6, 0x56));
        Assert.Throws<InvalidDataException>(() => harness.Session.SetPasteShortcut(PasteShortcut.ControlShiftV));
        Assert.Equal(PasteShortcut.ControlV, harness.Session.State.Settings.PasteShortcut);
        harness.Session.SetRecordPasteShortcut(DictationShortcut.RecordPasteDefault);
        harness.Session.SetPasteShortcut(PasteShortcut.ControlShiftV);
        Assert.Throws<InvalidDataException>(() => harness.Session.SetRecordPasteShortcut(new(6, 0x56)));
        Assert.Equal(DictationShortcut.RecordPasteDefault, harness.Session.State.Settings.RecordPasteShortcut);
        harness.Session.SetRecordChordEnabled(false);
        harness.Session.SetRecordPasteShortcut(new(6, 0x56));
        Assert.Throws<InvalidDataException>(() => harness.Session.SetRecordChordEnabled(true));
        Assert.False(harness.Session.State.Settings.RecordChordEnabled);
    });

    [Fact]
    public Task NewRecordAdmissionClearsOldBundleAndPasteDuringCaptureIsNotQueued() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(RecordSettings, recordTakeFactory: new RecordTakeSessionTests.TakeFactory());
        var retainedChanges = new List<BundlePayload?>();
        harness.Shortcut.RecordResultChanged += () => retainedChanges.Add(harness.Shortcut.RecordPayload);
        await harness.InitializeAsync();
        await CompleteRecordAsync(harness);
        Assert.NotNull(harness.Shortcut.RecordPayload);
        Assert.NotNull(retainedChanges.Last());
        await harness.Shortcut.PressedAsync(RecordChord, ShortcutActivation.Record);
        await harness.Shortcut.ReleasedAsync(ShortcutActivation.Record);
        await WaitForStateAsync(harness.Session, DictationPhase.Recording);
        await harness.Shortcut.PasteRecordPressedAsync();
        harness.Shortcut.PasteRecordReleased();
        Assert.Contains(harness.Notices, notice => notice.Title == "Record paste busy");
        Assert.Equal(0, harness.Inspector.Inspections);
        Assert.Null(harness.Shortcut.RecordPayload);
        Assert.Null(retainedChanges.Last());
        await harness.Shortcut.CancelRecordAsync();
        await harness.Shortcut.JoinAsync();
        Assert.Empty(harness.Inspector.Target.Inserts);
        await harness.Shortcut.PasteRecordPressedAsync();
        harness.Shortcut.PasteRecordReleased();
        Assert.Contains(harness.Notices, notice => notice.Title == "No record take");
        Assert.Equal(0, harness.Inspector.Inspections);
    });

    [Fact]
    public Task EscDuringRecordDiscardsAndClearsItsOwnerWithoutLeavingAPasteableResult() => UiThread.RunAsync(async () =>
    {
        var media = new RecordTakeSessionTests.TakeFactory();
        await using var harness = new ShortcutHarness(RecordSettings, recordTakeFactory: media);
        await harness.InitializeAsync();
        await harness.Shortcut.PressedAsync(RecordChord, ShortcutActivation.Record);
        await harness.Shortcut.ReleasedAsync(ShortcutActivation.Record);
        await WaitForStateAsync(harness.Session, DictationPhase.Recording);
        await media.Started.Task;
        await harness.Shortcut.CancelRecordAsync();
        await harness.Shortcut.JoinAsync();
        Assert.True(media.Capture.Discarded);
        Assert.False(harness.Shortcut.HasRecordAttempt);
        Assert.Empty(harness.Completions);
        await harness.Shortcut.PasteRecordPressedAsync();
        harness.Shortcut.PasteRecordReleased();
        Assert.Contains(harness.Notices, notice => notice.Title == "No record take");
        Assert.Equal(0, harness.Inspector.Inspections);
        Assert.Empty(harness.Inspector.Target.Inserts);
    });

    [Fact]
    public Task EscAfterRecordCompletionDoesNotDiscardRetainedBundleOrOwnThePaste() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(RecordSettings, recordTakeFactory: new RecordTakeSessionTests.TakeFactory());
        var discarded = new List<Guid>();
        harness.Session.DictationDiscarded += discarded.Add;
        await harness.InitializeAsync();
        await CompleteRecordAsync(harness);
        Assert.False(harness.Shortcut.HasRecordAttempt);
        await harness.Shortcut.CancelRecordAsync();
        await PasteRecordAsync(harness);
        Assert.Empty(discarded);
        Assert.NotNull(harness.Session.State.Payload);
        Assert.Single(harness.Inspector.Target.Inserts);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task FailedOrEmptyRecordNeverBecomesPasteable(bool fail) => UiThread.RunAsync(async () =>
    {
        var engine = new FakeEngine
        {
            Connecting = _ => Task.FromResult<IReadOnlyList<AudioModel>>([new() { Id = "shortcut", Family = "nemotron_asr", Mode = "streaming" }]),
            Streaming = async (_, audio, token, ready) =>
            {
                ready?.Invoke();
                await foreach (var chunk in audio.ReadAllAsync(token)) { }
                if (fail) throw new InvalidOperationException("Recognition failed.");
                return new("", "");
            }
        };
        await using var harness = new ShortcutHarness(RecordSettings, engine, recordTakeFactory: new RecordTakeSessionTests.TakeFactory());
        await harness.InitializeAsync();
        await harness.Shortcut.PressedAsync(RecordChord, ShortcutActivation.Record);
        await harness.Shortcut.ReleasedAsync(ShortcutActivation.Record);
        await WaitForStateAsync(harness.Session, DictationPhase.Recording);
        await harness.Session.FinishAsync();
        await harness.Shortcut.JoinAsync();
        await harness.Shortcut.PasteRecordPressedAsync();
        harness.Shortcut.PasteRecordReleased();
        Assert.Contains(harness.Notices, notice => notice.Title == "No record take");
        Assert.False(harness.Shortcut.HasRecordAttempt);
        Assert.Equal(0, harness.Inspector.Inspections);
        Assert.Empty(harness.Inspector.Target.Inserts);
    });

    [Fact]
    public Task SessionCurrencyChangeStopsPendingRecordPaste() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(RecordSettings, recordTakeFactory: new RecordTakeSessionTests.TakeFactory());
        await harness.InitializeAsync();
        await CompleteRecordAsync(harness);
        await harness.Shortcut.PasteRecordPressedAsync();
        var next = harness.Session.StartDictationAsync();
        await WaitForStateAsync(harness.Session, DictationPhase.Recording);
        harness.Shortcut.PasteRecordReleased();
        await harness.Shortcut.JoinAsync();
        Assert.Empty(harness.Inspector.Target.Inserts);
        Assert.Empty(harness.Recorded);
        await harness.Session.CancelAsync();
        await next;
        await PasteRecordAsync(harness);
        Assert.Equal(harness.Completions.First().OperationId, Assert.Single(harness.Recorded).OperationId);
    });

    [Fact]
    public Task ShutdownCancelsRecordPasteWhileChordHeldAndRecoveryCanPasteAgain() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(RecordSettings, recordTakeFactory: new RecordTakeSessionTests.TakeFactory());
        await harness.InitializeAsync();
        await CompleteRecordAsync(harness);
        await harness.Shortcut.PasteRecordPressedAsync();
        harness.Shortcut.BeginShutdown();
        await harness.Shortcut.JoinAsync();
        harness.Shortcut.PasteRecordReleased();
        Assert.False(harness.Shortcut.IsBusy);
        Assert.Empty(harness.Inspector.Target.Inserts);
        Assert.Empty(harness.Recorded);
        harness.Shortcut.ResumeAfterFailedShutdown();
        await PasteRecordAsync(harness);
        Assert.Single(harness.Inspector.Target.Inserts);
    });

    [Fact]
    public Task ShutdownJoinsPendingRecordPasteInspectionWithoutSendingInput() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(RecordSettings, recordTakeFactory: new RecordTakeSessionTests.TakeFactory());
        await harness.InitializeAsync();
        await CompleteRecordAsync(harness);
        var inspect = new TaskCompletionSource<IShortcutTextTarget>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Inspector.Inspecting = () => inspect.Task;
        await harness.Shortcut.PasteRecordPressedAsync();
        harness.Shortcut.PasteRecordReleased();
        harness.Shortcut.BeginShutdown();
        var join = harness.Shortcut.JoinAsync();
        Assert.False(join.IsCompleted);
        inspect.SetResult(harness.Inspector.Target);
        await join;
        Assert.Empty(harness.Inspector.Target.Inserts);
        Assert.Empty(harness.Recorded);
    });

    [Fact]
    public Task ShutdownCancelsAndJoinsAlreadyEnteredRecordPasteInput() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(RecordSettings, recordTakeFactory: new RecordTakeSessionTests.TakeFactory());
        await harness.InitializeAsync();
        await CompleteRecordAsync(harness);
        var input = new TaskCompletionSource<TextDeliveryOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Inspector.Target.InsertingAsync = _ => input.Task;
        await harness.Shortcut.PasteRecordPressedAsync();
        harness.Shortcut.PasteRecordReleased();
        await harness.Inspector.Target.InsertEntered.Task;
        harness.Shortcut.BeginShutdown();
        var join = harness.Shortcut.JoinAsync();
        Assert.False(join.IsCompleted);
        Assert.True(Assert.Single(harness.Inspector.Target.InsertionTokens).IsCancellationRequested);
        Assert.False(Assert.Single(harness.Inspector.Target.CurrencyChecks)());
        input.SetResult(TextDeliveryOutcome.Sent("Input already queued.", TextDeliveryReason.CancelledAfterInput));
        await join;
        Assert.Single(harness.Inspector.Target.Inserts);
        Assert.Empty(harness.Recorded);
        Assert.False(harness.Shortcut.IsBusy);
    });

    private static async Task<DictationCompletion> CompleteRecordAsync(ShortcutHarness harness)
    {
        await harness.Shortcut.PressedAsync(RecordChord, ShortcutActivation.Record);
        await harness.Shortcut.ReleasedAsync(ShortcutActivation.Record);
        await WaitForStateAsync(harness.Session, DictationPhase.Recording);
        await harness.Shortcut.PressedAsync(RecordChord, ShortcutActivation.Record);
        await harness.Shortcut.ReleasedAsync(ShortcutActivation.Record);
        await harness.CompletedAsync();
        await harness.Shortcut.JoinAsync();
        return harness.Completions.Last();
    }

    private static async Task PasteRecordAsync(ShortcutHarness harness)
    {
        await harness.Shortcut.PasteRecordPressedAsync();
        harness.Shortcut.PasteRecordReleased();
        await harness.NextRecordAsync();
        await harness.Shortcut.JoinAsync();
    }
}
