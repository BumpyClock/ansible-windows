using System.Collections.Concurrent;
using System.Text.Json;
using Ansible.Core;
using Ansible.Core.Windows;

namespace Ansible.Tests;

/// <summary>Runs the real meeting capture worker against fake devices and a manual 100 ns clock.</summary>
public sealed class MeetingCaptureWorkerTests
{
    private const short MicrophoneValue = 1000, OutputValue = 2000;
    private const long JitterMarginSamples = 4000 + 640;
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NoRescan = TimeSpan.FromHours(1);

    [Fact]
    public async Task MicrophoneAndOutputMixIntoContiguousAudioThatDrainsToTheStopPosition()
    {
        var devices = new FakeDevices();
        var speakers = devices.AddOutput("speakers");
        var capture = await StartAsync(devices);
        var probe = new CaptureProbe(capture);
        var terminal = new ConcurrentQueue<(bool Released, bool Completed)>();
        capture.HealthChanged += _ =>
        {
            if (devices.Microphone.Disposals > 0) terminal.Enqueue((capture.IsReleased, capture.Audio.Completion.IsCompleted));
        };

        await AdvanceAsync(devices, capture, probe, TimeSpan.FromSeconds(3));
        await capture.StopAsync().WaitAsync(WaitLimit);

        Assert.True(capture.IsReleased);
        Assert.Equal(48000, capture.ClockSamplePosition);
        Assert.Equal(48000, probe.Emitted);
        probe.AssertContiguous();
        var audio = await ReadAllAsync(capture);
        Assert.Equal(48000, audio.Length);
        Assert.All(audio, sample => Assert.Equal(MicrophoneValue + OutputValue, sample));
        Assert.Equal(300, capture.Health!.Microphone.Packets);
        Assert.Equal(300, capture.Health.Playback.Packets);
        Assert.Equal(1, devices.Microphone.Disposals);
        Assert.Equal(1, speakers.Disposals);
        Assert.Equal((true, false), Assert.Single(terminal));
    }

    [Fact]
    public async Task StartupScansOutputsBeforeTheMicrophoneAndTeardownReleasesInOwnerOrder()
    {
        var devices = new FakeDevices();
        var speakers = devices.AddOutput("speakers");
        var capture = await StartAsync(devices);
        var probe = new CaptureProbe(capture);
        await AdvanceAsync(devices, capture, probe, TimeSpan.FromSeconds(1));

        await capture.StopAsync().WaitAsync(WaitLimit);
        await capture.StopAsync().WaitAsync(WaitLimit);
        await capture.DisposeAsync().AsTask().WaitAsync(WaitLimit);
        await capture.DisposeAsync().AsTask().WaitAsync(WaitLimit);

        var events = devices.Events;
        var captureThread = SingleEvent(events, "microphone.start").Thread;
        var scannerThread = events.Where(entry => entry.Event == "enter").Select(entry => entry.Thread).Distinct()
            .Single(thread => thread != captureThread);
        Assert.True(IndexOf(events, "catalog.ids") < IndexOf(events, "microphone.start"));
        Assert.True(IndexOf(events, "microphone.start") < IndexOf(events, "speakers.start"));
        Assert.True(IndexOf(events, "speakers.stop") < IndexOf(events, "microphone.stop"));
        Assert.True(IndexOf(events, "exit", scannerThread) < IndexOf(events, "speakers.dispose"));
        Assert.True(IndexOf(events, "microphone.dispose") < IndexOf(events, "exit", captureThread));
        Assert.All(events.Where(entry => entry.Event.StartsWith("speakers.", StringComparison.Ordinal)
                || entry.Event.StartsWith("microphone.", StringComparison.Ordinal)),
            entry => Assert.Equal(captureThread, entry.Thread));
        Assert.Equal(new[] { captureThread }, speakers.ReadThreads);
        Assert.Equal(new[] { captureThread }, devices.Microphone.ReadThreads);
        AssertApartmentsBalanced(events);
        Assert.Equal(1, devices.Microphone.Disposals);
        Assert.Equal(1, speakers.Disposals);
    }

