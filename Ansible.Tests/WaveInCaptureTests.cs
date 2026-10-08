using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Ansible.Core;
using Ansible.Core.Windows;

namespace Ansible.Tests;

public sealed class WaveInCaptureTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    public async Task BoostedSamplesAndMeterRemainCurrentWithoutAnAudioReader(int decibels)
    {
        var api = new FakeWaveInApi();
        await using var capture = await new WaveInCaptureFactory(api).StartAsync(CancellationToken.None, decibels);
        var levels = new List<double>();
        capture.LevelChanged += levels.Add;
        var header = api.Headers[0];
        // More than the inference pipe capacity: metering must not wait for its reader.
        for (var index = 0; index < 20; index++)
        {
            api.Return(header, [0, 0, 100, 0, 156, 255, 160, 15, 96, 240]);
            await api.WaitForRequeueAsync();
        }
        await capture.StopAsync();
        byte[] expected = decibels == 0
            ? [0, 0, 100, 0, 156, 255, 160, 15, 96, 240]
            : [0, 0, 232, 3, 24, 252, 255, 127, 0, 128];
        var expectedLevel = decibels == 0
            ? Math.Sqrt((100.0 * 100 * 2 + 4000.0 * 4000 * 2) / 5) / 32768
            : Math.Sqrt((1000.0 * 1000 * 2 + 32767.0 * 32767 + 32768.0 * 32768) / 5) / 32768;
        Assert.Equal(20, levels.Count);
        Assert.All(levels, level => Assert.Equal(expectedLevel, level, 10));
        var packets = new List<byte[]>();
        await foreach (var packet in capture.Audio.ReadAllAsync()) { packets.Add(packet); }
        Assert.Equal(20, packets.Count);
        Assert.All(packets, packet => Assert.Equal(expected, packet));
        Assert.Equal(200 / 32000.0, capture.CapturedSeconds);
        Assert.Equal(0, api.DriverCallsDuringCallback);
    }

    [Fact]
    public async Task CapturesPcmAndRequeuesOutsideTheNativeCallback()
    {
        var api = new FakeWaveInApi();
        await using var capture = await new WaveInCaptureFactory(api).StartAsync(CancellationToken.None);
        var level = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.LevelChanged += value => level.TrySetResult(value);
        api.Return(api.Headers[0], [0, 128, 0, 64]);
        var packet = await capture.Audio.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await api.WaitForRequeueAsync();
        Assert.Equal([0, 128, 0, 64], packet);
        Assert.Equal(4 / 32000.0, capture.CapturedSeconds);
        Assert.True(await level.Task.WaitAsync(TimeSpan.FromSeconds(5)) > 0);
        Assert.Equal(0, api.DriverCallsDuringCallback);
        Assert.Equal(16000u, api.Format.SamplesPerSecond);
        Assert.Equal((ushort)1, api.Format.Channels);
        Assert.Equal((ushort)16, api.Format.BitsPerSample);
        await capture.StopAsync();
        Assert.True(capture.IsReleased);
        Assert.Empty(api.Headers);
        Assert.True(api.Closed);
        Assert.Equal(["Stop", "Reset", "Unprepare", "Unprepare", "Unprepare", "Unprepare", "Unprepare", "Unprepare", "Close"],
            api.Calls.Where(call => call is "Stop" or "Reset" or "Unprepare" or "Close"));
    }

    [Theory]
    [InlineData("Open", 1)]
    [InlineData("Prepare", 1)]
    [InlineData("Prepare", 3)]
    [InlineData("AddBuffer", 1)]
    [InlineData("AddBuffer", 4)]
    [InlineData("Start", 1)]
    public async Task StartupFaultReleasesEveryConfirmedResource(string operation, int occurrence)
    {
        var api = new FakeWaveInApi();
        api.Fail(operation, occurrence);
        var error = await Assert.ThrowsAsync<Win32Exception>(() =>
            new WaveInCaptureFactory(api).StartAsync(CancellationToken.None));
        Assert.Contains("waveIn error 5", error.Message);
        Assert.Empty(api.Headers);
        Assert.True(api.Closed || operation == "Open");
        Assert.DoesNotContain("Start", api.Calls.SkipWhile(call => call != operation).Skip(1));
    }

    [Theory]
    [InlineData("Unprepare")]
    [InlineData("Close")]
    public async Task StartupFailureCarriesRetryableUnresolvedOwnership(string cleanupOperation)
    {
        var api = new FakeWaveInApi();
        api.Fail("Start");
        api.Fail(cleanupOperation);
        var error = await Assert.ThrowsAsync<CaptureOwnershipException>(() =>
            new WaveInCaptureFactory(api).StartAsync(CancellationToken.None));
        var capture = error.Capture;
        Assert.False(capture.IsReleased);
        Assert.IsType<AggregateException>(error.InnerException);
        Assert.Contains("start recording", error.InnerException!.ToString());
        Assert.Contains(cleanupOperation == "Close" ? "close" : "release", error.InnerException.ToString());
        if (cleanupOperation == "Unprepare")
        {
            Assert.Single(api.Headers);
            api.Return(api.Headers[0], [0, 0]);
            Assert.False(api.Closed);
        }
        else
        {
            Assert.Empty(api.Headers);
            api.ReturnLateCallback();
        }
        await capture.StopAsync();
        Assert.True(capture.IsReleased);
        Assert.True(api.Closed);
        Assert.Equal(1, api.Calls.Count(call => call == "Start"));
        await capture.DisposeAsync();
    }

    [Fact]
    public async Task ResolvedStartupCleanupFaultDoesNotMaskThePrimaryFailure()
    {
        var api = new FakeWaveInApi();
        api.Fail("Start");
        api.Fail("Stop");
        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            new WaveInCaptureFactory(api).StartAsync(CancellationToken.None));
        Assert.Contains("start recording", error.ToString());
        Assert.Contains("stop recording", error.ToString());
        Assert.Empty(api.Headers);
        Assert.True(api.Closed);
    }

    [Fact]
    public async Task UnprepareFailureRetainsOnlyUnreleasedHeadersAndRetriesCleanup()
    {
        var api = new FakeWaveInApi();
        var capture = await new WaveInCaptureFactory(api).StartAsync(CancellationToken.None);
        var original = api.Headers.ToArray();
        api.Fail("Unprepare", 2);
        var error = await Assert.ThrowsAsync<AggregateException>(capture.StopAsync);
        Assert.Single(error.InnerExceptions);
        Assert.False(capture.IsReleased);
        Assert.Equal([original[1]], api.Headers);
        Assert.DoesNotContain("Close", api.Calls);
        var retained = Marshal.PtrToStructure<WaveInHeader>(api.Headers[0]);
        Assert.Equal(1280u, retained.BufferLength);
        Marshal.WriteByte(retained.Data, 123);
        api.Return(api.Headers[0], [1, 2]);
        await capture.StopAsync();
        Assert.True(capture.IsReleased);
        Assert.Empty(api.Headers);
        Assert.Equal(7, api.Calls.Count(call => call == "Unprepare"));
        Assert.Equal(1, api.Calls.Count(call => call == "Start"));
        Assert.True(api.Closed);
    }

    [Fact]
    public async Task CloseFailureRetainsDeviceAndCallbackRootForRetry()
    {
        var api = new FakeWaveInApi();
        var capture = await new WaveInCaptureFactory(api).StartAsync(CancellationToken.None);
        api.Fail("Close");
        await Assert.ThrowsAsync<AggregateException>(capture.StopAsync);
        Assert.False(capture.IsReleased);
        Assert.Empty(api.Headers);
        Assert.False(api.Closed);
        Assert.Same(capture, GCHandle.FromIntPtr((nint)api.Instance).Target);
        api.ReturnLateCallback();
        await capture.StopAsync();
        Assert.True(capture.IsReleased);
        Assert.True(api.Closed);
        Assert.Equal(2, api.Calls.Count(call => call == "Close"));
        Assert.Equal(1, api.Calls.Count(call => call == "Start"));
    }

    [Fact]
    public async Task StopFailureStillAttemptsResetAndReportsBothFailures()
    {
        var api = new FakeWaveInApi();
        var capture = await new WaveInCaptureFactory(api).StartAsync(CancellationToken.None);
        api.Fail("Stop");
        api.Fail("Reset");
        var error = await Assert.ThrowsAsync<AggregateException>(capture.StopAsync);
        Assert.Contains(error.InnerExceptions, item => item.Message.Contains("stop recording"));
        Assert.Contains(error.InnerExceptions, item => item.Message.Contains("return microphone buffers"));
        Assert.Equal(["Stop", "Reset"], api.Calls.Where(call => call is "Stop" or "Reset"));
        Assert.False(capture.IsReleased);
        Assert.Equal(6, api.Headers.Count);
        await capture.StopAsync();
        Assert.True(capture.IsReleased);
        Assert.Equal(["Stop", "Reset", "Stop", "Reset"], api.Calls.Where(call => call is "Stop" or "Reset"));
    }

    [Fact]
    public async Task StopFailureAloneCanReleaseOwnershipWhileStillReportingTheFault()
    {
        var api = new FakeWaveInApi();
        var capture = await new WaveInCaptureFactory(api).StartAsync(CancellationToken.None);
        api.Fail("Stop");
        var error = await Assert.ThrowsAsync<AggregateException>(capture.StopAsync);
        Assert.Single(error.InnerExceptions);
        Assert.True(capture.IsReleased);
        Assert.True(api.Closed);
        await capture.StopAsync();
        Assert.Equal(1, api.Calls.Count(call => call == "Stop"));
    }

    [Fact]
    public async Task ConcurrentStopRequestsShareTheActiveAttemptButNotItsFailedRetry()
    {
        var api = new FakeWaveInApi();
        var capture = await new WaveInCaptureFactory(api).StartAsync(CancellationToken.None);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        api.BeforeCall = operation =>
        {
            if (operation == "Stop")
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException(); }
            }
        };
        api.Fail("Close");
        var first = capture.StopAsync();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var second = capture.StopAsync();
        Assert.Same(first, second);
        release.Set();
        await Assert.ThrowsAsync<AggregateException>(() => first);
        api.BeforeCall = null;
        var retry = capture.StopAsync();
        Assert.NotSame(first, retry);
        await retry;
        Assert.True(capture.IsReleased);
    }

    [Fact]
    public async Task StopWaitsForThePumpBeforeUnpreparingNativeMemory()
    {
        var api = new FakeWaveInApi();
        var capture = await new WaveInCaptureFactory(api).StartAsync(CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        capture.LevelChanged += _ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException(); }
        };
        api.AfterCall = operation =>
        {
            if (operation == "Reset") { reset.TrySetResult(); }
        };
        api.Return(api.Headers[0], [0, 64]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = capture.StopAsync();
        try
        {
            await reset.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stopping.IsCompleted);
            Assert.False(capture.IsReleased);
            Assert.Equal(6, api.Headers.Count);
            Assert.DoesNotContain("Unprepare", api.Calls);
            Assert.DoesNotContain("Close", api.Calls);
            Assert.Equal(1280u, Marshal.PtrToStructure<WaveInHeader>(api.Headers[0]).BufferLength);
        }
        finally
        {
            release.Set();
        }
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(capture.IsReleased);
        Assert.Equal(0, api.DriverCallsDuringCallback);
    }

    [Theory]
    [InlineData("Open", "Prepare")]
    [InlineData("Prepare", "AddBuffer")]
    [InlineData("AddBuffer", "Prepare")]
    [InlineData("Start", null)]
    public async Task CancellationAtEveryStartupBoundaryNeverReturnsAReadyCapture(string cancelAfter, string? forbiddenNext)
    {
        using var cancellation = new CancellationTokenSource();
        var api = new FakeWaveInApi();
        api.AfterCall = operation =>
        {
            if (operation == cancelAfter) { cancellation.Cancel(); }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WaveInCaptureFactory(api).StartAsync(cancellation.Token));
        Assert.Empty(api.Headers);
        Assert.True(api.Closed);
        if (forbiddenNext is not null)
        {
            Assert.DoesNotContain(forbiddenNext, api.Calls.SkipWhile(call => call != cancelAfter).Skip(1));
        }
    }

    [Fact]
    public async Task AlreadyCancelledStartupDoesNotOpenTheDevice()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var api = new FakeWaveInApi();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WaveInCaptureFactory(api).StartAsync(cancellation.Token));
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task CancellationAfterAllBuffersArePreparedStillPreventsDeviceStart()
    {
        using var cancellation = new CancellationTokenSource();
        var api = new FakeWaveInApi();
        var queued = 0;
        api.AfterCall = operation =>
        {
            if (operation == "AddBuffer" && ++queued == 6) { cancellation.Cancel(); }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WaveInCaptureFactory(api).StartAsync(cancellation.Token));
        Assert.DoesNotContain("Start", api.Calls);
        Assert.Empty(api.Headers);
        Assert.True(api.Closed);
    }

    [Fact]
    public async Task CancellationWithCleanupFailurePreservesCaptureOwnership()
    {
        using var cancellation = new CancellationTokenSource();
        var api = new FakeWaveInApi();
        api.AfterCall = operation =>
        {
            if (operation == "Prepare") { cancellation.Cancel(); }
        };
        api.Fail("Unprepare");
        var error = await Assert.ThrowsAsync<CaptureOwnershipException>(() =>
            new WaveInCaptureFactory(api).StartAsync(cancellation.Token));
        Assert.Contains("OperationCanceledException", error.InnerException!.ToString());
        Assert.False(error.Capture.IsReleased);
        Assert.DoesNotContain("Start", api.Calls);
        api.AfterCall = null;
        await error.Capture.StopAsync();
        Assert.True(error.Capture.IsReleased);
    }

    [Fact]
    public async Task NativeStartupRunsOffTheCallingThread()
    {
        var api = new FakeWaveInApi();
        var result = new TaskCompletionSource<IAudioCapture>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            var callerThread = Environment.CurrentManagedThreadId;
            api.BeforeCall = operation =>
            {
                if (operation == "Open") { Assert.NotEqual(callerThread, Environment.CurrentManagedThreadId); }
            };
            try
            {
                result.SetResult(new WaveInCaptureFactory(api).StartAsync(CancellationToken.None).GetAwaiter().GetResult());
            }
            catch (Exception error) { result.SetException(error); }
        });
        caller.Start();
        await using var capture = await result.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(caller.Join(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RequeueFailureSurfacesOnAudioAndAggregatesWithCleanupFault()
    {
        var api = new FakeWaveInApi();
        var capture = await new WaveInCaptureFactory(api).StartAsync(CancellationToken.None);
        api.Fail("AddBuffer", 7);
        api.Fail("Close");
        api.Return(api.Headers[0], [0, 64]);
        Assert.Equal([0, 64], await capture.Audio.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        var audioError = await Assert.ThrowsAsync<Win32Exception>(() =>
            capture.Audio.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("requeue", audioError.Message);
        var stopError = await Assert.ThrowsAsync<AggregateException>(capture.StopAsync);
        Assert.Contains(stopError.InnerExceptions, item => item.Message.Contains("requeue"));
        Assert.Contains(stopError.InnerExceptions, item => item.Message.Contains("close"));
        Assert.False(capture.IsReleased);
        await capture.StopAsync();
        Assert.True(capture.IsReleased);
    }

    [Fact]
    public async Task QueueRejectsPacket7501WithoutSilentlyDroppingAcceptedPackets()
    {
        var api = new FakeWaveInApi();
        var capture = await new WaveInCaptureFactory(api).StartAsync(CancellationToken.None);
        var header = api.Headers[0];
        for (var index = 0; index < 7500; index++)
        {
            api.Return(header, [1, 0]);
            await api.WaitForRequeueAsync();
        }
        api.Return(header, [2, 0]);
        await Assert.ThrowsAsync<AggregateException>(capture.StopAsync);
        for (var index = 0; index < 7500; index++)
        {
            Assert.Equal([1, 0], await capture.Audio.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        var error = await Assert.ThrowsAsync<IOException>(() =>
            capture.Audio.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("backlog", error.Message);
        Assert.Equal(15000 / 32000.0, capture.CapturedSeconds);
        Assert.True(capture.IsReleased);
    }

    [Fact]
    public async Task RecordingDurationIsBoundedEvenWhenTheConsumerDrainsTheQueue()
    {
        var api = new FakeWaveInApi();
        var capture = await new WaveInCaptureFactory(api).StartAsync(CancellationToken.None);
        var header = api.Headers[0];
        var packet = new byte[1280];
        for (var index = 0; index < 7500; index++)
        {
            api.Return(header, packet);
            Assert.Equal(1280, (await capture.Audio.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Length);
            await api.WaitForRequeueAsync();
        }
        api.Return(header, packet);
        var error = await Assert.ThrowsAsync<IOException>(() =>
            capture.Audio.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("five-minute", error.Message);
        Assert.Equal(300, capture.CapturedSeconds);
        await Assert.ThrowsAsync<AggregateException>(capture.StopAsync);
        Assert.True(capture.IsReleased);
    }

    [Theory]
    [InlineData(1281)]
    [InlineData(3)]
    public async Task InvalidDriverBufferSizeDoesNotReadOutsideTheAllocation(int recordedBytes)
    {
        var api = new FakeWaveInApi();
        var capture = await new WaveInCaptureFactory(api).StartAsync(CancellationToken.None);
        api.Return(api.Headers[0], [], (uint)recordedBytes);
        await Assert.ThrowsAsync<IOException>(() =>
            capture.Audio.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, capture.CapturedSeconds);
        await Assert.ThrowsAsync<AggregateException>(capture.StopAsync);
        Assert.True(capture.IsReleased);
    }
}

internal sealed class FakeWaveInApi : IWaveInApi
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void WaveCallback(nint device, uint message, nuint instance, nuint header, nuint reserved);

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _counts = [];
    private readonly Dictionary<string, HashSet<int>> _faults = [];
    private readonly List<string> _calls = [];
    private readonly List<nint> _headers = [];
    private readonly HashSet<nint> _queued = [];
    private readonly Channel<bool> _requeues = Channel.CreateUnbounded<bool>();
    private readonly ThreadLocal<bool> _inCallback = new(() => false);
    private WaveCallback? _callback;
    private bool _started;
    public nuint Instance { get; private set; }
    public WaveInFormat Format { get; private set; }
    public bool Closed { get; private set; }
    public int DriverCallsDuringCallback { get; private set; }
    public Action<string>? BeforeCall { get; set; }
    public Action<string>? AfterCall { get; set; }
    public IReadOnlyList<nint> Headers { get { lock (_gate) { return _headers.ToArray(); } } }
    public IReadOnlyList<string> Calls { get { lock (_gate) { return _calls.ToArray(); } } }

    public void Fail(string operation, int occurrence = 1)
    {
        lock (_gate)
        {
            if (!_faults.TryGetValue(operation, out var occurrences))
            {
                _faults[operation] = occurrences = [];
            }
            occurrences.Add(occurrence);
        }
    }

    private uint Call(string operation, Action? onSuccess = null)
    {
        if (_inCallback.Value)
        {
            DriverCallsDuringCallback++;
            throw new InvalidOperationException("Driver call inside waveIn callback.");
        }
        BeforeCall?.Invoke(operation);
        uint result;
        lock (_gate)
        {
            _calls.Add(operation);
            var occurrence = _counts.GetValueOrDefault(operation) + 1;
            _counts[operation] = occurrence;
            result = _faults.TryGetValue(operation, out var faults) && faults.Contains(occurrence) ? 5u : 0u;
            if (result == 0) { onSuccess?.Invoke(); }
        }
        AfterCall?.Invoke(operation);
        return result;
    }

    public uint Open(out nint device, in WaveInFormat format, nint callback, nuint instance)
    {
        Format = format;
        Instance = instance;
        _callback = Marshal.GetDelegateForFunctionPointer<WaveCallback>(callback);
        var result = Call("Open");
        device = result == 0 ? (nint)1234 : 0;
        return result;
    }

    public uint Prepare(nint device, nint header) => Call("Prepare", () => _headers.Add(header));

    public uint AddBuffer(nint device, nint header)
    {
        var result = Call("AddBuffer", () =>
        {
            Assert.Contains(header, _headers);
            Assert.True(_queued.Add(header), "Header cannot be queued twice.");
        });
        if (result == 0 && _started) { _requeues.Writer.TryWrite(true); }
        return result;
    }

    public uint Start(nint device) => Call("Start", () => _started = true);
    public uint Stop(nint device) => Call("Stop");
    public uint Reset(nint device)
    {
        nint[] returned = [];
        var result = Call("Reset", () =>
        {
            returned = _queued.ToArray();
            _queued.Clear();
        });
        if (result == 0)
        {
            foreach (var header in returned) { Return(header, []); }
        }
        return result;
    }

    public uint Unprepare(nint device, nint header)
    {
        var result = Call("Unprepare");
        lock (_gate)
        {
            Assert.False(Closed, "Unprepare must retain the original device until it succeeds.");
            Assert.Contains(header, _headers);
            if (result != 0) { return result; }
            if (_queued.Contains(header)) { return 33; }
            _headers.Remove(header);
            return 0;
        }
    }

    public uint Close(nint device) => Call("Close", () =>
    {
        Assert.Empty(_headers);
        Assert.False(Closed);
        Closed = true;
    });

    public void Return(nint header, byte[] bytes, uint? reportedBytes = null)
    {
        lock (_gate)
        {
            Assert.Contains(header, _headers);
            _queued.Remove(header);
            var value = Marshal.PtrToStructure<WaveInHeader>(header);
            Marshal.Copy(bytes, 0, value.Data, bytes.Length);
            value.BytesRecorded = reportedBytes ?? (uint)bytes.Length;
            Marshal.StructureToPtr(value, header, false);
        }
        InvokeCallback(header);
    }

    public void ReturnLateCallback() => InvokeCallback((nint)5678);

    private void InvokeCallback(nint header)
    {
        Assert.False(Closed);
        _inCallback.Value = true;
        try { _callback!(1234, 0x3C0, Instance, (nuint)header, 0); }
        finally { _inCallback.Value = false; }
    }

    public Task WaitForRequeueAsync() =>
        _requeues.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
}
