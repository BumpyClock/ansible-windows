using System.Threading.Channels;
using Ansible.Core;
using Capture = Ansible.Tests.FakeMeetingCapture;
using CaptureFactory = Ansible.Tests.FakeMeetingCaptureFactory;

namespace Ansible.Tests;

public sealed class MeetingSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Ansible-meeting-session-" + Guid.NewGuid().ToString("N"));
    private static readonly MeetingCaptureRequest Request = new();

    [Fact]
    public async Task AutomaticCleanupRunsOnceAfterOriginalIsSavedAndUsesStartSettings()
    {
        var store = new MeetingStore(_directory);
        var engine = new Engine();
        var settings = new MeetingCleanupSettings { Automatic = true, CopilotModel = "start-model" };
        var calls = 0;
        var cleanup = new Cleanup(async (document, options, _) =>
        {
            calls++;
            Assert.Equal("start-model", options.Model);
            Assert.Equal(MeetingStatus.Completed, document.Status);
            Assert.True(engine.Disposed);
            var original = await store.LoadAsync(document.Id);
            Assert.Equal("raw words", Assert.Single(original.Turns).Text);
            Assert.Null(original.Turns[0].OriginalText);
            return ["Raw words."];
        });
        await using var session = new MeetingSession(() => engine, new CaptureFactory(), store, () => new(),
            textCleanup: cleanup, getCleanupSettings: () => settings);
        await session.StartAsync(Request);
        settings = settings with { CopilotModel = "changed-model", Automatic = false };
        engine.Report("Speaker 0: raw words");
        await session.EndAsync();
        await session.EndAsync();
        Assert.Equal(1, calls);
        Assert.Equal(MeetingTextCleanupState.Completed, session.State.TextCleanup);
        var saved = await store.LoadAsync(session.Document!.Id);
        Assert.Equal("Raw words.", Assert.Single(saved.Turns).Text);
        Assert.Equal("raw words", saved.Turns[0].OriginalText);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task DisabledOrDiscardedMeetingsNeverInvokeCleanup(bool enabled, bool discard)
    {
        var engine = new Engine();
        await using var session = new MeetingSession(() => engine, new CaptureFactory(), new(_directory), () => new(),
            textCleanup: new Cleanup((_, _, _) => throw new InvalidOperationException("Must not invoke")),
            getCleanupSettings: () => new() { Automatic = enabled });
        await session.StartAsync(Request);
        engine.Report("Speaker 0: raw words");
        if (discard) await session.DiscardAsync();
        else await session.EndAsync();
        Assert.Equal(MeetingTextCleanupState.None, session.State.TextCleanup);
        Assert.Null(session.Document!.Turns.FirstOrDefault()?.OriginalText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOrFailedMeetingsNeverInvokeCleanup(bool fail)
    {
        var engine = new Engine { ConnectFailure = fail ? new InvalidOperationException("Recognition unavailable") : null };
        await using var session = new MeetingSession(() => engine, new CaptureFactory(), new(_directory), () => new(),
            textCleanup: new Cleanup((_, _, _) => throw new InvalidOperationException("Must not invoke")),
            getCleanupSettings: () => new() { Automatic = true });
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(Request));
        else
        {
            await session.StartAsync(Request);
            engine.Report("");
        }
        await session.EndAsync();
        Assert.Equal(MeetingTextCleanupState.None, session.State.TextCleanup);
    }

    [Fact]
    public async Task ProviderFailureKeepsCompletedOriginalAndOffersSeparateCleanupError()
    {
        var engine = new Engine();
        var store = new MeetingStore(_directory);
        await using var session = new MeetingSession(() => engine, new CaptureFactory(), store, () => new(),
            textCleanup: new Cleanup((_, _, _) => throw new InvalidOperationException("Check provider access.")),
            getCleanupSettings: () => new() { Automatic = true });
        await session.StartAsync(Request);
        engine.Report("Speaker 0: raw words");
        await session.EndAsync();
        Assert.Equal(MeetingSessionPhase.Completed, session.State.Phase);
        Assert.Null(session.State.Error);
        Assert.Equal(MeetingTextCleanupState.Failed, session.State.TextCleanup);
        Assert.Equal("Check provider access.", session.State.TextCleanupError);
        var saved = await store.LoadAsync(session.Document!.Id);
        Assert.Equal(MeetingStatus.Completed, saved.Status);
        Assert.Equal("raw words", Assert.Single(saved.Turns).Text);
        Assert.Null(saved.Turns[0].OriginalText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndShutdownJoinCleanupAndPreserveOriginal(bool dispose)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = false;
        var engine = new Engine();
        var store = new MeetingStore(_directory);
        await using var session = new MeetingSession(() => engine, new CaptureFactory(), store, () => new(),
            textCleanup: new Cleanup(async (_, _, token) =>
            {
                entered.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); return []; }
                finally { exited = true; }
            }), getCleanupSettings: () => new() { Automatic = true });
        await session.StartAsync(Request);
        engine.Report("Speaker 0: raw words");
        var finish = session.EndAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MeetingTextCleanupState.Running, session.State.TextCleanup);
        Assert.False(finish.IsCompleted);
        if (dispose) await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        else session.CancelTextCleanup();
        await finish.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(exited);
        Assert.Equal(MeetingTextCleanupState.Cancelled, session.State.TextCleanup);
        Assert.Equal("raw words", Assert.Single((await store.LoadAsync(session.Document!.Id)).Turns).Text);
    }

    [Fact]
    public async Task DisposalBeginningBeforeCleanupStartsNeverInvokesTheProvider()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedWrites = 0;
        var providerCalls = 0;
        var engine = new Engine();
        var store = new MeetingStore(_directory);
        await using var session = new MeetingSession(() => engine, new CaptureFactory(), store, () => new(),
            textCleanup: new Cleanup((_, _, _) =>
            {
                Interlocked.Increment(ref providerCalls);
                throw new InvalidOperationException("Must not invoke");
            }),
            getCleanupSettings: () => new() { Automatic = true },
            writeMeeting: async document =>
            {
                if (document.Status == MeetingStatus.Completed && Interlocked.Increment(ref completedWrites) == 1)
                {
                    entered.SetResult();
                    await release.Task;
                }
                await store.SaveAsync(document);
            });
        await session.StartAsync(Request);
        engine.Report("Speaker 0: raw words");
        var finish = session.EndAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dispose = session.DisposeAsync().AsTask();
        release.SetResult();
        await Task.WhenAll(finish, dispose).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, providerCalls);
        Assert.Equal(MeetingTextCleanupState.None, session.State.TextCleanup);
        var saved = await store.LoadAsync(session.Document!.Id);
        Assert.Equal("raw words", Assert.Single(saved.Turns).Text);
        Assert.Null(saved.Turns[0].OriginalText);
    }

    private sealed class Cleanup(Func<MeetingDocument, MeetingCleanupOptions, CancellationToken, Task<IReadOnlyList<string>>> clean)
        : IMeetingTextCleanup
    {
        public Task<IReadOnlyList<string>> CleanAsync(MeetingDocument document, MeetingCleanupOptions options,
            CancellationToken cancellationToken = default) => clean(document, options, cancellationToken);
    }

    [Fact]
    public async Task CaptureHealthPropagatesWithoutBeingSavedAndResumeUsesNewCapture()
    {
        var captures = new CaptureFactory();
        var store = new MeetingStore(_directory);
        var engine = new Engine();
        await using var log = new LocalDiagnosticLog(Path.Combine(_directory, "diagnostics"));
        log.SetEnabled(true);
        await using var session = new MeetingSession(() => engine, captures, store, () => new(), diagnostics: log);
        await session.StartAsync(Request);
        var source = new MeetingAudioSourceHealth(0, 0, 0, 0, 0, 0, 0, null, null);
        var first = new MeetingCaptureHealth(16, 1, 0, null, source, source, null)
            { CaptureId = Guid.NewGuid() };
        captures.Captures[0].ReportHealth(first);
        Assert.Equal(first, session.State.CaptureHealth);
        captures.Captures[0].Send(3200);
        await WaitUntilAsync(() => engine.ReadBytes == 3200);
        engine.Report("Speaker 0: PRIVATE-TRANSCRIPT-SENTINEL");
        await session.PauseAsync();
        captures.Captures[0].ReportHealth(first with { ElapsedSeconds = 99 });
        Assert.Equal(first, session.State.CaptureHealth);
        await session.ResumeAsync();
        var second = first with { ElapsedSeconds = 0, CaptureId = Guid.NewGuid() };
        captures.Captures[1].ReportHealth(second);
        Assert.Equal(second, session.State.CaptureHealth);
        await session.EndAsync();
        await log.DisposeAsync();
        var saved = await File.ReadAllTextAsync(Path.Combine(_directory, session.Document!.Id.ToString("N") + ".json"));
        Assert.DoesNotContain("CaptureHealth", saved);
        Assert.DoesNotContain(first.CaptureId.ToString(), saved);
        var diagnosticText = await File.ReadAllTextAsync(Path.Combine(log.DirectoryPath, "local-diagnostics.jsonl"));
        Assert.Contains(first.CaptureId.ToString(), diagnosticText);
        Assert.Contains(second.CaptureId.ToString(), diagnosticText);
        Assert.DoesNotContain("PRIVATE-TRANSCRIPT-SENTINEL", diagnosticText);
        var lifecycle = new List<System.Text.Json.JsonElement>();
        foreach (var line in diagnosticText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            using var json = System.Text.Json.JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.GetProperty("Family").GetString() == "MeetingCapture")
                Assert.Equal(System.Text.Json.JsonValueKind.Null,
                    root.GetProperty("Pipeline").GetProperty("ProcessedSeconds").ValueKind);
            else if (root.GetProperty("Family").GetString() == "Meeting")
                lifecycle.Add(root.Clone());
        }
        var operation = Assert.Single(lifecycle, entry => entry.GetProperty("Kind").GetString() == "Starting")
            .GetProperty("OperationId").GetGuid();
        Assert.All(lifecycle, entry => Assert.Equal(operation, entry.GetProperty("OperationId").GetGuid()));
        Assert.Single(lifecycle, entry => entry.GetProperty("Kind").GetString() == "Paused");
        Assert.Single(lifecycle, entry => entry.GetProperty("Kind").GetString() == "Resumed");
        Assert.Equal("Completed", Assert.Single(lifecycle, entry => entry.GetProperty("Kind").GetString() == "Terminal")
            .GetProperty("Outcome").GetString());
    }

    [Fact]
    public async Task InvalidCaptureRequestHasCorrelatedFailureWithoutProcessId()
    {
        await using var log = new LocalDiagnosticLog(Path.Combine(_directory, "diagnostics"));
        log.SetEnabled(true);
        await using var session = new MeetingSession(() => new Engine(), new CaptureFactory(), new MeetingStore(_directory),
            () => new(), diagnostics: log);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.StartAsync(new(-23)));
        await log.DisposeAsync();
        var lines = await File.ReadAllLinesAsync(Path.Combine(log.DirectoryPath, "local-diagnostics.jsonl"));
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain("ProcessId", string.Join("", lines));
        using var start = System.Text.Json.JsonDocument.Parse(lines[0]);
        using var terminal = System.Text.Json.JsonDocument.Parse(lines[1]);
        Assert.Equal("Starting", start.RootElement.GetProperty("Kind").GetString());
        Assert.Equal("Failed", terminal.RootElement.GetProperty("Outcome").GetString());
        Assert.Equal(start.RootElement.GetProperty("OperationId").GetGuid(),
            terminal.RootElement.GetProperty("OperationId").GetGuid());
    }

    [Fact]
    public async Task DiagnosticRecordsFailureBeforeCaptureWithoutPrivateError()
    {
        await using var log = new LocalDiagnosticLog(Path.Combine(_directory, "diagnostics"));
        log.SetEnabled(true);
        await using var session = new MeetingSession(() => new Engine(), new CaptureFactory(), new MeetingStore(_directory),
            () => throw new InvalidOperationException("PRIVATE-CONTENT"), diagnostics: log);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(Request));
        await session.DisposeAsync();
        await log.DisposeAsync();
        var lines = await File.ReadAllLinesAsync(Path.Combine(log.DirectoryPath, "local-diagnostics.jsonl"));
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain("PRIVATE-CONTENT", string.Join("", lines));
        using var start = System.Text.Json.JsonDocument.Parse(lines[0]);
        using var end = System.Text.Json.JsonDocument.Parse(lines[1]);
        Assert.Equal("Starting", start.RootElement.GetProperty("Kind").GetString());
        Assert.Equal("Terminal", end.RootElement.GetProperty("Kind").GetString());
        Assert.Equal("Failed", end.RootElement.GetProperty("Outcome").GetString());
        Assert.Equal("Prepare", end.RootElement.GetProperty("Stage").GetString());
        Assert.Equal(start.RootElement.GetProperty("OperationId").GetGuid(),
            end.RootElement.GetProperty("OperationId").GetGuid());
    }

    [Fact]
    public async Task RecognitionKeepsPublishingWhileDiskIsBlockedAndEndAwaitsLatestEdits()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new MeetingStore(_directory);
        var blocked = 0;
        var engine = new Engine();
        var capture = new CaptureFactory();
        await using var session = new MeetingSession(() => engine, capture, store, () => new(),
            writeMeeting: async document =>
            {
                if (document.Turns.Count > 0 && Interlocked.Exchange(ref blocked, 1) == 0)
                { entered.SetResult(); await release.Task; }
                await store.SaveAsync(document);
            });
        await session.StartAsync(Request);
        capture.Captures[0].Send(3200);
        await WaitUntilAsync(() => engine.ReadBytes == 3200);
        Task? rename = null;
        Task? end = null;
        try
        {
            await Task.Run(() => engine.Report("Speaker 0: First")).WaitAsync(TimeSpan.FromSeconds(5));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() => engine.Report("Speaker 0: Latest speech")).WaitAsync(TimeSpan.FromSeconds(5));
            rename = session.RenameAsync("Latest title");
            Assert.False(rename.IsCompleted);
            end = session.EndAsync();
            Assert.False(end.IsCompleted);
            Assert.Equal("Latest speech", Assert.Single(session.Document!.Turns).Text);
        }
        finally { release.TrySetResult(); }
        await rename!.WaitAsync(TimeSpan.FromSeconds(5));
        await end!.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = await store.LoadAsync(session.Document!.Id);
        Assert.Equal("Latest title", saved.Title);
        Assert.Equal("Latest speech", Assert.Single(saved.Turns).Text);
        Assert.Equal(MeetingStatus.Completed, saved.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageFailureStopsCaptureSurfacesErrorAndRetainsRecoverableSpeech(bool secondaryRecognitionFailure)
    {
        var store = new MeetingStore(_directory);
        var engine = new Engine
        {
            CancellationFailure = secondaryRecognitionFailure ? new InvalidDataException("Secondary recognition failure") : null
        };
        var capture = new CaptureFactory();
        var failed = 0;
        await using var log = new LocalDiagnosticLog(Path.Combine(_directory, "diagnostics"));
        log.SetEnabled(true);
        await using var session = new MeetingSession(() => engine, capture, store, () => new(),
            writeMeeting: document =>
            {
                if (document.Turns.Count > 0 && Interlocked.Exchange(ref failed, 1) == 0)
                    throw new IOException("Disk unavailable");
                return store.SaveAsync(document);
            }, diagnostics: log);
        await session.StartAsync(Request);
        capture.Captures[0].Send(3200);
        await WaitUntilAsync(() => engine.ReadBytes == 3200);
        engine.Report("Speaker 0: Preserve this speech");
        await WaitUntilAsync(() => !session.IsActive);
        await session.EndAsync();
        Assert.Equal(MeetingSessionPhase.Failed, session.State.Phase);
        Assert.Contains("Disk unavailable", session.State.Error);
        Assert.True(capture.Captures[0].IsReleased);
        var saved = await store.LoadAsync(session.Document!.Id);
        Assert.Equal("Preserve this speech", Assert.Single(saved.Turns).Text);
        Assert.Equal(MeetingStatus.Failed, saved.Status);
        await log.DisposeAsync();
        var lines = await File.ReadAllLinesAsync(Path.Combine(log.DirectoryPath, "local-diagnostics.jsonl"));
        Assert.DoesNotContain("Disk unavailable", string.Join("", lines));
        var entries = lines.Select(line => System.Text.Json.JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Contains(entries, entry => entry.RootElement.GetProperty("Family").GetString() == "Storage" &&
                entry.RootElement.GetProperty("Category").GetString() == "Meeting" &&
                entry.RootElement.GetProperty("Outcome").GetString() == "Failed");
            var terminal = Assert.Single(entries, entry => entry.RootElement.GetProperty("Family").GetString() == "Meeting" &&
                entry.RootElement.GetProperty("Kind").GetString() == "Terminal").RootElement;
            Assert.Equal("Storage", terminal.GetProperty("Stage").GetString());
            Assert.Equal("Failed", terminal.GetProperty("Outcome").GetString());
            Assert.Equal(new IOException().HResult, terminal.GetProperty("ErrorHResult").GetInt32());
        }
        finally { foreach (var entry in entries) entry.Dispose(); }
    }

    [Fact]
    public async Task EndJoinsFailureTerminalAfterReservationIsReleased()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new Engine();
        var capture = new CaptureFactory();
        var store = new MeetingStore(_directory);
        var failed = 0;
        var blocked = 0;
        await using var log = new LocalDiagnosticLog(Path.Combine(_directory, "diagnostics"));
        log.SetEnabled(true);
        await using var session = new MeetingSession(() => engine, capture, store, () => new(),
            writeMeeting: document =>
            {
                if (document.Turns.Count > 0 && Interlocked.Exchange(ref failed, 1) == 0)
                    throw new IOException("Disk unavailable");
                return store.SaveAsync(document);
            }, diagnostics: log);
        session.StateChanged += snapshot =>
        {
            if (snapshot.Phase == MeetingSessionPhase.Failed && !session.IsActive &&
                Interlocked.Exchange(ref blocked, 1) == 0)
            {
                released.TrySetResult();
                allowPublication.Task.GetAwaiter().GetResult();
            }
        };
        await session.StartAsync(Request);
        capture.Captures[0].Send(3200);
        await WaitUntilAsync(() => engine.ReadBytes == 3200);
        engine.Report("Speaker 0: Preserved");
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var end = session.EndAsync();
        try
        {
            Assert.NotSame(end, await Task.WhenAny(end, Task.Delay(500)));
        }
        finally { allowPublication.TrySetResult(); }
        await end.WaitAsync(TimeSpan.FromSeconds(5));
        await log.DisposeAsync();
        var lines = await File.ReadAllLinesAsync(Path.Combine(log.DirectoryPath, "local-diagnostics.jsonl"));
        var entries = lines.Select(line => System.Text.Json.JsonDocument.Parse(line)).ToArray();
        try
        {
            var terminal = Assert.Single(entries, entry => entry.RootElement.GetProperty("Family").GetString() == "Meeting" &&
                entry.RootElement.GetProperty("Kind").GetString() == "Terminal").RootElement;
            Assert.Equal("Storage", terminal.GetProperty("Stage").GetString());
            Assert.Equal("Failed", terminal.GetProperty("Outcome").GetString());
        }
        finally { foreach (var entry in entries) entry.Dispose(); }
    }

    [Fact]
    public async Task RestartWaitsForPriorFailureTerminalBeforeReplacingItsIdentity()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEngine = new Engine();
        var enginesCreated = 0;
        var capture = new CaptureFactory();
        var store = new MeetingStore(_directory);
        var failed = 0;
        var blocked = 0;
        await using var log = new LocalDiagnosticLog(Path.Combine(_directory, "diagnostics"));
        log.SetEnabled(true);
        await using var session = new MeetingSession(
            () => Interlocked.Increment(ref enginesCreated) == 1 ? firstEngine : new Engine(),
            capture, store, () => new(), writeMeeting: document =>
            {
                if (document.Turns.Count > 0 && Interlocked.Exchange(ref failed, 1) == 0)
                    throw new IOException("Disk unavailable");
                return store.SaveAsync(document);
            }, diagnostics: log);
        session.StateChanged += snapshot =>
        {
            if (snapshot.Phase == MeetingSessionPhase.Failed && !session.IsActive &&
                Interlocked.Exchange(ref blocked, 1) == 0)
            {
                released.TrySetResult();
                allowPublication.Task.GetAwaiter().GetResult();
            }
        };
        await session.StartAsync(Request);
        capture.Captures[0].Send(3200);
        await WaitUntilAsync(() => firstEngine.ReadBytes == 3200);
        firstEngine.Report("Speaker 0: Preserved");
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var restart = session.StartAsync(Request);
        try { Assert.NotSame(restart, await Task.WhenAny(restart, Task.Delay(500))); }
        finally { allowPublication.TrySetResult(); }
        await restart.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, enginesCreated);
        await session.EndAsync();
        await log.DisposeAsync();
        var lines = await File.ReadAllLinesAsync(Path.Combine(log.DirectoryPath, "local-diagnostics.jsonl"));
        var entries = lines.Select(line => System.Text.Json.JsonDocument.Parse(line)).ToArray();
        try
        {
            var terminals = entries.Select(entry => entry.RootElement)
                .Where(entry => entry.GetProperty("Family").GetString() == "Meeting" &&
                    entry.GetProperty("Kind").GetString() == "Terminal").ToArray();
            Assert.Equal(2, terminals.Length);
            Assert.Equal("Storage", terminals[0].GetProperty("Stage").GetString());
            Assert.Equal("Failed", terminals[0].GetProperty("Outcome").GetString());
            Assert.Equal("Completed", terminals[1].GetProperty("Outcome").GetString());
            Assert.NotEqual(terminals[0].GetProperty("OperationId").GetGuid(),
                terminals[1].GetProperty("OperationId").GetGuid());
        }
        finally { foreach (var entry in entries) entry.Dispose(); }
    }

    [Fact]
    public async Task RejectedStartDoesNotTerminateAnExistingMeeting()
    {
        await using var log = new LocalDiagnosticLog(Path.Combine(_directory, "diagnostics"));
        log.SetEnabled(true);
        await using var session = new MeetingSession(() => new Engine(), new CaptureFactory(), new MeetingStore(_directory),
            () => new(), diagnostics: log);
        await session.StartAsync(Request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(Request));
        Assert.True(session.IsActive);
        Assert.Equal(MeetingSessionPhase.Recording, session.State.Phase);
        await session.EndAsync();
        await log.DisposeAsync();
        var lines = await File.ReadAllLinesAsync(Path.Combine(log.DirectoryPath, "local-diagnostics.jsonl"));
        var entries = lines.Select(line => System.Text.Json.JsonDocument.Parse(line)).ToArray();
        try
        {
            var terminal = Assert.Single(entries, entry => entry.RootElement.GetProperty("Family").GetString() == "Meeting" &&
                entry.RootElement.GetProperty("Kind").GetString() == "Terminal").RootElement;
            Assert.Equal("Completed", terminal.GetProperty("Outcome").GetString());
        }
        finally { foreach (var entry in entries) entry.Dispose(); }
    }

    [Fact]
    public async Task RecognitionFailureRemainsPrimaryWhenFailureSaveAlsoFails()
    {
        var failRecognition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new Engine
        {
            StreamingWork = async token =>
            {
                await failRecognition.Task.WaitAsync(token);
                throw new InvalidDataException("Primary recognition failure");
            }
        };
        var store = new MeetingStore(_directory);
        var failedSave = 0;
        await using var log = new LocalDiagnosticLog(Path.Combine(_directory, "diagnostics"));
        log.SetEnabled(true);
        await using var session = new MeetingSession(() => engine, new CaptureFactory(), store, () => new(),
            writeMeeting: document =>
            {
                if (document.Status == MeetingStatus.Failed && Interlocked.Exchange(ref failedSave, 1) == 0)
                    throw new IOException("Secondary save failure");
                return store.SaveAsync(document);
            }, diagnostics: log);
        await session.StartAsync(Request);
        failRecognition.TrySetResult();
        await WaitUntilAsync(() => !session.IsActive);
        await session.EndAsync();
        Assert.Contains("Primary recognition failure", session.State.Error);
        await log.DisposeAsync();
        var lines = await File.ReadAllLinesAsync(Path.Combine(log.DirectoryPath, "local-diagnostics.jsonl"));
        var entries = lines.Select(line => System.Text.Json.JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Contains(entries, entry => entry.RootElement.GetProperty("Family").GetString() == "Storage" &&
                entry.RootElement.GetProperty("Outcome").GetString() == "Failed");
            var terminal = Assert.Single(entries, entry => entry.RootElement.GetProperty("Family").GetString() == "Meeting" &&
                entry.RootElement.GetProperty("Kind").GetString() == "Terminal").RootElement;
            Assert.Equal("Recognize", terminal.GetProperty("Stage").GetString());
            Assert.Equal("Failed", terminal.GetProperty("Outcome").GetString());
            Assert.Equal(new InvalidDataException().HResult, terminal.GetProperty("ErrorHResult").GetInt32());
        }
        finally { foreach (var entry in entries) entry.Dispose(); }
    }

    [Fact]
    public async Task CleanupPublishesOnlyAfterDurableSaveAndFailureKeepsOriginalSpeech()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new MeetingStore(_directory);
        var cleanupAttempts = 0;
        var engine = new Engine();
        var captures = new CaptureFactory();
        await using var session = new MeetingSession(() => engine, captures, store, () => new(),
            writeMeeting: async document =>
            {
                if (document.Turns.Any(turn => turn.OriginalText is not null) && Interlocked.Increment(ref cleanupAttempts) == 1)
                {
                    entered.TrySetResult();
                    await release.Task;
                    throw new IOException("Cleanup save failed");
                }
                await store.SaveAsync(document);
            });
        await session.StartAsync(Request);
        captures.Captures[0].Send(3200);
        await WaitUntilAsync(() => engine.ReadBytes == 3200);
        engine.Report("Speaker 0: Original speech");
        await session.EndAsync();
        var cleanup = session.ApplyCleanupAsync(["Cleaned speech."]);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(cleanup.IsCompleted);
            Assert.Equal("Original speech", Assert.Single(session.Document!.Turns).Text);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<IOException>(() => cleanup.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Original speech", Assert.Single(session.Document!.Turns).Text);
        Assert.Null(session.Document.Turns[0].OriginalText);
        Assert.Equal(MeetingStatus.Completed, session.Document.Status);
        Assert.Equal("Original speech", Assert.Single((await store.LoadAsync(session.Document.Id)).Turns).Text);
        await session.ApplyCleanupAsync(["Cleaned speech."]);
        Assert.Equal(MeetingStatus.Completed, session.Document.Status);
        Assert.Equal("Cleaned speech.", Assert.Single(session.Document.Turns).Text);
        Assert.Equal("Original speech", session.Document.Turns[0].OriginalText);
        Assert.Equal("Cleaned speech.", Assert.Single((await store.LoadAsync(session.Document.Id)).Turns).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscardAndDisposeCancelPreparationAndJoinBeforeReleasingReservation(bool dispose)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var join = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new Engine { Preparing = async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                await join.Task;
                throw;
            }
        } };
        var captures = new CaptureFactory();
        var releases = 0;
        var session = new MeetingSession(() => engine, captures, new(_directory), () => new(),
            release: () => { releases++; return Task.CompletedTask; });
        var start = session.StartAsync(Request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var finish = dispose ? session.DisposeAsync().AsTask() : session.DiscardAsync();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(finish.IsCompleted);
        Assert.True(session.IsActive);
        Assert.Equal(0, releases);
        Assert.Empty(captures.Captures);
        join.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        await finish.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(captures.Captures);
        Assert.True(engine.Disposed);
        Assert.Equal(1, releases);
        Assert.False(session.IsActive);
        if (!dispose) await session.DisposeAsync();
    }

    [Fact]
    public async Task PauseFreezesUpdatesAndCaptureResumeUsesSameStreamAndEndSavesFinal()
    {
        var engine = new Engine();
        var capture = new CaptureFactory();
        var reserved = 0;
        var released = 0;
        var store = new MeetingStore(_directory);
        await using var session = new MeetingSession(() => engine, capture, store, () => new(),
            () => { reserved++; return Task.CompletedTask; }, () => { released++; return Task.CompletedTask; });
        await session.StartAsync(Request);
        capture.Captures[0].Send(3200);
        await WaitUntilAsync(() => engine.ReadBytes == 3200);
        engine.Report("Speaker 0: First");
        await session.PauseAsync();
        Assert.True(capture.Captures[0].IsReleased);
        Assert.True(session.IsActive);
        engine.Report("Speaker 0: First Speaker 1: Pending");
        Assert.Equal("First", Assert.Single(session.Document!.Turns).Text);
        await session.ResumeAsync();
        Assert.Equal(2, session.Document!.Turns.Count);
        Assert.Equal(1, engine.StreamCalls);
        Assert.Equal(2, capture.Captures.Count);
        capture.Captures[1].Send(6400);
        await session.EndAsync();
        Assert.Equal(9600, engine.ReadBytes);
        Assert.Equal(MeetingSessionPhase.Completed, session.State.Phase);
        Assert.False(session.IsActive);
        Assert.True(engine.Disposed);
        Assert.Equal(1, reserved);
        Assert.Equal(1, released);
        Assert.All(session.Document!.Turns, turn => Assert.False(turn.IsProvisional));
        var saved = await store.LoadAsync(session.Document.Id);
        Assert.Equal(MeetingStatus.Completed, saved.Status);
        Assert.Equal("Pending", saved.Turns[1].Text);
    }

    [Fact]
    public async Task PausedBacklogRemainsUnreadUntilResume()
    {
        var engine = new Engine { BlockReads = true };
        var captures = new CaptureFactory();
        await using var session = Create(engine, captures);
        await session.StartAsync(Request);
        captures.Captures[0].Send(6400);
        await WaitUntilAsync(() => session.State.CapturedSeconds == 0.2);
        await session.PauseAsync();
        engine.AllowReads.TrySetResult();
        await Task.Delay(30);
        Assert.Equal(0, engine.ReadBytes);
        await session.ResumeAsync();
        await WaitUntilAsync(() => engine.ReadBytes == 6400);
        await session.EndAsync();
    }

    [Fact]
    public async Task NewCumulativeUpdateOnResumeSupersedesOlderPausedUpdate()
    {
        var engine = new Engine();
        await using var session = Create(engine, new());
        await session.StartAsync(Request);
        engine.Report("Speaker 0: First");
        await session.PauseAsync();
        engine.Report("Speaker 0: First older pending");
        var injected = false;
        session.StateChanged += state =>
        {
            if (state.Phase != MeetingSessionPhase.Recording || injected) return;
            injected = true;
            engine.Report("Speaker 0: First older pending newest words");
        };
        await session.ResumeAsync();
        Assert.Equal("First older pending newest words", Assert.Single(session.Document!.Turns).Text);
        await session.EndAsync();
    }

    [Fact]
    public async Task DiscardCancelsRecognitionAndKeepsAlreadySavedTranscript()
    {
        var engine = new Engine();
        await using var session = Create(engine, new());
        await session.StartAsync(Request);
        engine.Report("Speaker 0: Preserve this");
        await session.DiscardAsync();
        Assert.True(engine.Canceled);
        Assert.False(session.IsActive);
        Assert.Equal(MeetingStatus.Interrupted, session.Document!.Status);
        Assert.Equal("Preserve this", Assert.Single((await new MeetingStore(_directory).LoadAsync(session.Document.Id)).Turns).Text);
    }

    [Fact]
    public async Task DiscardWhilePausedDoesNotPublishPendingSpeech()
    {
        var engine = new Engine();
        await using var session = Create(engine, new());
        await session.StartAsync(Request);
        engine.Report("Speaker 0: Saved speech");
        await session.PauseAsync();
        engine.Report("Speaker 0: Saved speech Speaker 1: Pending speech");
        await session.DiscardAsync();
        Assert.Equal("Saved speech", Assert.Single(session.Document!.Turns).Text);
    }

    [Fact]
    public async Task StartupFailureReleasesEngineAndDictationExclusion()
    {
        var engine = new Engine { ConnectFailure = new InvalidOperationException("Model missing") };
        var releases = 0;
        await using var session = new MeetingSession(() => engine, new CaptureFactory(), new(_directory), () => new(),
            release: () => { releases++; return Task.CompletedTask; });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(Request));
        Assert.Equal("Model missing", error.Message);
        Assert.False(session.IsActive);
        Assert.True(engine.Disposed);
        Assert.Equal(1, releases);
        Assert.Equal(MeetingSessionPhase.Failed, session.State.Phase);
    }

    [Fact]
    public async Task EndWhilePausedFinishesPendingUpdateWithoutRestartingCapture()
    {
        var engine = new Engine();
        var captures = new CaptureFactory();
        await using var session = Create(engine, captures);
        await session.StartAsync(Request);
        engine.Report("Speaker 0: First");
        await session.PauseAsync();
        engine.Report("Speaker 0: First Speaker 1: Pending");
        await session.EndAsync();
        Assert.Single(captures.Captures);
        Assert.Equal("Pending", session.Document!.Turns[1].Text);
        Assert.All(session.Document.Turns, turn => Assert.False(turn.IsProvisional));
    }

    [Fact]
    public async Task FailedCaptureStartupRetainsOwnershipUntilRetryReleasesResources()
    {
        var retained = new Capture { BlockRelease = true };
        var captures = new CaptureFactory { StartupFailure = new MeetingCaptureOwnershipException("Capture failed", retained, new IOException("Device lost")) };
        var releases = 0;
        var engine = new Engine();
        await using var session = new MeetingSession(() => engine, captures, new(_directory), () => new(),
            release: () => { releases++; return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<Exception>(() => session.StartAsync(Request));
        Assert.True(session.IsActive);
        Assert.Equal(0, releases);
        Assert.False(engine.Disposed);
        retained.BlockRelease = false;
        await session.EndAsync();
        Assert.True(retained.IsReleased);
        Assert.False(session.IsActive);
        Assert.Equal(1, releases);
        Assert.Equal(MeetingStatus.Failed, session.Document!.Status);
    }

    [Fact]
    public async Task BoundedBacklogFailsAndPreservesText()
    {
        var engine = new Engine { BlockReads = true };
        var captures = new CaptureFactory();
        await using var session = Create(engine, captures);
        await session.StartAsync(Request);
        engine.Report("Speaker 0: Before overload");
        captures.Captures[0].Send(3200 * 301);
        await WaitUntilAsync(() => session.State.Phase == MeetingSessionPhase.Failed);
        Assert.Contains("30 seconds", session.State.Error);
        engine.AllowReads.TrySetResult();
        await WaitUntilAsync(() => !session.IsActive);
        Assert.True(captures.Captures[0].IsReleased);
        Assert.Equal("Before overload", Assert.Single(session.Document!.Turns).Text);
    }

    [Fact]
    public async Task TranscriptUpdatesDoNotOverwriteTitleOrSpeakerEdits()
    {
        var engine = new Engine();
        await using var session = Create(engine, new());
        await session.StartAsync(Request);
        engine.Report("Speaker 0: First");
        await session.RenameAsync("Team planning");
        await session.RenameSpeakerAsync("Speaker 0", "Alex");
        engine.Report("Speaker 0: First continued Speaker 1: Reply");
        await session.EndAsync();
        var saved = await new MeetingStore(_directory).LoadAsync(session.Document!.Id);
        Assert.Equal("Team planning", saved.Title);
        Assert.Equal("Alex", saved.GetSpeakerName("Speaker 0"));
        Assert.Equal("First continued", saved.Turns[0].Text);
    }

    private MeetingSession Create(Engine engine, CaptureFactory captures) => new(() => engine, captures, new(_directory), () => new());

    [Fact]
    public async Task EndFromUiContextFinalizesAndReleasesNativeResourcesOffUi()
    {
        using var context = new MeetingUiContext();
        var engine = new Engine { RequireBackgroundFinalization = true };
        var captures = new CaptureFactory();
        var aligner = new Aligner { YieldInitialization = true };
        var store = new MeetingStore(_directory);
        await context.RunAsync(async () =>
        {
            await using var session = new MeetingSession(() => engine, captures, store, () => new(), alignerFactory: () => aligner);
            await session.StartAsync(Request);
            captures.Captures[0].Send(96000);
            await WaitUntilAsync(() => engine.ReadBytes == 96000);
            await Task.Run(() => engine.Report("Speaker 0: Final meeting words"));
            await session.EndAsync();
            Assert.Null(session.State.Error);
            Assert.Equal(MeetingSessionPhase.Completed, session.State.Phase);
            Assert.False(session.IsActive);
            Assert.True(captures.Captures[0].IsReleased);
            Assert.True(engine.Disposed);
            Assert.Null(engine.DisposalContext);
            Assert.True(aligner.Disposed);
            Assert.Null(aligner.DisposalContext);
            var saved = await store.LoadAsync(session.Document!.Id);
            Assert.Equal(MeetingStatus.Completed, saved.Status);
            var turn = Assert.Single(saved.Turns);
            Assert.Equal("Final meeting words", turn.Text);
            Assert.False(turn.IsProvisional);
            Assert.Equal(MeetingTimingQuality.Aligned, turn.TimingQuality);
        }).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class MeetingUiContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public MeetingUiContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var action in _queue.GetConsumingEnumerable()) action();
            }) { IsBackground = true };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add(() => callback(state));

        public Task RunAsync(Func<Task> action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception error) { completion.SetException(error); }
            }, null);
            return completion.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            if (_thread.Join(TimeSpan.FromSeconds(5))) _queue.Dispose();
        }
    }

    [Fact]
    public async Task TimingRefinementRetainsAnchorsAndCleanupPreservesOriginalAndNames()
    {
        var engine = new Engine();
        var captures = new CaptureFactory();
        var aligner = new Aligner();
        await using var session = new MeetingSession(() => engine, captures, new(_directory), () => new(), alignerFactory: () => aligner);
        await session.StartAsync(Request);
        captures.Captures[0].Send(16000 * 2 * 3);
        await WaitUntilAsync(() => engine.ReadBytes == 96000);
        engine.Report("Speaker 0: Hello there");
        var turn = Assert.Single(session.Document!.Turns);
        Assert.Equal(MeetingTimingQuality.Chunk, turn.TimingQuality);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyCleanupAsync(["Hello."]));
        await session.RenameSpeakerAsync("Speaker 0", "Alex");
        await session.EndAsync();
        Assert.True(aligner.Disposed);
        Assert.Equal(0.5, session.Document!.Turns[0].StartSeconds);
        await session.ApplyCleanupAsync(["Hello, there."]);
        var saved = await new MeetingStore(_directory).LoadAsync(session.Document.Id);
        Assert.Equal("Hello there", saved.Turns[0].OriginalText);
        Assert.Equal("Hello, there.", saved.Turns[0].Text);
        Assert.Equal("Alex", saved.GetSpeakerName("Speaker 0"));
        Assert.Equal(0.5, saved.Turns[0].StartSeconds);
    }

    [Fact]
    public async Task OptionalAlignmentFailureDoesNotStopLocalTranscription()
    {
        var engine = new Engine();
        var captures = new CaptureFactory();
        var aligner = new Aligner { FailInitialization = true };
        await using var session = new MeetingSession(() => engine, captures, new(_directory), () => new(), alignerFactory: () => aligner);
        await session.StartAsync(Request);
        captures.Captures[0].Send(32000);
        await WaitUntilAsync(() => engine.ReadBytes == 32000);
        engine.Report("Speaker 0: Local speech");
        await session.EndAsync();
        Assert.Null(session.State.Error);
        Assert.Equal(MeetingSessionPhase.Completed, session.State.Phase);
        Assert.Equal("Local speech", session.Document!.Turns[0].Text);
        Assert.Equal(MeetingTimingQuality.Chunk, session.Document.Turns[0].TimingQuality);
        Assert.True(aligner.Disposed);
    }

    private sealed class Aligner : IMeetingWordAligner
    {
        public bool FailInitialization { get; init; }
        public bool YieldInitialization { get; init; }
        public bool Disposed { get; private set; }
        public SynchronizationContext? DisposalContext { get; private set; }
        public async Task InitializeAsync(CancellationToken token)
        {
            if (FailInitialization) throw new InvalidOperationException("Alignment weights missing");
            if (YieldInitialization) await Task.Yield();
        }
        public Task<IReadOnlyList<MeetingTurn>> AlignAsync(WaveAudio audio, IReadOnlyList<MeetingTurn> turns, CancellationToken token)
            => Task.FromResult<IReadOnlyList<MeetingTurn>>(turns.Select(turn => turn with
                { StartSeconds = 0.5, EndSeconds = 1.5, TimingQuality = MeetingTimingQuality.Aligned }).ToArray());
        public ValueTask DisposeAsync() { DisposalContext = SynchronizationContext.Current; Disposed = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task TeamsEvidenceNamesTwoExclusiveAlignedTurnsAndManualNameWins()
    {
        var engine = new Engine();
        var captures = new CaptureFactory();
        var observer = new Observer();
        await using var session = new MeetingSession(() => engine, captures, new(_directory), () => new(),
            observerFactory: () => observer, alignerFactory: () => new TwoTurnAligner());
        await session.StartAsync(new(42));
        captures.Captures[0].PlaybackRms = 0.2;
        captures.Captures[0].Send(9 * 32000);
        await WaitUntilAsync(() => engine.ReadBytes == 9 * 32000);
        for (var second = 0; second <= 8; second++) observer.Report(new(second * 16000, ["Alex"], null, MeetingSpeakerReadStatus.Readable));
        engine.Report("Speaker 0: First words Speaker 0: Second words");
        Assert.Empty(session.Document!.AutoSpeakerNames);
        await session.EndAsync();
        Assert.Equal("Alex", session.Document!.GetSpeakerName("Speaker 0"));
        Assert.All(session.Document.Turns, turn => Assert.Equal(MeetingAudioSource.Playback, turn.Source));
        Assert.True(observer.Disposed);
        await session.RenameSpeakerAsync("Speaker 0", "Corrected name");
        Assert.Equal("Corrected name", session.Document.GetSpeakerName("Speaker 0"));
    }

    private sealed class TwoTurnAligner : IMeetingWordAligner
    {
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<MeetingTurn>> AlignAsync(WaveAudio audio, IReadOnlyList<MeetingTurn> turns, CancellationToken token)
            => Task.FromResult<IReadOnlyList<MeetingTurn>>(turns.Select(turn => turn with
                { StartSeconds = turn.Text.StartsWith("First", StringComparison.Ordinal) ? 1 : 5,
                    EndSeconds = turn.Text.StartsWith("First", StringComparison.Ordinal) ? 3 : 7,
                    TimingQuality = MeetingTimingQuality.Aligned }).ToArray());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Observer : IMeetingSpeakerObserver
    {
        private Action<MeetingSpeakerObservation>? _report;
        public bool Disposed { get; private set; }
        public Task StartAsync(int processId, Func<long> position, Action<MeetingSpeakerObservation> report, CancellationToken token)
        { _report = report; return Task.CompletedTask; }
        public void Report(MeetingSpeakerObservation observation) => _report!(observation);
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task MeetingReservationBlocksDictationAndSecondReservationUntilReleased()
    {
        var dictation = new DictationSession(_ => throw new InvalidOperationException("Engine must remain unused"),
            new UnusedCaptureFactory(), new UnusedAudioInput(), new UsageStore(Path.Combine(_directory, "usage.json")),
            _directory, new AppSettingsStore(Path.Combine(_directory, "settings.json")));
        await dictation.ReserveMeetingAsync();
        Assert.True(dictation.State.MeetingActive);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dictation.ReserveMeetingAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => dictation.StartDictationAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => dictation.CloseAsync());
        await dictation.ReleaseMeetingAsync();
        Assert.False(dictation.State.MeetingActive);
        await dictation.ReserveMeetingAsync();
        await dictation.ReleaseMeetingAsync();
        await dictation.CloseAsync();
    }

    [Fact]
    public async Task DisposalBlocksNewMeetingAdmissionWhileOwnedCaptureIsStillReleasing()
    {
        var engine = new Engine();
        var captures = new CaptureFactory();
        await using var session = Create(engine, captures);
        await session.StartAsync(Request);
        var capture = captures.Captures[0];
        capture.StopBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposal = session.DisposeAsync().AsTask();
        await capture.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var restart = session.StartAsync(Request);
        capture.StopBarrier.TrySetResult();
        await disposal;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => restart);
        Assert.Single(captures.Captures);
        Assert.False(session.IsActive);
    }

    [Fact]
    public async Task ConcurrentDisposalWaitsForTheFirstTeardownInsteadOfReachingTheClosedWriter()
    {
        for (var round = 0; round < 30; round++)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var session = new MeetingSession(() => new Engine(), new CaptureFactory(), new(_directory), () => new(),
                writeMeeting: async document =>
                {
                    if (document.Status != MeetingStatus.Completed) return;
                    entered.TrySetResult();
                    await release.Task;
                });
            await session.StartAsync(Request);

            var first = session.DisposeAsync().AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = session.DisposeAsync().AsTask();
            release.SetResult();

            var failure = await Record.ExceptionAsync(() => Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(failure is null, $"Round {round}: {failure}");
            Assert.False(session.IsActive);
        }
    }

    [Fact]
    public async Task DisposalThatFailedToSaveIsRetriedByTheNextCallInsteadOfRepeatingTheFailure()
    {
        var completedWrites = 0;
        await using var session = new MeetingSession(() => new Engine(), new CaptureFactory(), new(_directory), () => new(),
            writeMeeting: document => document.Status == MeetingStatus.Completed && Interlocked.Increment(ref completedWrites) == 1
                ? Task.FromException(new IOException("Disk unavailable"))
                : Task.CompletedTask);
        await session.StartAsync(Request);

        await Assert.ThrowsAsync<IOException>(() => session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(session.IsActive);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MeetingReadsCurrentOptionsAtStartAndKeepsThemUntilTheNextMeeting(bool supportsDictionary)
    {
        var engine = new Engine { SupportsDictionary = supportsDictionary };
        var options = new RecognitionOptions("fr", "word from when the window opened", NativeBackend.Cpu);
        await using var session = new MeetingSession(() => engine, new CaptureFactory(), new(_directory), () => options);

        options = options with { Language = "de", CustomDictionary = "latest word", Backend = NativeBackend.Vulkan };
        await session.StartAsync(Request);
        Assert.Equal(supportsDictionary ? "latest word" : "", engine.PreparedOptions?.CustomDictionary);
        Assert.Equal("de", engine.PreparedOptions?.Language);
        Assert.Equal(NativeBackend.Vulkan, engine.PreparedOptions?.Backend);
        Assert.Equal(engine.PreparedOptions, engine.StreamedOptions);

        options = options with { Language = "es", CustomDictionary = "word for next meeting", Backend = NativeBackend.Auto };
        await session.PauseAsync();
        await session.ResumeAsync();
        Assert.Equal(supportsDictionary ? "latest word" : "", engine.StreamedOptions?.CustomDictionary);
        Assert.Equal("de", engine.StreamedOptions?.Language);
        Assert.Equal(NativeBackend.Vulkan, engine.StreamedOptions?.Backend);
        await session.EndAsync();

        await session.StartAsync(Request);
        Assert.Equal(supportsDictionary ? "word for next meeting" : "", engine.PreparedOptions?.CustomDictionary);
        Assert.Equal("es", engine.PreparedOptions?.Language);
        Assert.Equal(NativeBackend.Auto, engine.PreparedOptions?.Backend);
        Assert.Equal(engine.PreparedOptions, engine.StreamedOptions);
        await session.EndAsync();
    }

    [Fact]
    public async Task UnavailableRecognitionOptionsDoNotLeaveAHeldReservation()
    {
        var releases = 0;
        var captures = new CaptureFactory();
        await using var session = new MeetingSession(() => new Engine(), captures, new(_directory),
            () => throw new InvalidOperationException("Options unavailable."),
            release: () => { releases++; return Task.CompletedTask; });

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(Request));

        Assert.False(session.IsActive);
        Assert.Empty(captures.Captures);
        Assert.Equal(0, releases);
        Assert.Equal(MeetingSessionPhase.Idle, session.State.Phase);
    }

    [Fact]
    public async Task ExplicitMeetingSelectionIsForwardedWithoutAStreamingFallback()
    {
        var engine = new Engine { Models = [NemotronAsr(), DiarCompanion(), VibeVoiceStreaming()] };
        await using var session = new MeetingSession(() => engine, new CaptureFactory(), new(_directory), () => new(),
            getModelId: () => AudioModelDefaults.RecognitionModelId);

        await session.StartAsync(Request);

        Assert.Equal(AudioModelDefaults.RecognitionModelId, engine.PreparedModel?.Id);
        await session.EndAsync();
    }

    [Fact]
    public async Task StartCapturesModelAndOptionsBeforeAnAsynchronousReservation()
    {
        var enteredReserve = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReserve = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new RecognitionOptions("fr", "", NativeBackend.Cpu);
        string? selected = AudioModelDefaults.RecognitionModelId;
        var engine = new Engine { Models = [NemotronAsr(), DiarCompanion(), VibeVoiceStreaming()] };
        await using var session = new MeetingSession(() => engine, new CaptureFactory(), new(_directory),
            () => options,
            reserve: async () => { enteredReserve.TrySetResult(); await releaseReserve.Task; },
            getModelId: () => selected);
        try
        {
            var starting = session.StartAsync(Request);
            await enteredReserve.Task.WaitAsync(TimeSpan.FromSeconds(2));
            options = new RecognitionOptions("de", "", NativeBackend.Vulkan);
            selected = VibeVoiceStreaming().Id;
            releaseReserve.SetResult();
            await starting.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(AudioModelDefaults.RecognitionModelId, engine.PreparedModel?.Id);
            Assert.Equal("fr", engine.PreparedOptions?.Language);
            Assert.Equal(NativeBackend.Cpu, engine.PreparedOptions?.Backend);
            await session.EndAsync();
        }
        finally { releaseReserve.TrySetResult(); }
    }

    [Fact]
    public async Task UnconfiguredStartWithoutCompanionFallsBackToVibeVoiceInsteadOfDefaultNemotron()
    {
        var engine = new Engine { Models = [NemotronAsr(), VibeVoiceStreaming()] };
        await using var session = new MeetingSession(() => engine, new CaptureFactory(), new(_directory), () => new(),
            getModelId: () => null);

        await session.StartAsync(Request);

        Assert.Equal(VibeVoiceStreaming().Id, engine.PreparedModel?.Id);
        Assert.Equal("vibevoice_asr_streaming", engine.PreparedModel?.Family);
        await session.EndAsync();
    }

    [Fact]
    public async Task UnconfiguredStartWithCompanionPrefersNemotronThenKrokoBeforeVibeVoice()
    {
        var nemotron = new Engine { Models = [NemotronAsr(), KrokoAsr(), VibeVoiceStreaming(), DiarCompanion()] };
        await using (var session = new MeetingSession(() => nemotron, new CaptureFactory(), new(_directory), () => new(),
            getModelId: () => null))
        {
            await session.StartAsync(Request);
            Assert.Equal(AudioModelDefaults.RecognitionModelId, nemotron.PreparedModel?.Id);
            await session.EndAsync();
        }

        var kroko = new Engine { Models = [KrokoAsr(), VibeVoiceStreaming(), DiarCompanion()] };
        await using (var session = new MeetingSession(() => kroko, new CaptureFactory(), new(_directory), () => new(),
            getModelId: () => null))
        {
            await session.StartAsync(Request);
            Assert.Equal(AudioModelDefaults.KrokoModelId, kroko.PreparedModel?.Id);
            await session.EndAsync();
        }

        var vibevoice = new Engine { Models = [VibeVoiceStreaming(), DiarCompanion()] };
        await using (var session = new MeetingSession(() => vibevoice, new CaptureFactory(), new(_directory), () => new(),
            getModelId: () => null))
        {
            await session.StartAsync(Request);
            Assert.Equal(VibeVoiceStreaming().Id, vibevoice.PreparedModel?.Id);
            await session.EndAsync();
        }
    }

    [Fact]
    public async Task SelectedMeetingModelIsCapturedAtStartAndIgnoresLaterGetModelIdChanges()
    {
        string? selected = AudioModelDefaults.RecognitionModelId;
        var engine = new Engine { Models = [NemotronAsr(), DiarCompanion(), VibeVoiceStreaming()] };
        await using var session = new MeetingSession(() => engine, new CaptureFactory(), new(_directory), () => new(),
            getModelId: () => selected);

        await session.StartAsync(Request);
        Assert.Equal(AudioModelDefaults.RecognitionModelId, engine.PreparedModel?.Id);

        selected = VibeVoiceStreaming().Id;
        await session.PauseAsync();
        await session.ResumeAsync();
        Assert.Equal(AudioModelDefaults.RecognitionModelId, engine.PreparedModel?.Id);
        await session.EndAsync();

        await session.StartAsync(Request);
        Assert.Equal(VibeVoiceStreaming().Id, engine.PreparedModel?.Id);
        await session.EndAsync();
    }

    [Fact]
    public async Task MissingOrUnstreamableSelectedMeetingModelReleasesTheReservation()
    {
        var releases = 0;
        var captures = new CaptureFactory();
        var engine = new Engine { Models = [VibeVoiceStreaming(), DiarCompanion()] };
        await using var session = new MeetingSession(() => engine, captures, new(_directory), () => new(),
            release: () => { releases++; return Task.CompletedTask; },
            getModelId: () => AudioModelDefaults.RecognitionModelId);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(Request));

        Assert.Contains(AudioModelDefaults.RecognitionModelId, error.Message);
        Assert.False(session.IsActive);
        Assert.Empty(captures.Captures);
        Assert.Equal(1, releases);
        Assert.Equal(MeetingSessionPhase.Failed, session.State.Phase);
    }

    private static AudioModel NemotronAsr() => new()
    {
        Id = AudioModelDefaults.RecognitionModelId, Family = "nemotron_asr", Mode = "streaming"
    };

    private static AudioModel KrokoAsr() => new()
    {
        Id = AudioModelDefaults.KrokoModelId, Family = "kroko_asr", Mode = "streaming"
    };

    private static AudioModel VibeVoiceStreaming() => new()
    {
        Id = "vibevoice-streaming-1.5b-q4", Family = "vibevoice_asr_streaming", Mode = "streaming"
    };

    private static AudioModel DiarCompanion() => new()
    {
        Id = AudioModelDefaults.DiarizationModelId, Family = "nemotron_3_diar", Task = "diar", Mode = "streaming"
    };

    private sealed class Engine : IMeetingRecognitionEngine
    {
        private IProgress<TranscriptUpdate>? _progress;
        private string _text = "";
        private long _readBytes;
        public long ReadBytes => Interlocked.Read(ref _readBytes);
        public int StreamCalls { get; private set; }
        public bool Disposed { get; private set; }
        public bool Canceled { get; private set; }
        public Exception? CancellationFailure { get; init; }
        public Func<CancellationToken, Task>? StreamingWork { get; init; }
        public bool RequireBackgroundFinalization { get; init; }
        public SynchronizationContext? DisposalContext { get; private set; }
        public bool BlockReads { get; init; }
        public Exception? ConnectFailure { get; init; }
        public bool SupportsDictionary { get; init; }
        public IReadOnlyList<AudioModel>? Models { get; init; }
        public AudioModel? PreparedModel { get; private set; }
        public RecognitionOptions? PreparedOptions { get; private set; }
        public RecognitionOptions? StreamedOptions { get; private set; }
        public Func<CancellationToken, Task>? Preparing { get; init; }
        public Task PrepareMeetingAsync(AudioModel model, RecognitionOptions options, CancellationToken token)
        {
            PreparedModel = model;
            PreparedOptions = options;
            return Preparing?.Invoke(token) ?? Task.CompletedTask;
        }
        public TaskCompletionSource AllowReads { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<AudioModel>> ConnectAsync(CancellationToken cancellationToken) => ConnectFailure is { } error
            ? Task.FromException<IReadOnlyList<AudioModel>>(error)
            : Task.FromResult<IReadOnlyList<AudioModel>>(Models ?? [new() { Id = "vibevoice", Family = "vibevoice_asr_streaming",
                Mode = "streaming", SupportsCustomDictionary = SupportsDictionary }]);
        public async Task StreamMeetingAsync(AudioModel model, ChannelReader<byte[]> audio, RecognitionOptions options,
            IProgress<TranscriptUpdate> progress, CancellationToken cancellationToken, Action? onReady = null)
        {
            StreamCalls++;
            StreamedOptions = options;
            _progress = progress;
            onReady?.Invoke();
            try
            {
                if (StreamingWork is { } work) { await work(cancellationToken); return; }
                if (BlockReads) await AllowReads.Task.WaitAsync(cancellationToken);
                await foreach (var packet in audio.ReadAllAsync(cancellationToken)) Interlocked.Add(ref _readBytes, packet.Length);
                // Fail before invoking a blocking callback so the regression cannot deadlock the test runner.
                if (RequireBackgroundFinalization && SynchronizationContext.Current is not null)
                    throw new InvalidOperationException("Final recognition callback ran on the UI context.");
                progress.Report(new(_text, true));
            }
            catch (OperationCanceledException)
            {
                Canceled = true;
                if (CancellationFailure is { } failure) throw failure;
                throw;
            }
        }
        public void Report(string text) { _text = text; _progress!.Report(new(text, false)); }
        public ValueTask DisposeAsync() { DisposalContext = SynchronizationContext.Current; Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class UnusedCaptureFactory : IAudioCaptureFactory
    {
        public Task<IAudioCapture> StartAsync(CancellationToken cancellationToken, int microphoneBoostDecibels = 0) =>
            throw new InvalidOperationException("Dictation capture must remain unused");
    }

    private sealed class UnusedAudioInput : IAudioInputReader
    {
        public Task<WaveAudio> ReadRecordingAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<byte[]> ReadReplayAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
