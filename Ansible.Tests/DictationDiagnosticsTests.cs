using System.Text.Json;
using Ansible.Core;

namespace Ansible.Tests;

public sealed partial class DictationSessionTests
{
    [Fact]
    public async Task ShortcutMicrophoneTimingIsRecordedOnlyWhenProvided()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics-test-" + Guid.NewGuid().ToString("N"));
        var log = new LocalDiagnosticLog(directory);
        var session = new DictationSession(_ => new FakeEngine(), new FakeCaptureFactory(), new FakeInput(),
            new FakeUsageStore(), "models", new FakeSettingsStore { Settings = new() { LocalDiagnostics = true } },
            diagnostics: log);
        try
        {
            await session.InitializeAsync();
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            var work = session.StartShortcutDictationAsync(start);
            await WaitForStateAsync(session, DictationPhase.Recording);
            await session.FinishAsync();
            Assert.Equal(SessionOutcomeKind.Completed, (await work).Kind);
            await session.CloseAsync();
            await log.DisposeAsync();
            var lines = await File.ReadAllLinesAsync(Path.Combine(directory, "local-diagnostics.jsonl"));
            var entries = lines.Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var terminal = Assert.Single(entries, entry =>
                    entry.RootElement.GetProperty("Family").GetString() == "Operation" &&
                    entry.RootElement.GetProperty("Activity").GetString() == "Dictation" &&
                    entry.RootElement.GetProperty("Kind").GetString() == "Terminal");
                Assert.True(terminal.RootElement.GetProperty("ShortcutToMicrophoneMilliseconds").GetDouble() >= 0);
            }
            finally { foreach (var entry in entries) entry.Dispose(); }
        }
        finally
        {
            await session.CloseAsync();
            await log.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task OriginalCaptureFailureAndLaterRecoveryAreSeparateEvents()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics-test-" + Guid.NewGuid().ToString("N"));
        var log = new LocalDiagnosticLog(directory);
        var capture = new FakeCapture { FailDisposal = true };
        var factory = new FakeCaptureFactory
        {
            Starting = _ => Task.FromException<IAudioCapture>(
                new CaptureOwnershipException("PRIVATE-CAPTURE", capture, new IOException("PRIVATE-DRIVER")))
        };
        var session = new DictationSession(_ => new FakeEngine(), factory, new FakeInput(), new FakeUsageStore(),
            "models", new FakeSettingsStore { Settings = new() { LocalDiagnostics = true } }, diagnostics: log);
        try
        {
            await session.InitializeAsync();
            Assert.Equal(SessionOutcomeKind.Failed, (await session.StartDictationAsync()).Kind);
            await Assert.ThrowsAsync<IOException>(() => session.CloseAsync());
            capture.FailDisposal = false;
            await session.CloseAsync();
            await log.DisposeAsync();
            var lines = await File.ReadAllLinesAsync(Path.Combine(directory, "local-diagnostics.jsonl"));
            Assert.DoesNotContain("PRIVATE-", string.Join("", lines));
            var entries = lines.Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var operations = entries.Select(entry => entry.RootElement)
                    .Where(entry => entry.GetProperty("Family").GetString() == "Operation" &&
                        entry.GetProperty("Activity").GetString() == "Dictation").ToArray();
                var terminal = Assert.Single(operations, entry => entry.GetProperty("Kind").GetString() == "Terminal");
                Assert.Equal("OpenMicrophone", terminal.GetProperty("Stage").GetString());
                Assert.True(terminal.GetProperty("RetainedResources").GetBoolean());
                var recoveries = operations.Where(entry => entry.GetProperty("Kind").GetString() == "Recovery").ToArray();
                Assert.Equal(2, recoveries.Length);
                Assert.Equal("Failed", recoveries[0].GetProperty("Outcome").GetString());
                Assert.Equal("Completed", recoveries[1].GetProperty("Outcome").GetString());
                Assert.Equal(terminal.GetProperty("OperationId").GetGuid(), recoveries[1].GetProperty("OperationId").GetGuid());
            }
            finally { foreach (var entry in entries) entry.Dispose(); }
        }
        finally
        {
            capture.FailDisposal = false;
            await session.CloseAsync();
            await log.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task NativeDiagnosticContextKeepsBackendAndDropsUnrecognizedModelIdentifiers()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics-test-" + Guid.NewGuid().ToString("N"));
        var log = new LocalDiagnosticLog(directory);
        log.SetEnabled(true);
        var id = Guid.NewGuid();
        try
        {
            var context = new NativeDiagnosticContext(log, log.CurrentGeneration!.Value, id, NativeBackend.Auto)
            {
                SelectionSource = NativeDiagnosticSource.MemoryCache
            };
            var model = new AudioModel { Id = "PRIVATE-MODEL-ID", Family = "moonshine_asr" };
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            context.Write(NativeDiagnosticStage.Selection, model, start, NativeBackend.Vulkan);
            context.Write(NativeDiagnosticStage.Session, model, start, NativeBackend.Vulkan, reused: true);
            context.Write(NativeDiagnosticStage.Session, model, start, error:
                new NativeMemoryAdmissionException("PRIVATE-MEMORY-ERROR", 123, 45, 67));
            context.Write(NativeDiagnosticStage.Fallback, model, start, error:
                new NativeExecutionException("PRIVATE-NATIVE-ERROR"));
            Assert.Equal(NativeBackend.Vulkan, context.ActualBackend);
            Assert.True(context.SessionReused);
            Assert.Null(NativeDiagnosticContext.SafeModelId(model.Id));
            await log.DisposeAsync();
            var lines = await File.ReadAllLinesAsync(Path.Combine(directory, "local-diagnostics.jsonl"));
            Assert.Equal(4, lines.Length);
            Assert.DoesNotContain("PRIVATE-MODEL-ID", string.Join("", lines));
            Assert.DoesNotContain("PRIVATE-MEMORY-ERROR", string.Join("", lines));
            Assert.DoesNotContain("PRIVATE-NATIVE-ERROR", string.Join("", lines));
            using var json = JsonDocument.Parse(lines[0]);
            Assert.Equal("Native", json.RootElement.GetProperty("Family").GetString());
            Assert.Equal(id, json.RootElement.GetProperty("OperationId").GetGuid());
            Assert.Equal("MemoryCache", json.RootElement.GetProperty("Source").GetString());
            Assert.Equal("Vulkan", json.RootElement.GetProperty("ActualBackend").GetString());
            using var memory = JsonDocument.Parse(lines[2]);
            Assert.Equal(123, memory.RootElement.GetProperty("RequiredMemoryBytes").GetInt64());
            Assert.Equal(45u, memory.RootElement.GetProperty("AvailablePhysicalBytes").GetUInt64());
            Assert.Equal(67u, memory.RootElement.GetProperty("AvailableCommitBytes").GetUInt64());
            using var fallback = JsonDocument.Parse(lines[3]);
            Assert.Equal("Fallback", fallback.RootElement.GetProperty("Stage").GetString());
            Assert.Equal(JsonValueKind.Null, fallback.RootElement.GetProperty("DurationMilliseconds").ValueKind);
        }
        finally
        {
            await log.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task StorageFailuresAreStructuredAndDoNotChangeSuccessfulRecognition()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics-test-" + Guid.NewGuid().ToString("N"));
        var log = new LocalDiagnosticLog(directory);
        var session = new DictationSession(_ => new FakeEngine(), new FakeCaptureFactory(), new FakeInput(),
            new FakeUsageStore { FailRecording = true }, "PRIVATE-PATH",
            new FakeSettingsStore { Settings = new() { LocalDiagnostics = true }, FailSaving = true }, diagnostics: log);
        try
        {
            await session.InitializeAsync();
            session.SetLanguage("fr");
            var work = session.StartDictationAsync();
            await WaitForStateAsync(session, DictationPhase.Recording);
            await session.FinishAsync();
            Assert.Equal(SessionOutcomeKind.Completed, (await work).Kind);
            await session.CloseAsync();
            await log.DisposeAsync();
            var lines = await File.ReadAllLinesAsync(Path.Combine(directory, "local-diagnostics.jsonl"));
            Assert.DoesNotContain("unwritable", string.Join("", lines));
            Assert.DoesNotContain("PRIVATE-PATH", string.Join("", lines));
            var entries = lines.Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var storage = entries.Select(entry => entry.RootElement)
                    .Where(entry => entry.GetProperty("Family").GetString() == "Storage").ToArray();
                Assert.Contains(storage, entry => entry.GetProperty("Category").GetString() == "Settings" &&
                    entry.GetProperty("Outcome").GetString() == "Failed");
                Assert.Contains(storage, entry => entry.GetProperty("Category").GetString() == "Usage" &&
                    entry.GetProperty("Outcome").GetString() == "Failed");
            }
            finally { foreach (var entry in entries) entry.Dispose(); }
        }
        finally
        {
            await session.CloseAsync();
            await log.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task UnreadableSettingsCannotEnableDiagnostics()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics-test-" + Guid.NewGuid().ToString("N"));
        await using var log = new LocalDiagnosticLog(directory);
        var settings = new FakeSettingsStore { Settings = new() { LocalDiagnostics = true }, FailLoading = true };
        var session = new DictationSession(_ => new FakeEngine(), new FakeCaptureFactory(), new FakeInput(),
            new FakeUsageStore(), "models", settings, diagnostics: log);
        await session.InitializeAsync();
        await session.CloseAsync();
        Assert.False(session.State.Settings.LocalDiagnostics);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task FailedDiagnosticWriterDoesNotChangeRecognition()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics-test-" + Guid.NewGuid().ToString("N"));
        await using var log = new LocalDiagnosticLog(directory, (_, _) =>
            throw new IOException("PRIVATE-EXCEPTION-PATH"));
        var session = new DictationSession(_ => new FakeEngine(), new FakeCaptureFactory(), new FakeInput(),
            new FakeUsageStore(), "models", new FakeSettingsStore { Settings = new() { LocalDiagnostics = true } },
            diagnostics: log);
        try
        {
            await session.InitializeAsync();
            var work = session.StartDictationAsync();
            await WaitForStateAsync(session, DictationPhase.Recording);
            await session.FinishAsync();
            Assert.Equal(SessionOutcomeKind.Completed, (await work).Kind);
            await session.CloseAsync();
            await log.DisposeAsync();
            Assert.True(log.DroppedEvents > 0);
            Assert.DoesNotContain("PRIVATE-EXCEPTION-PATH", log.Error);
        }
        finally
        {
            await session.CloseAsync();
            await log.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task EntryThatCannotBeSerializedIsDroppedWhileLaterEntriesStillWrite()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics-test-" + Guid.NewGuid().ToString("N"));
        var log = new LocalDiagnosticLog(directory);
        log.SetEnabled(true);
        var generation = log.CurrentGeneration!.Value;
        var id = Guid.NewGuid();
        try
        {
            log.Write(Terminal(id, double.NaN), generation);
            log.Write(Terminal(id, 12), generation);
            await log.DisposeAsync();

            Assert.Equal(1, log.DroppedEvents);
            var line = Assert.Single(await File.ReadAllLinesAsync(Path.Combine(directory, "local-diagnostics.jsonl")));
            using var json = JsonDocument.Parse(line);
            Assert.Equal(12, json.RootElement.GetProperty("DurationMilliseconds").GetDouble());
            Assert.Equal(1, json.RootElement.GetProperty("DroppedEvents").GetInt64());
        }
        finally
        {
            await log.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task UnexpectedAppendFailureDropsOnlyThatEntryAndDisposeCompletes()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics-test-" + Guid.NewGuid().ToString("N"));
        var written = new List<string>();
        var calls = 0;
        var log = new LocalDiagnosticLog(directory, (_, line) =>
        {
            if (++calls == 1) throw new NotSupportedException("PRIVATE-EXCEPTION-PATH");
            written.Add(line);
            return Task.CompletedTask;
        });
        log.SetEnabled(true);
        var generation = log.CurrentGeneration!.Value;
        var id = Guid.NewGuid();
        try
        {
            log.Write(Terminal(id, 1), generation);
            log.Write(Terminal(id, 2), generation);
            await log.DisposeAsync();

            Assert.Equal(1, log.DroppedEvents);
            Assert.Equal(2, calls);
            Assert.DoesNotContain("PRIVATE-EXCEPTION-PATH", Assert.Single(written));
        }
        finally
        {
            await log.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static OperationDiagnostic Terminal(Guid id, double durationMilliseconds) =>
        new(id, DateTimeOffset.UtcNow, OperationDiagnosticKind.Terminal, SessionActivity.Dictation,
            OperationDiagnosticStage.Recognize, SessionOutcomeKind.Completed, DurationMilliseconds: durationMilliseconds);

    [Fact]
    public async Task DiagnosticEventsCorrelateCompletedAndCancelledOperationsWithoutContent()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics-test-" + Guid.NewGuid().ToString("N"));
        var log = new LocalDiagnosticLog(directory);
        var engine = new FakeEngine { Result = new("PRIVATE-TRANSCRIPT", "PRIVATE-TRANSCRIPT") };
        var settings = new FakeSettingsStore { Settings = new() { LocalDiagnostics = true } };
        var session = new DictationSession(_ => engine, new FakeCaptureFactory(), new FakeInput(),
            new FakeUsageStore(), "PRIVATE-PATH", settings, diagnostics: log);
        try
        {
            await session.InitializeAsync();
            var completed = session.StartDictationAsync();
            await WaitForStateAsync(session, DictationPhase.Recording);
            await session.FinishAsync();
            Assert.Equal(SessionOutcomeKind.Completed, (await completed).Kind);
            var completedId = session.State.OperationId;

            var cancelled = session.StartDictationAsync();
            await WaitForStateAsync(session, DictationPhase.Recording);
            await session.CancelAsync();
            Assert.Equal(SessionOutcomeKind.Cancelled, (await cancelled).Kind);
            var cancelledId = session.State.OperationId;
            await session.CloseAsync();
            await log.DisposeAsync();

            var lines = await File.ReadAllLinesAsync(Path.Combine(directory, "local-diagnostics.jsonl"));
            Assert.DoesNotContain("PRIVATE-TRANSCRIPT", string.Join("", lines));
            Assert.DoesNotContain("PRIVATE-PATH", string.Join("", lines));
            var entries = lines.Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var operations = entries.Select(entry => entry.RootElement)
                    .Where(entry => entry.TryGetProperty("Activity", out var activity) && activity.GetString() == "Dictation").ToArray();
                Assert.Equal(4, operations.Length);
                foreach (var (id, outcome) in new[] { (completedId, "Completed"), (cancelledId, "Cancelled") })
                {
                    var matching = operations.Where(entry => entry.GetProperty("OperationId").GetGuid() == id).ToArray();
                    Assert.Equal(2, matching.Length);
                    Assert.Equal("Starting", matching[0].GetProperty("Kind").GetString());
                    Assert.Equal("Terminal", matching[1].GetProperty("Kind").GetString());
                    Assert.Equal("Operation", matching[1].GetProperty("Family").GetString());
                    Assert.Equal("Auto", matching[1].GetProperty("RequestedBackend").GetString());
                    Assert.Equal(outcome, matching[1].GetProperty("Outcome").GetString());
                    Assert.Equal(JsonValueKind.Number, matching[1].GetProperty("DurationMilliseconds").ValueKind);
                    Assert.Equal(JsonValueKind.Null, matching[1].GetProperty("FirstPartialMilliseconds").ValueKind);
                    Assert.Equal(matching[0].GetProperty("RunId").GetGuid(), matching[1].GetProperty("RunId").GetGuid());
                }
            }
            finally { foreach (var entry in entries) entry.Dispose(); }
        }
        finally
        {
            await session.CloseAsync();
            await log.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task DeliveryRecordContainsCorrelatedOutcomeAndNoSpeechOrErrorText()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics-test-" + Guid.NewGuid().ToString("N"));
        var log = new LocalDiagnosticLog(directory);
        log.SetEnabled(true);
        var generation = log.CurrentGeneration!.Value;
        var id = Guid.NewGuid();
        try
        {
            var delivery = new FinalTextDelivery((_, _) =>
                Task.FromResult(TextDeliveryOutcome.Rejected("PRIVATE-TARGET-ERROR", TextDeliveryReason.TargetInvalid)), () => true, () => false,
                trace => log.Write(new DeliveryDiagnostic(id, DateTimeOffset.UtcNow, trace.Outcome, TextInsertionMethod.Paste,
                    PasteShortcut.ControlV, trace.ResultToDeliveryMilliseconds, trace.DeferredMilliseconds,
                    trace.ErrorHResult) { Reason = trace.Reason }, generation));
            await delivery.RunAsync(Task.FromResult(new SessionOutcome(SessionOutcomeKind.Completed,
                new("PRIVATE-DISPLAY", "PRIVATE-SPEECH"))), CancellationToken.None);
            await log.DisposeAsync();
            var line = Assert.Single(await File.ReadAllLinesAsync(Path.Combine(directory, "local-diagnostics.jsonl")));
            Assert.DoesNotContain("PRIVATE-", line);
            using var json = JsonDocument.Parse(line);
            var entry = json.RootElement;
            Assert.Equal("Delivery", entry.GetProperty("Family").GetString());
            Assert.Equal(id, entry.GetProperty("OperationId").GetGuid());
            Assert.Equal("Rejected", entry.GetProperty("Outcome").GetString());
            Assert.Equal("TargetInvalid", entry.GetProperty("Reason").GetString());
            Assert.Equal("Paste", entry.GetProperty("Method").GetString());
            Assert.Equal("ControlV", entry.GetProperty("PasteShortcut").GetString());
            Assert.True(entry.GetProperty("ResultToDeliveryMilliseconds").GetDouble() >= 0);
            Assert.Equal(0, entry.GetProperty("DeferredMilliseconds").GetDouble());
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("ErrorHResult").ValueKind);
        }
        finally
        {
            await log.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