    [Fact]
    public async Task LateOutputIsPreparedByTheScannerAndAdoptedByTheCaptureThread()
    {
        var devices = new FakeDevices();
        var capture = await StartAsync(devices, TimeSpan.FromMilliseconds(20));
        var probe = new CaptureProbe(capture);
        var headset = devices.AddOutput("headset");
        await WaitUntilAsync(() => headset.IsStarted);

        await AdvanceAsync(devices, capture, probe, TimeSpan.FromSeconds(1.5));
        await capture.StopAsync().WaitAsync(WaitLimit);

        var events = devices.Events;
        var captureThread = SingleEvent(events, "microphone.start").Thread;
        Assert.NotEqual(captureThread, SingleEvent(events, "headset.open").Thread);
        Assert.Equal(captureThread, SingleEvent(events, "headset.start").Thread);
        Assert.Equal(captureThread, SingleEvent(events, "headset.stop").Thread);
        Assert.Equal(captureThread, SingleEvent(events, "headset.dispose").Thread);
        Assert.Equal(new[] { captureThread }, headset.ReadThreads);
        Assert.All(await ReadAllAsync(capture), sample => Assert.Equal(MicrophoneValue + OutputValue, sample));
        Assert.True(capture.IsReleased);
        AssertApartmentsBalanced(events);
    }

    [Fact]
    public async Task UnavailableOutputEnumerationLeavesAMicrophoneOnlyMeeting()
    {
        var devices = new FakeDevices { ActiveIdsError = new IOException("Enumeration failed.") };
        await using var diagnostics = new DiagnosticRecorder();
        var capture = await StartAsync(devices, diagnostics: diagnostics.Log);
        var probe = new CaptureProbe(capture);

        await AdvanceAsync(devices, capture, probe, TimeSpan.FromSeconds(1.5));
        await capture.StopAsync().WaitAsync(WaitLimit);

        var audio = await ReadAllAsync(capture);
        Assert.Equal(24000, audio.Length);
        Assert.All(audio, sample => Assert.Equal(MicrophoneValue, sample));
        Assert.Contains((MeetingCaptureDiagnosticKind.OutputSkipped, MeetingCaptureStage.EnumerateOutputs), await diagnostics.FlushAsync());
        Assert.Equal(0, capture.Health!.OutputDevices);
        Assert.True(capture.IsReleased);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputFailureDropsOnlyThatOutput(bool discontinuity)
    {
        var devices = new FakeDevices();
        var speakers = devices.AddOutput("speakers");
        if (discontinuity) speakers.Flags = packet => packet == 50 ? MeetingCapturePacket.Discontinuity : 0;
        await using var diagnostics = new DiagnosticRecorder();
        var capture = await StartAsync(devices, diagnostics: diagnostics.Log);
        var probe = new CaptureProbe(capture);

        await AdvanceAsync(devices, capture, probe, TimeSpan.FromSeconds(1.5));
        if (!discontinuity) speakers.ReadError = new IOException("Output removed.");
        await AdvanceAsync(devices, capture, probe, TimeSpan.FromSeconds(1.5));
        await capture.StopAsync().WaitAsync(WaitLimit);

        var audio = await ReadAllAsync(capture);
        Assert.Equal(48000, audio.Length);
        Assert.Equal(MicrophoneValue, audio[^1]);
        Assert.Equal(1, capture.Health!.OutputDevicesLost);
        Assert.Equal(1, speakers.Disposals);
        Assert.Contains((MeetingCaptureDiagnosticKind.OutputLost, MeetingCaptureStage.ReadPlayback), await diagnostics.FlushAsync());
        Assert.True(capture.IsReleased);
    }

    [Fact]
    public async Task FirstMicrophonePacketMayReportADiscontinuity()
    {
        var devices = new FakeDevices();
        devices.Microphone.Flags = packet => packet == 0 ? MeetingCapturePacket.Discontinuity : 0;
        var capture = await StartAsync(devices);
        var probe = new CaptureProbe(capture);

        await AdvanceAsync(devices, capture, probe, TimeSpan.FromSeconds(1));
        await capture.StopAsync().WaitAsync(WaitLimit);

        Assert.All(await ReadAllAsync(capture), sample => Assert.Equal(MicrophoneValue, sample));
        Assert.Equal(16000, probe.Emitted);
    }

    [Theory]
    [InlineData(5, MeetingCapturePacket.Discontinuity, "discontinuity")]
    [InlineData(3, MeetingCapturePacket.TimestampError, "invalid audio timestamp")]
    public async Task InvalidMicrophonePacketFailsTheMeetingVisibly(int packet, uint flags, string message)
    {
        var devices = new FakeDevices();
        devices.Microphone.Flags = index => index == packet ? flags : 0;
        await using var diagnostics = new DiagnosticRecorder();
        var capture = await StartAsync(devices, diagnostics: diagnostics.Log);
        var probe = new CaptureProbe(capture);

        await AdvanceAsync(devices, capture, probe, TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<IOException>(() => ReadAllAsync(capture));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.True(capture.IsReleased);
        await capture.StopAsync().WaitAsync(WaitLimit);
        var entries = await diagnostics.FlushAsync();
        Assert.Contains((MeetingCaptureDiagnosticKind.Failed, MeetingCaptureStage.ReadMicrophone), entries);
        Assert.Equal(MeetingCaptureDiagnosticKind.Stopped, entries[^1].Kind);
    }

    [Fact]
    public async Task MicrophoneReadFailureCompletesAudioAfterRelease()
    {
        var devices = new FakeDevices();
        var speakers = devices.AddOutput("speakers");
        var capture = await StartAsync(devices);
        var terminal = new ConcurrentQueue<(bool Released, bool Completed)>();
        capture.HealthChanged += _ =>
        {
            if (devices.Microphone.Disposals > 0) terminal.Enqueue((capture.IsReleased, capture.Audio.Completion.IsCompleted));
        };
        var failure = new IOException("Microphone removed.");

        devices.Microphone.ReadError = failure;

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => ReadAllAsync(capture)));
        await capture.StopAsync().WaitAsync(WaitLimit);
        Assert.NotEmpty(terminal);
        Assert.All(terminal, snapshot => Assert.Equal((true, false), snapshot));
        await capture.DisposeAsync().AsTask().WaitAsync(WaitLimit);
        Assert.Equal(1, devices.Microphone.Disposals);
        Assert.Equal(1, speakers.Disposals);
        AssertApartmentsBalanced(devices.Events);
    }

