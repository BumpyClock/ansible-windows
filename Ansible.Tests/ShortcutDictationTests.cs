using System.Collections.Concurrent;
using System.Text.Json;
using Ansible.Core;

namespace Ansible.Tests;

public sealed partial class DictationSessionTests
{
    private static readonly DictationShortcut PlainShortcut = DictationShortcut.Default;
    private static readonly DictationShortcut ModifierShortcut = new(0x0002, 0x20);

    [Fact]
    public Task ToggleShortcutInsertsFinalSpeechOnceAndRecordsTheDelivery() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(new AppSettings { PushToTalk = false }, diagnostics: true);
        await harness.InitializeAsync();

        await harness.Shortcut.PressedAsync(PlainShortcut);
        await WaitForStateAsync(harness.Session, DictationPhase.Recording);
        await harness.Shortcut.ReleasedAsync();
        await harness.Shortcut.PressedAsync(PlainShortcut);
        var record = await harness.NextRecordAsync();
        await harness.CompletedAsync();
        await harness.Shortcut.JoinAsync();

        Assert.Equal("hello world", Assert.Single(harness.Inspector.Target.Inserts));
        Assert.True(Assert.Single(harness.Inspector.Target.CurrentAtInsert));
        var completion = Assert.Single(harness.Completions);
        Assert.Equal((completion.OperationId, TextDeliveryStatus.Verified), record);
        Assert.Single(harness.Recorded);
        Assert.Equal(new CaretBounds?[] { null, harness.Inspector.Target.Caret }, harness.Anchors);
        Assert.Equal(1, harness.Inspector.Inspections);
        Assert.False(harness.Shortcut.IsBusy);
        Assert.DoesNotContain(harness.Notices, IsShortcutWarning);
        Assert.Equal(new[] { "Admitted" }, await harness.CloseAsync());
    });

    [Fact]
    public Task RejectedShortcutDestinationFailsRecognitionWithoutInsertingOrRecording() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(new AppSettings { PushToTalk = false }, diagnostics: true);
        var inspection = new TaskCompletionSource<IShortcutTextTarget>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Inspector.Inspecting = () => inspection.Task;
        await harness.InitializeAsync();

        var press = harness.Shortcut.PressedAsync(PlainShortcut);
        await WaitForStateAsync(harness.Session, DictationPhase.Recording);
        inspection.SetException(new InvalidOperationException("The selected field is read-only."));
        await press;
        await harness.Shortcut.JoinAsync();

        Assert.Equal(DictationPhase.Ready, harness.Session.State.Phase);
        Assert.Empty(harness.Inspector.Target.Inserts);
        Assert.Empty(harness.Recorded);
        Assert.Empty(harness.Completions);
        Assert.Contains(harness.Notices, notice => notice.Message.Contains("read-only"));
        Assert.Equal(new[] { "AdmissionRejected" }, await harness.CloseAsync());
    });

    [Fact]
    public Task DeferredDeliveryRetriesUntilTheDestinationAcceptsTheText() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(new AppSettings { PushToTalk = false });
        harness.Inspector.Target.Inserting = attempt => attempt < 3
            ? TextDeliveryOutcome.Deferred("Modifiers held.", TextDeliveryReason.ModifiersHeld)
            : TextDeliveryOutcome.Verified("Inserted.", TextDeliveryReason.Verified);
        await harness.InitializeAsync();

        await harness.DictateToggleAsync(PlainShortcut);
        var record = await harness.NextRecordAsync();
        await harness.Shortcut.JoinAsync();

        Assert.Equal(TextDeliveryStatus.Verified, record.Status);
        Assert.Equal(3, harness.Inspector.Target.Inserts.Count);
        Assert.Single(harness.Recorded);
        Assert.DoesNotContain(harness.Notices, IsShortcutWarning);
    });

    [Theory]
    [InlineData(TextDeliveryStatus.Rejected)]
    [InlineData(TextDeliveryStatus.Partial)]
    [InlineData(TextDeliveryStatus.CleanupFailed)]
    public Task StoppedDeliveryNeverRetriesAndWarnsTheUser(TextDeliveryStatus status) => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(new AppSettings { PushToTalk = false });
        harness.Inspector.Target.Inserting = _ => new TextDeliveryOutcome(status, "The field stopped accepting input.");
        await harness.InitializeAsync();

        await harness.DictateToggleAsync(PlainShortcut);
        var record = await harness.NextRecordAsync();
        await harness.Shortcut.JoinAsync();

        Assert.Equal(status, record.Status);
        Assert.Single(harness.Inspector.Target.Inserts);
        Assert.Contains(harness.Notices, notice => notice is
        {
            Kind: NoticeKind.Warning, Title: "Insertion stopped",
            Message: "The field stopped accepting input. Check the destination before using Copy transcript; do not repeat text that already appeared."
        });
    });

    [Fact]
    public Task UnverifiedDeliveryWarnsThatTextWasSent() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(new AppSettings { PushToTalk = false });
        harness.Inspector.Target.Inserting = _ => TextDeliveryOutcome.Sent("Paste was sent.", TextDeliveryReason.SentUnverified);
        await harness.InitializeAsync();

        await harness.DictateToggleAsync(PlainShortcut);
        Assert.Equal(TextDeliveryStatus.Sent, (await harness.NextRecordAsync()).Status);
        await harness.Shortcut.JoinAsync();

        Assert.Single(harness.Inspector.Target.Inserts);
        Assert.Contains(harness.Notices, notice => notice is
            { Kind: NoticeKind.Warning, Title: "Text sent; unverified", Message: "Paste was sent." });
    });

    [Fact]
    public Task HeldModifierShortcutDefersInsertionUntilRelease() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(new AppSettings { PushToTalk = false });
        await harness.InitializeAsync();

        await harness.Shortcut.PressedAsync(ModifierShortcut);
        await WaitForStateAsync(harness.Session, DictationPhase.Recording);
        await harness.Shortcut.ReleasedAsync();
        await harness.Shortcut.PressedAsync(ModifierShortcut);
        await harness.CompletedAsync();
        await Task.Delay(150);
        Assert.Empty(harness.Inspector.Target.Inserts);
        Assert.True(harness.Shortcut.IsBusy);

        await harness.Shortcut.ReleasedAsync();
        await harness.NextRecordAsync();
        await harness.Shortcut.JoinAsync();

        Assert.Equal("hello world", Assert.Single(harness.Inspector.Target.Inserts));
    });

    [Fact]
    public Task HeldShortcutWithoutModifiersDoesNotDeferInsertion() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(new AppSettings { PushToTalk = false });
        await harness.InitializeAsync();

        await harness.Shortcut.PressedAsync(PlainShortcut);
        await WaitForStateAsync(harness.Session, DictationPhase.Recording);
        await harness.Shortcut.ReleasedAsync();
        await harness.Shortcut.PressedAsync(PlainShortcut);
        await harness.NextRecordAsync();
        await harness.Shortcut.JoinAsync();

        Assert.Equal("hello world", Assert.Single(harness.Inspector.Target.Inserts));
    });

    [Fact]
    public Task PushToTalkReleasedBeforeAdmissionNeverStartsRecording() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(new AppSettings { PushToTalk = true }, diagnostics: true);
        await harness.InitializeAsync();

        var press = harness.Shortcut.PressedAsync(PlainShortcut);
        Assert.True(harness.Shortcut.IsBusy);
        await harness.Shortcut.ReleasedAsync();
        await press;
        await harness.Shortcut.JoinAsync();

        Assert.False(harness.Shortcut.IsBusy);
        Assert.Equal(0, harness.Inspector.Inspections);
        Assert.Equal(0, harness.Capture.Starts);
        Assert.Equal(0, harness.Engine.StreamStarts);
        Assert.Equal(DictationPhase.Ready, harness.Session.State.Phase);
        Assert.Empty(harness.Inspector.Target.Inserts);
        Assert.Equal(new[] { "Stale" }, await harness.CloseAsync());
    });

    [Fact]
    public Task PushToTalkReleaseFinishesAfterTheTailAndRepressKeepsRecording() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(new AppSettings { PushToTalk = true });
        await harness.InitializeAsync();

        await harness.Shortcut.PressedAsync(PlainShortcut);
        await WaitForStateAsync(harness.Session, DictationPhase.Recording);
        var firstRelease = harness.Shortcut.ReleasedAsync();
        Assert.True(harness.Tails.TryDequeue(out var firstTail));
        await harness.Shortcut.PressedAsync(PlainShortcut);
        firstTail.SetResult();
        await firstRelease;

        Assert.Equal(DictationPhase.Recording, harness.Session.State.Phase);
        Assert.Empty(harness.Inspector.Target.Inserts);

        var secondRelease = harness.Shortcut.ReleasedAsync();
        Assert.True(harness.Tails.TryDequeue(out var secondTail));
        Assert.Equal(DictationPhase.Recording, harness.Session.State.Phase);
        secondTail.SetResult();
        await secondRelease;
        await harness.NextRecordAsync();
        await harness.Shortcut.JoinAsync();

        Assert.Equal("hello world", Assert.Single(harness.Inspector.Target.Inserts));
        Assert.All(harness.TailDurations, duration => Assert.Equal(ShortcutCaptureCoordinator.ReleaseTail, duration));
        Assert.Equal(1, harness.Engine.StreamStarts);
    });

    [Fact]
    public Task ShortcutIsBusyWhileCaptureOrDeliveryIsPending() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(new AppSettings { PushToTalk = false });
        var inspection = new TaskCompletionSource<IShortcutTextTarget>(TaskCreationOptions.RunContinuationsAsynchronously);
        var insertion = new TaskCompletionSource<TextDeliveryOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Inspector.Inspecting = () => inspection.Task;
        harness.Inspector.Target.InsertingAsync = _ => insertion.Task;
        await harness.InitializeAsync();
        Assert.False(harness.Shortcut.IsBusy);

        var press = harness.Shortcut.PressedAsync(PlainShortcut);
        Assert.True(harness.Shortcut.IsBusy);
        await WaitForStateAsync(harness.Session, DictationPhase.Recording);
        Assert.True(harness.Shortcut.IsBusy);
        inspection.SetResult(harness.Inspector.Target);
        await press;
        await harness.Shortcut.ReleasedAsync();
        await harness.Shortcut.PressedAsync(PlainShortcut);
        await harness.Inspector.Target.InsertEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(harness.Shortcut.IsBusy);

        insertion.SetResult(TextDeliveryOutcome.Verified("Inserted.", TextDeliveryReason.Verified));
        await harness.NextRecordAsync();
        await harness.Shortcut.JoinAsync();

        Assert.False(harness.Shortcut.IsBusy);
    });

    [Fact]
    public Task PressDuringDeliveryIsIgnoredAndDoesNotInsertTwice() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(new AppSettings { PushToTalk = false }, diagnostics: true);
        var insertion = new TaskCompletionSource<TextDeliveryOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Inspector.Target.InsertingAsync = _ => insertion.Task;
        await harness.InitializeAsync();

        await harness.DictateToggleAsync(PlainShortcut);
        await harness.Inspector.Target.InsertEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.Shortcut.ReleasedAsync();
        await harness.Shortcut.PressedAsync(PlainShortcut);

        Assert.Equal(1, harness.Inspector.Inspections);
        Assert.Equal(1, harness.Capture.Starts);
        insertion.SetResult(TextDeliveryOutcome.Verified("Inserted.", TextDeliveryReason.Verified));
        await harness.NextRecordAsync();
        await harness.Shortcut.JoinAsync();

        Assert.Single(harness.Inspector.Target.Inserts);
        Assert.Single(harness.Recorded);
        Assert.Equal(1, harness.Engine.StreamStarts);
        Assert.Equal(new[] { "Admitted", "DeliveryPending" }, await harness.CloseAsync());
    });

    [Fact]
    public Task ShutdownCancelsPendingDeliveryAndRecoveryAcceptsANewPress() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(new AppSettings { PushToTalk = false });
        await harness.InitializeAsync();

        await harness.Shortcut.PressedAsync(ModifierShortcut);
        await WaitForStateAsync(harness.Session, DictationPhase.Recording);
        await harness.Shortcut.ReleasedAsync();
        await harness.Shortcut.PressedAsync(ModifierShortcut);
        await harness.CompletedAsync();
        await Task.Delay(50);

        harness.Shortcut.BeginShutdown();
        await harness.Shortcut.PressedAsync(ModifierShortcut);
        await harness.Shortcut.JoinAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(harness.Shortcut.IsBusy);
        Assert.Empty(harness.Inspector.Target.Inserts);
        Assert.Empty(harness.Recorded);
        Assert.Equal(1, harness.Inspector.Inspections);
        Assert.DoesNotContain(harness.Notices, IsShortcutWarning);

        harness.Shortcut.ResumeAfterFailedShutdown();
        await harness.Shortcut.ReleasedAsync();
        await harness.DictateToggleAsync(ModifierShortcut);
        await harness.Shortcut.ReleasedAsync();
        var record = await harness.NextRecordAsync();
        await harness.CompletedAsync();
        await harness.Shortcut.JoinAsync();

        Assert.Equal("hello world", Assert.Single(harness.Inspector.Target.Inserts));
        Assert.Equal(harness.Completions.Last().OperationId, record.OperationId);
        Assert.Equal(2, harness.Inspector.Inspections);
    });

    [Fact]
    public Task ShortcutBeforeConnectionWarnsThatDictationIsUnavailable() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(new AppSettings { PushToTalk = false });

        await harness.Shortcut.PressedAsync(PlainShortcut);

        Assert.Equal(DictationPhase.Disconnected, harness.Session.State.Phase);
        Assert.Equal(new SessionNotice(NoticeKind.Warning, "Dictation unavailable",
            "Choose a speech model that supports microphone dictation before using the shortcut."), harness.Session.State.Notice);
        Assert.Equal(0, harness.Inspector.Inspections);
    });

    [Fact]
    public Task ShortcutWithIneligibleModelWarnsThatSpeechTextIsRequired() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(new AppSettings { PushToTalk = false }, new FakeEngine(), diagnostics: true);
        await harness.InitializeAsync();
        Assert.False(harness.Session.State.SelectedModel?.CanInsertDictation);

        await harness.Shortcut.PressedAsync(PlainShortcut);

        Assert.Equal(new SessionNotice(NoticeKind.Warning, "Speech text required",
            "This model does not support speech text extraction for shortcut insertion. Choose another speech model. Manual transcription remains available."),
            harness.Session.State.Notice);
        Assert.Equal(0, harness.Inspector.Inspections);
        Assert.Equal(0, harness.Capture.Starts);
        Assert.Equal(new[] { "ModelIneligible" }, await harness.CloseAsync());
    });

    [Fact]
    public Task ShortcutDuringMeetingIsRejectedWithoutDictationNotice() => UiThread.RunAsync(async () =>
    {
        await using var harness = new ShortcutHarness(new AppSettings { PushToTalk = false }, diagnostics: true);
        await harness.InitializeAsync();
        await harness.Session.ReserveMeetingAsync();

        await harness.Shortcut.PressedAsync(PlainShortcut);

        Assert.DoesNotContain(harness.Notices, notice => notice.Title == "Dictation unavailable");
        Assert.Equal(0, harness.Inspector.Inspections);
        Assert.Equal(0, harness.Capture.Starts);
        await harness.Session.ReleaseMeetingAsync();
        Assert.Equal(new[] { "SessionUnavailable" }, await harness.CloseAsync());
    });

    private static bool IsShortcutWarning(SessionNotice notice) =>
        notice.Title is "Insertion stopped" or "Text sent; unverified" or "Operation failed";

    private sealed class ShortcutHarness : IAsyncDisposable
    {
        private static readonly AudioModel InsertableModel = new() { Id = "shortcut", Family = "nemotron_asr", Mode = "streaming" };
        private readonly string? _directory;
        private readonly LocalDiagnosticLog? _log;
        private readonly SemaphoreSlim _records = new(0);
        private readonly SemaphoreSlim _completed = new(0);
        private bool _closed;

        public ShortcutHarness(AppSettings settings, FakeEngine? engine = null, bool diagnostics = false)
        {
            Engine = engine ?? new FakeEngine
            {
                Connecting = _ => Task.FromResult<IReadOnlyList<AudioModel>>([InsertableModel])
            };
            if (diagnostics)
            {
                _directory = Path.Combine(AppContext.BaseDirectory, "shortcut-diagnostics-" + Guid.NewGuid().ToString("N"));
                _log = new LocalDiagnosticLog(_directory);
                settings = settings with { LocalDiagnostics = true };
            }
            Session = new DictationSession(_ => Engine, Capture, new FakeInput(), new FakeUsageStore(), "models",
                new FakeSettingsStore { Settings = settings }, diagnostics: _log);
            Session.StateChanged += state => Notices.Enqueue(state.Notice);
            Session.DictationCompleted += completion =>
            {
                Completions.Enqueue(completion);
                _completed.Release();
            };
            Shortcut = new ShortcutDictation(Session, Inspector, _log,
                (operationId, status) =>
                {
                    Recorded.Enqueue((operationId, status));
                    _records.Release();
                },
                Anchors.Enqueue,
                (duration, token) =>
                {
                    var tail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    TailDurations.Enqueue(duration);
                    Tails.Enqueue(tail);
                    return tail.Task.WaitAsync(token);
                });
        }

        public FakeEngine Engine { get; }
        public FakeCaptureFactory Capture { get; } = new();
        public FakeShortcutInspector Inspector { get; } = new();
        public DictationSession Session { get; }
        public ShortcutDictation Shortcut { get; }
        public ConcurrentQueue<(Guid OperationId, TextDeliveryStatus Status)> Recorded { get; } = new();
        public ConcurrentQueue<DictationCompletion> Completions { get; } = new();
        public ConcurrentQueue<CaretBounds?> Anchors { get; } = new();
        public ConcurrentQueue<SessionNotice> Notices { get; } = new();
        public ConcurrentQueue<TaskCompletionSource> Tails { get; } = new();
        public ConcurrentQueue<TimeSpan> TailDurations { get; } = new();

        public async Task InitializeAsync()
        {
            await Session.InitializeAsync();
            Assert.Equal(DictationPhase.Ready, Session.State.Phase);
        }

        public async Task DictateToggleAsync(DictationShortcut shortcut)
        {
            await Shortcut.PressedAsync(shortcut);
            await WaitForStateAsync(Session, DictationPhase.Recording);
            await Shortcut.ReleasedAsync();
            await Shortcut.PressedAsync(shortcut);
        }

        public async Task CompletedAsync() =>
            Assert.True(await _completed.WaitAsync(TimeSpan.FromSeconds(5)), "No dictation completed.");

        public async Task<(Guid OperationId, TextDeliveryStatus Status)> NextRecordAsync()
        {
            Assert.True(await _records.WaitAsync(TimeSpan.FromSeconds(5)), "No delivery was recorded.");
            return Recorded.Last();
        }

        /// <summary>Closes the session and returns the shortcut diagnostic outcomes in write order.</summary>
        public async Task<string[]> CloseAsync()
        {
            await ShutdownAsync();
            if (_directory is null) return [];
            var path = Path.Combine(_directory, "local-diagnostics.jsonl");
            if (!File.Exists(path)) return [];
            var outcomes = new List<string>();
            foreach (var line in await File.ReadAllLinesAsync(path))
            {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.GetProperty("Family").GetString() == "Shortcut")
                    outcomes.Add(json.RootElement.GetProperty("Outcome").GetString()!);
            }
            return outcomes.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            try { await ShutdownAsync(); }
            finally
            {
                if (_directory is not null && Directory.Exists(_directory)) Directory.Delete(_directory, true);
            }
        }

        private async Task ShutdownAsync()
        {
            if (_closed) return;
            _closed = true;
            Shortcut.BeginShutdown();
            try { await Shortcut.JoinAsync(); }
            finally
            {
                Shortcut.Dispose();
                await Session.CloseAsync();
                if (_log is not null) await _log.DisposeAsync();
            }
        }
    }

    private sealed class FakeShortcutInspector : IShortcutTargetInspector
    {
        public FakeShortcutTarget Target { get; } = new(new CaretBounds(40, 80, 2, 18));
        public Func<Task<IShortcutTextTarget>>? Inspecting { get; set; }
        public int Inspections { get; private set; }
        public Task<IShortcutTextTarget> InspectAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            Inspections++;
            return Inspecting?.Invoke() ?? Task.FromResult<IShortcutTextTarget>(Target);
        }
    }

    private sealed class FakeShortcutTarget(CaretBounds? caret) : IShortcutTextTarget
    {
        public CaretBounds? Caret => caret;
        public ConcurrentQueue<string> Inserts { get; } = new();
        public ConcurrentQueue<bool> CurrentAtInsert { get; } = new();
        public TaskCompletionSource InsertEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<int, TextDeliveryOutcome>? Inserting { get; set; }
        public Func<int, Task<TextDeliveryOutcome>>? InsertingAsync { get; set; }

        public Task<TextDeliveryOutcome> InsertAsync(string text, Func<bool> isCurrent, CancellationToken cancellationToken)
        {
            CurrentAtInsert.Enqueue(isCurrent());
            Inserts.Enqueue(text);
            InsertEntered.TrySetResult();
            var attempt = Inserts.Count;
            if (InsertingAsync is not null) return InsertingAsync(attempt);
            return Task.FromResult(Inserting?.Invoke(attempt) ?? TextDeliveryOutcome.Verified("Inserted.", TextDeliveryReason.Verified));
        }
    }

    /// <summary>
    /// A single-threaded synchronization context that mirrors the UI thread contract, so yields and
    /// continuations interleave deterministically.
    /// </summary>
    private sealed class UiThread : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private bool _finished;

        public static Task RunAsync(Func<Task> body)
        {
            var context = new UiThread();
            var thread = new Thread(() => context.Run(body)) { IsBackground = true, Name = "Shortcut test UI thread" };
            thread.Start();
            return context._completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_gate)
            {
                if (!_finished) { _queue.Add((callback, state)); return; }
            }
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        public override SynchronizationContext CreateCopy() => this;

        private void Run(Func<Task> body)
        {
            SetSynchronizationContext(this);
            Post(_ => Start(), null);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                try { callback(state); }
                catch (Exception error) { _completion.TrySetException(error); }
            }

            async void Start()
            {
                try
                {
                    await body();
                    _completion.TrySetResult();
                }
                catch (Exception error) { _completion.TrySetException(error); }
                finally
                {
                    lock (_gate) { _finished = true; }
                    _queue.CompleteAdding();
                }
            }
        }
    }
}
