using System.Text.Json;
using Ansible.Core;
using Ansible.Core.Windows;

namespace Ansible.Tests;

public sealed class MeetingCaptureDiagnosticsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Ansible-audio-diagnostics-" + Guid.NewGuid().ToString("N"));
    private static MeetingCaptureHealth EmptyHealth => new MeetingCaptureMetrics(
        new(123)).Snapshot(0, null);
    private static MeetingCaptureDiagnostic Entry => new(Guid.NewGuid(), DateTimeOffset.UtcNow,
        MeetingCaptureDiagnosticKind.Sample, MeetingCaptureStage.ReadPlayback, EmptyHealth);

    [Fact]
    public void MissingPlaybackRemainsDifferentFromSilentPacketsAndMicrophoneSignal()
    {
        var metrics = new MeetingCaptureMetrics(new(123));
        metrics.ObservePacket(true, new([0, 64, 0, 64], 1, 0), 1);
        metrics.ObserveMix(640, 2, 0, 0.5, 0);
        var missing = metrics.Snapshot(2, null);
        Assert.Equal(1, missing.Microphone.Packets);
        Assert.Equal(2, missing.Microphone.ReceivedSamples);
        Assert.Equal(0.5, missing.Microphone.PeakInputRms);
        Assert.Equal(1, missing.Microphone.LastSignalAgeSeconds);
        Assert.Equal(638, missing.Microphone.MissingMixedSamples);
        Assert.Equal(0.5, missing.Microphone.WindowMixedRms);
        Assert.Equal(0, missing.Playback.Packets);
        Assert.Equal(640, missing.Playback.MissingMixedSamples);
        Assert.Null(missing.Playback.LastPacketAgeSeconds);
        Assert.Equal("No audio packets", missing.Playback.SignalStatus);

        metrics.ObservePacket(false, new(new byte[320], 1, 0, 2), 3);
        var silent = metrics.Snapshot(4, null);
        Assert.Equal(1, silent.Playback.Packets);
        Assert.Equal(160, silent.Playback.ReceivedSamples);
        Assert.Equal(160, silent.Playback.SilentSamples);
        Assert.Equal(1, silent.Playback.LastPacketAgeSeconds);
        Assert.Null(silent.Playback.LastSignalAgeSeconds);
        Assert.Equal("Quiet", silent.Playback.SignalStatus);
        Assert.Equal(0, silent.Microphone.WindowMixedRms);
        Assert.Equal(0.5, silent.Microphone.PeakInputRms);
    }

    [Fact]
    public void PlaybackSignalHasAgesAndDoesNotMeanSpeechWasRecognized()
    {
        var metrics = new MeetingCaptureMetrics(new());
        metrics.ObservePacket(false, new([0, 32], 1, 0), 10);
        var signal = metrics.Snapshot(11, null);
        Assert.Equal("Audio detected", signal.Playback.SignalStatus);
        Assert.Equal(0.25, signal.Playback.WindowInputRms);
        Assert.Equal(0, signal.Playback.WindowMixedRms);
        Assert.Equal(0, signal.Playback.SilentSamples);
        Assert.False(signal.PlaybackIsQuiet);
        Assert.False(metrics.Snapshot(24.99, null).PlaybackIsQuiet);
        var quiet = metrics.Snapshot(25, null);
        Assert.True(quiet.PlaybackIsQuiet);
        Assert.Equal("No recent audio packets", quiet.Playback.SignalStatus);
        Assert.Equal(0, quiet.Playback.WindowInputRms);
        Assert.Equal(0.25, quiet.Playback.PeakInputRms);
    }

    [Fact]
    public void SilenceHintWaitsFifteenSecondsAndQuietPcmIsNotFlaggedSilent()
    {
        var metrics = new MeetingCaptureMetrics(new(123));
        Assert.False(metrics.Snapshot(14.99, null).PlaybackIsQuiet);
        metrics.ObservePacket(false, new(new byte[320], 1, 0), 15);
        var health = metrics.Snapshot(15, "Audio diagnostics could not be saved.");
        Assert.True(health.PlaybackIsQuiet);
        Assert.Equal(0, health.Playback.SilentSamples);
        Assert.Equal("Quiet", health.Playback.SignalStatus);
        Assert.NotNull(health.DiagnosticsError);
    }

    [Fact]
    public void TimelineCountsCapturedSilenceInsteadOfTreatingItAsMissing()
    {
        var timeline = new MeetingPacketTimeline();
        timeline.Add(2, new byte[8], 6);
        Assert.Equal(new byte[16], timeline.Read(8, out var actual));
        Assert.Equal(4, actual);
        timeline.Read(8, out var absent);
        Assert.Equal(0, absent);
    }

    [Fact]
    public async Task DisposalDrainsStructuredLocalMetadataAndHasNoContentFields()
    {
        var entry = Entry with { Pipeline = new(MeetingSessionPhase.Recording, 12, 10, 3, 8) };
        await using (var log = new LocalDiagnosticLog(_directory, new Version(1, 2, 3, 4))) { log.SetEnabled(true); log.Write(entry); }
        var files = Directory.GetFiles(_directory);
        var lines = await File.ReadAllLinesAsync(Assert.Single(files));
        using var json = JsonDocument.Parse(Assert.Single(lines));
        var root = json.RootElement;
        Assert.Equal(entry.CaptureId, root.GetProperty("CaptureId").GetGuid());
        Assert.Equal("Sample", root.GetProperty("Kind").GetString());
        Assert.Equal("ReadPlayback", root.GetProperty("Stage").GetString());
        Assert.False(root.GetProperty("Health").TryGetProperty("ProcessId", out _));
        Assert.False(root.GetProperty("Health").TryGetProperty("DiagnosticsError", out _));
        Assert.Equal(12, root.GetProperty("Pipeline").GetProperty("CapturedSeconds").GetDouble());
        Assert.Equal("Recording", root.GetProperty("Pipeline").GetProperty("Phase").GetString());
        Assert.Equal(10, root.GetProperty("Pipeline").GetProperty("ConsumedSeconds").GetDouble());
        Assert.Equal(3, root.GetProperty("Pipeline").GetProperty("RecognitionUpdates").GetInt64());
        Assert.Equal(8, root.GetProperty("Pipeline").GetProperty("ProcessedSeconds").GetDouble());
        Assert.Equal(new[] { "CaptureId", "TimestampUtc", "Kind", "Stage", "Health", "ErrorHResult", "DroppedEvents", "Pipeline", "Family", "SchemaVersion", "RunId", "MeetingOperationId", "AppBuild" },
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal("MeetingCapture", root.GetProperty("Family").GetString());
        Assert.Equal("1.2.3.4", root.GetProperty("AppBuild").GetString());
        Assert.Equal(new[] { "ElapsedSeconds", "OutputDevices", "SkippedOutputDevices", "Microphone", "Playback", "CaptureId", "OutputDevicesLost" },
            root.GetProperty("Health").EnumerateObject().Select(property => property.Name));
        Assert.Equal(new[] { "Packets", "ReceivedSamples", "SilentSamples", "MissingMixedSamples", "PeakInputRms",
                "WindowInputRms", "WindowMixedRms", "LastPacketAgeSeconds", "LastSignalAgeSeconds", "LastPacketTiming" },
            root.GetProperty("Health").GetProperty("Playback").EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain(_directory, lines[0]);
    }

    [Fact]
    public async Task RotationRetainsOnlyCurrentAndPreviousFile()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "local-diagnostics.jsonl");
        await File.WriteAllTextAsync(path, new string('x', (int)LocalDiagnosticLog.MaximumFileBytes));
        await File.WriteAllTextAsync(path + ".previous", "Older log");
        await using (var log = new LocalDiagnosticLog(_directory)) { log.SetEnabled(true); log.Write(Entry); }
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
        Assert.Equal(LocalDiagnosticLog.MaximumFileBytes, new FileInfo(path + ".previous").Length);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(0, json.RootElement.GetProperty("DroppedEvents").GetInt64());
    }

    [Fact]
    public async Task StorageFailureIsObservableAndExceptionTextIsNotSerialized()
    {
        const string privateContent = "PRIVATE-TRANSCRIPT-AND-PATH-SENTINEL";
        var fail = true;
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var log = new LocalDiagnosticLog(_directory, (path, line) =>
        {
            if (fail) { observed.TrySetResult(); throw new IOException(privateContent); }
            return File.AppendAllTextAsync(path, line);
        });
        log.SetEnabled(true);
        log.Write(Entry);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => log.Error is not null);
        Assert.DoesNotContain(privateContent, log.Error);
        fail = false;
        log.Write(Entry);
        await log.DisposeAsync();
        var text = await File.ReadAllTextAsync(Path.Combine(_directory, "local-diagnostics.jsonl"));
        Assert.DoesNotContain(privateContent, text);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(1, json.RootElement.GetProperty("DroppedEvents").GetInt64());
    }

    [Fact]
    public async Task BlockedDiskHasABoundedQueueWithoutBlockingCaptureAndShutdownJoinsWriter()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new List<string>();
        await using var log = new LocalDiagnosticLog(_directory, async (_, line) =>
        {
            entered.TrySetResult();
            await release.Task;
            lines.Add(line);
        });
        log.SetEnabled(true);
        log.Write(Entry);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 70; index++) log.Write(Entry);
        Assert.NotNull(log.Error);
        var close = log.DisposeAsync().AsTask();
        try { Assert.False(close.IsCompleted); }
        finally { release.TrySetResult(); }
        await close.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(65, lines.Count);
        using var json = JsonDocument.Parse(lines[^1]);
        Assert.Equal(6, json.RootElement.GetProperty("DroppedEvents").GetInt64());
    }

    [Fact]
    public async Task ReenableDoesNotCarryDroppedCountsFromAnEarlierConsentGeneration()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var log = new LocalDiagnosticLog(_directory, async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        log.SetEnabled(true);
        log.Write(Entry);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 70; index++) log.Write(Entry);
        Assert.True(log.DroppedEvents > 0);
        log.SetEnabled(false);
        log.SetEnabled(true);
        release.SetResult();
        await log.DisposeAsync();
        Assert.Equal(0, log.DroppedEvents);
        Assert.Null(log.Error);
    }

    [Fact]
    public async Task DisabledLogNeverCreatesDirectory()
    {
        await using (var log = new LocalDiagnosticLog(_directory))
        {
            log.Write(Entry);
            Assert.False(log.Enabled);
        }
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task OptOutDiscardsQueuedEntriesAndDoesNotReplayThemOnReenable()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new List<string>();
        await using var log = new LocalDiagnosticLog(_directory, async (_, line) =>
        {
            entered.TrySetResult();
            await release.Task;
            lines.Add(line);
        });
        log.SetEnabled(true);
        var first = Entry;
        var stale = Entry;
        var last = Entry;
        log.Write(first);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        log.Write(stale);
        log.SetEnabled(false);
        log.Write(Entry);
        log.SetEnabled(true);
        log.Write(last);
        release.SetResult();
        await log.DisposeAsync();
        Assert.Equal(2, lines.Count);
        Assert.Contains(first.CaptureId.ToString(), lines[0]);
        Assert.Contains(last.CaptureId.ToString(), lines[1]);
        Assert.DoesNotContain(stale.CaptureId.ToString(), string.Join("", lines));
    }

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