    [Fact]
    public async Task FullAudioQueueFailsInsteadOfDroppingAudio()
    {
        var devices = new FakeDevices();
        var capture = await StartAsync(devices);
        var probe = new CaptureProbe(capture);

        await AdvanceAsync(devices, capture, probe, TimeSpan.FromSeconds(40));

        var error = await Assert.ThrowsAsync<IOException>(() => ReadAllAsync(capture));
        Assert.Contains("falling behind", error.Message, StringComparison.Ordinal);
        Assert.True(capture.IsReleased);
        await capture.StopAsync().WaitAsync(WaitLimit);
    }

    [Fact]
    public async Task MicrophoneStartFailureRethrowsAndReleasesPreparedOutputs()
    {
        var devices = new FakeDevices();
        var speakers = devices.AddOutput("speakers");
        var failure = new IOException("Microphone in use.");
        devices.Microphone.StartError = failure;

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => StartAsync(devices)));

        Assert.Single(devices.Events, entry => entry.Event == "speakers.open");
        Assert.False(speakers.IsStarted);
        Assert.Equal(1, speakers.Disposals);
        Assert.Equal(1, devices.Microphone.Disposals);
        AssertApartmentsBalanced(devices.Events);
    }

    [Fact]
    public async Task CancellationDuringStartupReleasesEverything()
    {
        using var gate = new ManualResetEventSlim();
        var devices = new FakeDevices { OpenMicrophoneGate = gate };
        var speakers = devices.AddOutput("speakers");
        using var cancellation = new CancellationTokenSource();
        var factory = new MeetingCaptureFactory(null, devices, () => devices.Clock.Now, NoRescan);

        var start = factory.StartAsync(new(), cancellation.Token);
        await devices.OpenMicrophoneEntered.Task.WaitAsync(WaitLimit);
        cancellation.Cancel();
        gate.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(WaitLimit));
        Assert.False(devices.Microphone.IsStarted);
        Assert.Equal(1, devices.Microphone.Disposals);
        Assert.Equal(1, speakers.Disposals);
        AssertApartmentsBalanced(devices.Events);
    }

    [Fact]
    public async Task ReleaseFailureKeepsTheCaptureOwnedAndFaultsAudio()
    {
        var devices = new FakeDevices();
        var leak = new IOException("Release failed.");
        devices.Microphone.DisposeError = leak;
        var capture = await StartAsync(devices);
        var probe = new CaptureProbe(capture);
        await AdvanceAsync(devices, capture, probe, TimeSpan.FromSeconds(1));

        await capture.StopAsync().WaitAsync(WaitLimit);

        Assert.False(capture.IsReleased);
        Assert.Equal(16000, probe.Emitted);
        var error = await Assert.ThrowsAsync<AggregateException>(() => ReadAllAsync(capture));
        Assert.Contains(leak, error.InnerExceptions);
    }

    [Fact]
    public async Task StartupReleaseFailureTransfersOwnership()
    {
        var devices = new FakeDevices();
        var failure = new IOException("Microphone in use.");
        devices.Microphone.StartError = failure;
        devices.Microphone.DisposeError = new IOException("Release failed.");

        var error = await Assert.ThrowsAsync<MeetingCaptureOwnershipException>(() => StartAsync(devices));

        Assert.Same(failure, error.InnerException);
        Assert.False(error.Capture.IsReleased);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputStopFailureDuringTeardownStillReleasesEveryResource(bool cancellation)
    {
        var devices = new FakeDevices();
        var output = devices.AddOutput("speakers");
        var stopFailure = cancellation ? (Exception)new OperationCanceledException("Output stop cancelled.")
            : new IOException("Output stop failed.");
        output.StopError = stopFailure;
        await using var capture = await StartAsync(devices);
        var readFailure = new IOException("Microphone removed.");
        devices.Microphone.ReadError = readFailure;

        var error = await Assert.ThrowsAsync<AggregateException>(() => ReadAllAsync(capture));
        await capture.StopAsync().WaitAsync(WaitLimit);

        Assert.Contains(readFailure, error.InnerExceptions);
        Assert.Contains(stopFailure, error.InnerExceptions);
        Assert.True(capture.IsReleased);
        Assert.Equal(1, output.Disposals);
        Assert.Equal(1, devices.Microphone.Disposals);
        AssertApartmentsBalanced(devices.Events);
    }

    [Fact]
    public async Task FailedOutputReleaseKeepsThePrimaryFailureAndOwnershipVisible()
    {
        var devices = new FakeDevices();
        var output = devices.AddOutput("speakers");
        var releaseFailure = new IOException("Output release failed.");
        output.DisposeError = releaseFailure;
        await using var diagnostics = new DiagnosticRecorder();
        await using var capture = await StartAsync(devices, diagnostics: diagnostics.Log);
        var readFailure = new IOException("Microphone removed.");
        devices.Microphone.ReadError = readFailure;

        var error = await Assert.ThrowsAsync<AggregateException>(() => ReadAllAsync(capture));
        await capture.StopAsync().WaitAsync(WaitLimit);

        Assert.Contains("remain owned", error.Message, StringComparison.Ordinal);
        Assert.Contains(readFailure, error.InnerExceptions);
        Assert.Contains(releaseFailure, error.InnerExceptions);
        Assert.False(capture.IsReleased);
        Assert.Equal(1, output.Disposals);
        Assert.Equal(1, devices.Microphone.Disposals);
        Assert.Single(await diagnostics.FlushAsync(), entry => entry.Kind == MeetingCaptureDiagnosticKind.Failed);
    }

    [Fact]
    public async Task ThrowingTerminalHealthSubscriberFaultsAudioAfterRelease()
    {
        var devices = new FakeDevices();
        devices.AddOutput("speakers");
        await using var capture = await StartAsync(devices);
        var failure = new InvalidOperationException("Subscriber failed.");
        capture.HealthChanged += _ => { if (capture.IsReleased) throw failure; };

        await capture.StopAsync().WaitAsync(WaitLimit);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAllAsync(capture)));
        Assert.True(capture.IsReleased);
        AssertApartmentsBalanced(devices.Events);
    }

    [Fact]
    public async Task CancellationExceptionDuringDrainIsNotSuccessfulCompletion()
    {
        var devices = new FakeDevices();
        await using var capture = await StartAsync(devices);
        var probe = new CaptureProbe(capture);
        await AdvanceAsync(devices, capture, probe, TimeSpan.FromSeconds(1));
        capture.Activity += _ =>
        {
            if (devices.Microphone.IsStopped) throw new OperationCanceledException("Subscriber cancelled.");
        };

        await capture.StopAsync().WaitAsync(WaitLimit);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAllAsync(capture));
        Assert.True(capture.IsReleased);
        Assert.True(capture.CapturedSeconds < 1);
    }

    [Fact]
    public async Task SlowOutputStopStillDrainsOnlyToTheFrozenStopPosition()
    {
        var devices = new FakeDevices();
        var output = devices.AddOutput("speakers");
        output.OnStop = () => devices.Clock.Advance(TimeSpan.FromMilliseconds(100));
        await using var capture = await StartAsync(devices);
        var probe = new CaptureProbe(capture);
        await AdvanceAsync(devices, capture, probe, TimeSpan.FromSeconds(1));

        await capture.StopAsync().WaitAsync(WaitLimit);

        Assert.Equal(16000, capture.ClockSamplePosition);
        Assert.Equal(16000, (await ReadAllAsync(capture)).Length);
        Assert.Equal(110, capture.Health!.Microphone.Packets);
        Assert.True(capture.IsReleased);
        probe.AssertContiguous();
    }

    [Fact]
    public async Task MicrophoneStopFailureIsReportedWithoutASecondStopAttempt()
    {
        var devices = new FakeDevices();
        var failure = new IOException("Microphone stop failed.");
        devices.Microphone.StopError = failure;
        await using var capture = await StartAsync(devices);

        await capture.StopAsync().WaitAsync(WaitLimit);

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => ReadAllAsync(capture)));
        Assert.Single(devices.Events, entry => entry.Event == "microphone.stop");
        Assert.Equal(1, devices.Microphone.Disposals);
        Assert.True(capture.IsReleased);
    }

    [Fact]
    public async Task FailingClockCannotSkipDeviceTeardown()
    {
        var devices = new FakeDevices();
        devices.AddOutput("speakers");
        await using var capture = await StartAsync(devices);
        var failure = new OverflowException("Clock failed.");
        devices.Clock.ReadError = failure;

        var error = await Assert.ThrowsAsync<AggregateException>(() => ReadAllAsync(capture));
        await capture.StopAsync().WaitAsync(WaitLimit);

        Assert.Contains(failure, error.InnerExceptions);
        Assert.True(capture.IsReleased);
        Assert.Equal(0, capture.ClockSamplePosition);
        Assert.Equal(1, devices.Microphone.Disposals);
        AssertApartmentsBalanced(devices.Events);
    }

    [Fact]
    public async Task ScannerPreparedButUnadoptedOutputIsReleasedAfterScannerJoins()
    {
        var devices = new FakeDevices();
        await using var capture = await StartAsync(devices, TimeSpan.FromMilliseconds(20));
        using var gate = new ManualResetEventSlim();
        devices.OpenLoopbackGate = gate;
        var output = devices.AddOutput("late");
        await devices.OpenLoopbackEntered.Task.WaitAsync(WaitLimit);
        var stop = capture.StopAsync();
        try
        {
            Assert.False(stop.IsCompleted);
            Assert.Equal(0, output.Disposals);
        }
        finally { gate.Set(); }
        await stop.WaitAsync(WaitLimit);

        Assert.False(output.IsStarted);
        Assert.Equal(1, output.Disposals);
        var scannerThread = SingleEvent(devices.Events, "late.open").Thread;
        Assert.True(IndexOf(devices.Events, "exit", scannerThread) < IndexOf(devices.Events, "late.dispose"));
        AssertApartmentsBalanced(devices.Events);
    }

    [Fact]
    public async Task TransientScannerCatalogFailureRetriesAndAdoptsALaterOutput()
    {
        var devices = new FakeDevices { CatalogError = attempt => attempt == 2 ? new IOException("Catalog unavailable.") : null };
        await using var diagnostics = new DiagnosticRecorder();
        await using var capture = await StartAsync(devices, TimeSpan.FromMilliseconds(20), diagnostics.Log);
        var output = devices.AddOutput("late");
        await WaitUntilAsync(() => output.IsStarted);

        await capture.StopAsync().WaitAsync(WaitLimit);

        Assert.Contains((MeetingCaptureDiagnosticKind.OutputSkipped, MeetingCaptureStage.EnumerateOutputs), await diagnostics.FlushAsync());
        Assert.Equal(1, output.Disposals);
        AssertApartmentsBalanced(devices.Events);
    }

    [Fact]
    public async Task FatalScannerFailureFaultsCaptureInsteadOfSilentlyDisablingDiscovery()
    {
        var failure = new OperationCanceledException("Scanner stopped unexpectedly.");
        var devices = new FakeDevices { CatalogError = attempt => attempt == 2 ? failure : null };
        await using var capture = await StartAsync(devices, TimeSpan.FromMilliseconds(20));

        var error = await Assert.ThrowsAsync<IOException>(() => ReadAllAsync(capture));
        await capture.StopAsync().WaitAsync(WaitLimit);

        Assert.Same(failure, error.InnerException);
        Assert.True(capture.IsReleased);
        AssertApartmentsBalanced(devices.Events);
    }

    [Fact]
    public async Task FailedOutputStartRetriesWithANewSource()
    {
        var devices = new FakeDevices();
        var first = devices.AddOutput("speakers");
        first.StartError = new IOException("Output busy.");
        await using var capture = await StartAsync(devices, TimeSpan.FromMilliseconds(20));
        await WaitUntilAsync(() => devices.AddOutput("speakers") is { IsStarted: true });
        var replacement = devices.AddOutput("speakers");

        await capture.StopAsync().WaitAsync(WaitLimit);

        Assert.NotSame(first, replacement);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, replacement.Disposals);
        Assert.True(capture.IsReleased);
    }

    [Fact]
    public async Task FatalOutputStartFailureReleasesThePreparedSource()
    {
        var devices = new FakeDevices();
        var output = devices.AddOutput("speakers");
        var failure = new OperationCanceledException("Output start cancelled.");
        output.StartError = failure;

        Assert.Same(failure, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StartAsync(devices)));

        Assert.Equal(1, output.Disposals);
        Assert.Equal(1, devices.Microphone.Disposals);
        AssertApartmentsBalanced(devices.Events);
    }

    [Fact]
    public async Task RemovedOutputIsReleasedWithoutEndingMicrophoneCapture()
    {
        var devices = new FakeDevices();
        var output = devices.AddOutput("speakers");
        await using var capture = await StartAsync(devices, TimeSpan.FromMilliseconds(20));
        devices.RemoveOutput("speakers");
        await WaitUntilAsync(() => output.Disposals == 1);
        var probe = new CaptureProbe(capture);
        await AdvanceAsync(devices, capture, probe, TimeSpan.FromSeconds(1));

        await capture.StopAsync().WaitAsync(WaitLimit);

        Assert.Equal(1, capture.Health!.OutputDevicesLost);
        Assert.All(await ReadAllAsync(capture), sample => Assert.Equal(MicrophoneValue, sample));
        Assert.True(capture.IsReleased);
    }

    [Theory]
    [InlineData("zero", "invalid audio timestamp")]
    [InlineData("overflow", "invalid audio timestamp")]
    [InlineData("regression", "regressing device position")]
    public async Task InvalidPacketPositionsFaultTheActualWorker(string scenario, string message)
    {
        var devices = new FakeDevices();
        if (scenario == "regression")
            devices.Microphone.Position = packet => packet == 3 ? 1UL : (ulong)(packet * 160);
        else
            devices.Microphone.Timestamp = packet => packet == 3 ? (scenario == "zero" ? 0UL : ulong.MaxValue) : null;
        await using var capture = await StartAsync(devices);
        var probe = new CaptureProbe(capture);
        await AdvanceAsync(devices, capture, probe, TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<IOException>(() => ReadAllAsync(capture));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.True(capture.IsReleased);
    }

    private static Task<IMeetingCapture> StartAsync(FakeDevices devices, TimeSpan? outputScanInterval = null,
        LocalDiagnosticLog? diagnostics = null) =>
        new MeetingCaptureFactory(diagnostics, devices, () => devices.Clock.Now, outputScanInterval ?? NoRescan)
            .StartAsync(new(), CancellationToken.None).WaitAsync(WaitLimit);

    /// <summary>
    /// Advances the clock and waits until every live source has observed it and emission has reached the jitter margin.
    /// The first step stays below the margin so no audio is emitted before sources have read past it, and later steps
    /// stay inside the two-second packet timeline.
    /// </summary>
    private static async Task AdvanceAsync(FakeDevices devices, IMeetingCapture capture, CaptureProbe probe, TimeSpan duration)
    {
        var remaining = duration;
        while (remaining > TimeSpan.Zero)
        {
            var step = capture.ClockSamplePosition == 0 ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1.5);
            if (step > remaining) step = remaining;
            devices.Clock.Advance(step);
            remaining -= step;
            var now = devices.Clock.Now;
            await WaitUntilAsync(() => capture.IsReleased || capture.Audio.Completion.IsCompleted
                || (devices.SourcesObserved(now) && probe.Emitted >= capture.ClockSamplePosition - JitterMarginSamples));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Meeting capture worker did not reach the expected state.");
            await Task.Delay(2);
        }
    }

    /// <summary>Drains <see cref="IMeetingCapture.Audio"/>; its completion fault surfaces only after queued audio is read.</summary>
    private static Task<short[]> ReadAllAsync(IMeetingCapture capture) => DrainAsync(capture).WaitAsync(WaitLimit);

    private static async Task<short[]> DrainAsync(IMeetingCapture capture)
    {
        var samples = new List<short>();
        await foreach (var packet in capture.Audio.ReadAllAsync())
            for (var index = 0; index < packet.Length; index += 2) samples.Add((short)(packet[index] | packet[index + 1] << 8));
        await capture.Audio.Completion;
        return [.. samples];
    }

    private static (string Event, int Thread) SingleEvent(IReadOnlyList<(string Event, int Thread)> events, string name) =>
        Assert.Single(events, entry => entry.Event == name);

    private static int IndexOf(IReadOnlyList<(string Event, int Thread)> events, string name, int? thread = null)
    {
        for (var index = 0; index < events.Count; index++)
            if (events[index].Event == name && (thread is null || events[index].Thread == thread)) return index;
        throw new Xunit.Sdk.XunitException($"Missing device event {name}.");
    }

    private static void AssertApartmentsBalanced(IReadOnlyList<(string Event, int Thread)> events)
    {
        var entered = events.Where(entry => entry.Event == "enter").Select(entry => entry.Thread).Order().ToArray();
        var exited = events.Where(entry => entry.Event == "exit").Select(entry => entry.Thread).Order().ToArray();
        Assert.NotEmpty(entered);
        Assert.Equal(entered, exited);
    }

    private sealed class CaptureProbe
    {
        private readonly ConcurrentQueue<MeetingAudioActivity> _activity = new();
        private long _emitted;
        public CaptureProbe(IMeetingCapture capture) => capture.Activity += activity =>
        {
            _activity.Enqueue(activity);
            Interlocked.Add(ref _emitted, activity.Samples);
        };
        public long Emitted => Interlocked.Read(ref _emitted);
        public void AssertContiguous()
        {
            var next = 0L;
            foreach (var activity in _activity)
            {
                Assert.Equal(next, activity.StartSample);
                next += activity.Samples;
            }
        }
    }

    private sealed class DiagnosticRecorder : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "ansible-meeting-worker-" + Guid.NewGuid().ToString("N"));
        private readonly ConcurrentQueue<string> _lines = new();
        public DiagnosticRecorder()
        {
            Log = new(_directory, (_, line) => { _lines.Enqueue(line); return Task.CompletedTask; });
            Log.SetEnabled(true);
        }
        public LocalDiagnosticLog Log { get; }
        public async Task<IReadOnlyList<(MeetingCaptureDiagnosticKind Kind, MeetingCaptureStage Stage)>> FlushAsync()
        {
            await Log.DisposeAsync();
            return [.. _lines.Select(line =>
            {
                using var json = JsonDocument.Parse(line);
                return (Enum.Parse<MeetingCaptureDiagnosticKind>(json.RootElement.GetProperty("Kind").GetString()!),
                    Enum.Parse<MeetingCaptureStage>(json.RootElement.GetProperty("Stage").GetString()!));
            })];
        }
        public async ValueTask DisposeAsync()
        {
            await Log.DisposeAsync();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    private sealed class ManualClock
    {
        private long _now = 10_000_000_000;
        private Exception? _readError;
        public Exception? ReadError { get => Volatile.Read(ref _readError); set => Volatile.Write(ref _readError, value); }
        public long Now => ReadError is { } error ? throw error : Interlocked.Read(ref _now);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _now, duration.Ticks);
    }

    /// <summary>Fake device boundary that records each device call with the managed thread that made it.</summary>
    private sealed class FakeDevices : IMeetingAudioDevices
    {
        private readonly ConcurrentDictionary<string, FakeSource> _outputs = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<(string Event, int Thread)> _events = new();
        private int _catalogAttempts;

        public FakeDevices() => Microphone = new(this, "microphone", MicrophoneValue);

        public ManualClock Clock { get; } = new();
        public FakeSource Microphone { get; }
        public Exception? ActiveIdsError { get; init; }
        public Func<int, Exception?>? CatalogError { get; init; }
        public ManualResetEventSlim? OpenMicrophoneGate { get; init; }
        public TaskCompletionSource OpenMicrophoneEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim? OpenLoopbackGate { get; set; }
        public TaskCompletionSource OpenLoopbackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<(string Event, int Thread)> Events => [.. _events];

        public FakeSource AddOutput(string id) => _outputs.GetOrAdd(id, name => new(this, name, OutputValue));
        public void RemoveOutput(string id) => _outputs.TryRemove(id, out _);
        public bool SourcesObserved(long now) => _outputs.Values.Append(Microphone).All(source => source.HasObserved(now));
        public void Record(string name) => _events.Enqueue((name, Environment.CurrentManagedThreadId));

        public IDisposable EnterThread()
        {
            Record("enter");
            return new Apartment(this);
        }

        public IMeetingAudioSource OpenMicrophone(Action<MeetingCaptureStage> stage)
        {
            stage(MeetingCaptureStage.OpenMicrophone);
            Record("microphone.open");
            OpenMicrophoneEntered.TrySetResult();
            OpenMicrophoneGate?.Wait(WaitLimit);
            return Microphone;
        }

        public IMeetingOutputCatalog OpenOutputCatalog()
        {
            Record("catalog.open");
            if (CatalogError?.Invoke(Interlocked.Increment(ref _catalogAttempts)) is { } error) throw error;
            return new Catalog(this);
        }

        private sealed class Apartment(FakeDevices devices) : IDisposable
        {
            public void Dispose() => devices.Record("exit");
        }

        private sealed class Catalog(FakeDevices devices) : IMeetingOutputCatalog
        {
            public IReadOnlyList<string> ActiveIds()
            {
                devices.Record("catalog.ids");
                if (devices.ActiveIdsError is { } error) throw error;
                return [.. devices._outputs.Keys.Order(StringComparer.Ordinal)];
            }

            public IMeetingAudioSource OpenLoopback(string id, Action<MeetingCaptureStage> stage)
            {
                stage(MeetingCaptureStage.OpenPlayback);
                devices.Record(id + ".open");
                devices.OpenLoopbackEntered.TrySetResult();
                if (devices.OpenLoopbackGate is { } gate && !gate.Wait(WaitLimit))
                    throw new TimeoutException("Loopback open gate timed out.");
                return devices._outputs.AddOrUpdate(id, name => new(devices, name, OutputValue),
                    (name, source) => source.Disposals == 0 ? source : new(devices, name, OutputValue));
            }

            public void Dispose() => devices.Record("catalog.dispose");
        }
    }

    /// <summary>
    /// Produces 10 ms packets of a constant sample. A packet becomes readable only after an empty read observed a clock
    /// value past its end, so the worker's clock read for its next pass already covers every packet it receives.
    /// </summary>
    private sealed class FakeSource(FakeDevices devices, string name, short value) : IMeetingAudioSource
    {
        private const int PacketSamples = 160;
        private const long PacketTicks = PacketSamples * 625;
        private readonly ConcurrentDictionary<int, byte> _readThreads = new();
        private long _startClock = -1, _horizon = -1, _stopClock = long.MaxValue, _packets;
        private int _disposals;
        private Exception? _readError;

        public Exception? StartError { get; set; }
        public Exception? StopError { get; set; }
        public Exception? DisposeError { get; set; }
        public Action? OnStop { get; set; }
        public Func<long, uint>? Flags { get; set; }
        public Func<long, ulong?>? Timestamp { get; set; }
        public Func<long, ulong>? Position { get; set; }
        public Exception? ReadError { get => Volatile.Read(ref _readError); set => Volatile.Write(ref _readError, value); }
        public bool IsStarted => Interlocked.Read(ref _startClock) >= 0;
        public bool IsStopped => Interlocked.Read(ref _stopClock) != long.MaxValue;
        public int Disposals => Volatile.Read(ref _disposals);
        public int[] ReadThreads => [.. _readThreads.Keys];

        public bool HasObserved(long now) => !IsStarted || Disposals > 0
            || Interlocked.Read(ref _stopClock) != long.MaxValue || Interlocked.Read(ref _horizon) >= now;

        public void Start()
        {
            ObjectDisposedException.ThrowIf(Disposals > 0, this);
            devices.Record(name + ".start");
            if (StartError is { } error) throw error;
            var now = devices.Clock.Now;
            Interlocked.Exchange(ref _horizon, now);
            Interlocked.Exchange(ref _startClock, now);
        }

        public void Stop()
        {
            devices.Record(name + ".stop");
            OnStop?.Invoke();
            Interlocked.Exchange(ref _stopClock, devices.Clock.Now);
            Interlocked.Exchange(ref _horizon, devices.Clock.Now);
            if (StopError is { } error) throw error;
        }

        public MeetingCapturePacket? Read()
        {
            ObjectDisposedException.ThrowIf(Disposals > 0, this);
            _readThreads.TryAdd(Environment.CurrentManagedThreadId, 0);
            if (ReadError is { } error) throw error;
            var start = Interlocked.Read(ref _startClock);
            if (start < 0) return null;
            var timestamp = start + _packets * PacketTicks;
            if (timestamp + PacketTicks > Math.Min(Interlocked.Read(ref _horizon), Interlocked.Read(ref _stopClock)))
            {
                Interlocked.Exchange(ref _horizon, devices.Clock.Now);
                return null;
            }
            var pcm = new byte[PacketSamples * 2];
            for (var index = 0; index < pcm.Length; index += 2)
            {
                pcm[index] = (byte)value;
                pcm[index + 1] = (byte)(value >> 8);
            }
            var packet = new MeetingCapturePacket(pcm, Timestamp?.Invoke(_packets) ?? (ulong)timestamp,
                Position?.Invoke(_packets) ?? (ulong)(_packets * PacketSamples), Flags?.Invoke(_packets) ?? 0);
            _packets++;
            return packet;
        }

        public void Dispose()
        {
            devices.Record(name + ".dispose");
            Interlocked.Increment(ref _disposals);
            if (DisposeError is { } error) throw error;
        }
    }
}
