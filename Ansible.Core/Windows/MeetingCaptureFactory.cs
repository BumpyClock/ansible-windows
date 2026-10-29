using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Ansible.Core.Windows;

public sealed class MeetingCaptureFactory : IMeetingCaptureFactory
{
    private readonly LocalDiagnosticLog? _diagnostics;
    private readonly IMeetingAudioDevices _devices;
    private readonly Func<long> _now100Nanoseconds;
    private readonly TimeSpan _outputScanInterval;

    public MeetingCaptureFactory(LocalDiagnosticLog? diagnostics = null)
        : this(diagnostics, WasapiMeetingAudioDevices.Instance, static () => MeetingCaptureClock.Now100Nanoseconds, MeetingCapture.DefaultOutputScanInterval) { }

    /// <summary>Test seam: replaces WASAPI devices, the 100 ns capture clock, and the background output scan interval.</summary>
    internal MeetingCaptureFactory(LocalDiagnosticLog? diagnostics, IMeetingAudioDevices devices,
        Func<long> now100Nanoseconds, TimeSpan outputScanInterval)
    {
        _diagnostics = diagnostics;
        _devices = devices;
        _now100Nanoseconds = now100Nanoseconds;
        _outputScanInterval = outputScanInterval;
    }

    public async Task<IMeetingCapture> StartAsync(MeetingCaptureRequest request, CancellationToken cancellationToken)
    {
        request.Validate();
        var capture = new MeetingCapture(request, cancellationToken, _diagnostics, _devices, _now100Nanoseconds, _outputScanInterval);
        try { await capture.Started.ConfigureAwait(false); return capture; }
        catch (Exception error)
        {
            Exception? cleanup = null;
            try { await capture.DisposeAsync().ConfigureAwait(false); }
            catch (Exception disposeError) { cleanup = disposeError; }
            if (!capture.IsReleased)
                throw new MeetingCaptureOwnershipException("Meeting audio resources remain owned after startup failed.", capture,
                    cleanup is null ? error : new AggregateException(error, cleanup));
            throw;
        }
    }
}

internal sealed class MeetingCapture : IMeetingCapture
{
    internal static readonly TimeSpan DefaultOutputScanInterval = TimeSpan.FromSeconds(2);
    private readonly LocalDiagnosticLog? _diagnostics;
    private readonly MeetingCaptureMetrics _metrics;
    private readonly IMeetingAudioDevices _devices;
    private readonly Func<long> _now100Nanoseconds;
    private readonly TimeSpan _outputScanInterval;
    private readonly Guid _captureId = Guid.NewGuid();
    private long _elapsedStart100Nanoseconds;
    private double _nextHealthSeconds = 1, _nextLogSeconds = 5;
    private MeetingCaptureStage _stage = MeetingCaptureStage.InitializeCom;
    private MeetingCaptureHealth? _health;
    private readonly Channel<byte[]> _audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(750)
        { SingleWriter = true, SingleReader = true, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource _stop;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    private readonly object _lifecycle = new();
    private bool _disposed;
    private long _samples;
    private long _origin100Nanoseconds = long.MinValue;
    private long _stoppedClockPosition = -1;
    private int _released;
    private readonly ConcurrentQueue<Exception> _cleanupFailures = new();
    // Failed releases stay owned, but cannot be retried after their COM thread exits.
    private readonly ConcurrentQueue<IDisposable> _retainedResources = new();
    private readonly List<OutputEndpoint> _outputs = [];
    private readonly HashSet<string> _skippedOutputs = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<OutputScan> _scans = new();
    private readonly ConcurrentDictionary<string, byte> _claimedOutputs = new(StringComparer.Ordinal);
    private long _outputsLost;
    private bool _enumerationFailed;
    public ChannelReader<byte[]> Audio => _audio.Reader;
    public double CapturedSeconds => Interlocked.Read(ref _samples) / 16000.0;
    public long ClockSamplePosition
    {
        get
        {
            var stopped = Interlocked.Read(ref _stoppedClockPosition);
            if (stopped >= 0) return stopped;
            var origin = Interlocked.Read(ref _origin100Nanoseconds);
            return origin == long.MinValue ? 0 : Math.Max(0, MeetingCaptureClock.Samples(_now100Nanoseconds(), origin));
        }
    }
    public bool IsReleased => Volatile.Read(ref _released) != 0;
    public event Action<double>? LevelChanged;
    public event Action<MeetingAudioActivity>? Activity;
    public event Action<MeetingCaptureHealth>? HealthChanged;
    public MeetingCaptureHealth? Health => Volatile.Read(ref _health);
    internal Task Started => _started.Task;

    internal MeetingCapture(MeetingCaptureRequest request, CancellationToken token, LocalDiagnosticLog? diagnostics,
        IMeetingAudioDevices devices, Func<long> now100Nanoseconds, TimeSpan outputScanInterval)
    {
        _diagnostics = diagnostics;
        _metrics = new(request);
        _devices = devices;
        _now100Nanoseconds = now100Nanoseconds;
        _outputScanInterval = outputScanInterval;
        _elapsedStart100Nanoseconds = now100Nanoseconds();
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _worker = Task.Factory.StartNew(Run, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    private double ElapsedSeconds
    {
        get
        {
            var stopped = Interlocked.Read(ref _stoppedClockPosition);
            return stopped >= 0 ? stopped / 16000.0
                : (_now100Nanoseconds() - _elapsedStart100Nanoseconds) / 10_000_000.0;
        }
    }
    private void Run()
    {
        IDisposable? apartment = null;
        IMeetingAudioSource? microphone = null;
        IMeetingOutputCatalog? catalog = null;
        var micStarted = false;
        var failures = new List<Exception>();
        MeetingCaptureStage? failureStage = null;
        using var scanStop = new CancellationTokenSource();
        Thread? scanner = null;
        try
        {
            PublishHealth(MeetingCaptureDiagnosticKind.Starting);
            _stop.Token.ThrowIfCancellationRequested();
            apartment = _devices.EnterThread();
            microphone = _devices.OpenMicrophone(stage => _stage = stage);
            _stage = MeetingCaptureStage.EnumerateOutputs;
            catalog = _devices.OpenOutputCatalog();
            // Opening an output endpoint can take hundreds of milliseconds, so the first scan runs before the microphone starts.
            ScanOutputs(catalog);
            var mic = new MeetingPacketTimeline();
            var micClock = new MeetingPacketClock();
            _stop.Token.ThrowIfCancellationRequested();
            var origin = _now100Nanoseconds();
            _elapsedStart100Nanoseconds = origin;
            Interlocked.Exchange(ref _origin100Nanoseconds, origin);
            _stage = MeetingCaptureStage.StartMicrophone;
            microphone.Start(); micStarted = true;
            AdoptOutputs(mic.Cursor);
            var scanThread = new Thread(() => ScanOutputsUntil(scanStop.Token)) { IsBackground = true, Name = "Meeting output scan" };
            scanThread.Start();
            scanner = scanThread;
            PublishHealth(MeetingCaptureDiagnosticKind.Recording);
            _started.TrySetResult();
            while (!_stop.IsCancellationRequested)
            {
                var clockPosition = ClockSamplePosition;
                Fill(microphone, mic, micClock, origin, clockPosition, true);
                FillOutputs(origin, clockPosition);
                AdoptOutputs(mic.Cursor);
                if (ElapsedSeconds >= _nextHealthSeconds)
                {
                    var log = ElapsedSeconds >= _nextLogSeconds;
                    PublishHealth(log ? MeetingCaptureDiagnosticKind.Sample : null);
                    _nextHealthSeconds = ElapsedSeconds + 1;
                    if (log) _nextLogSeconds = ElapsedSeconds + 5;
                }
                var availableThrough = clockPosition - 4000;
                if (mic.Cursor + 640 > availableThrough)
                {
                    _stop.Token.WaitHandle.WaitOne(2);
                    continue;
                }
                Emit(mic, 640);
            }
            var stopPosition = ClockSamplePosition;
            Interlocked.Exchange(ref _stoppedClockPosition, stopPosition);
            _stage = MeetingCaptureStage.Stop;
            for (var index = _outputs.Count - 1; index >= 0; index--)
            {
                var output = _outputs[index];
                output.Started = false;
                try { output.Source.Stop(); }
                catch (Exception error) when (IsOutputFailure(error)) { DropOutput(output, error.HResult); }
            }
            micStarted = false;
            microphone.Stop();
            var drainClock = MeetingCaptureClock.Samples(_now100Nanoseconds(), origin);
            Fill(microphone, mic, micClock, origin, drainClock, true);
            FillOutputs(origin, drainClock);
            while (mic.Cursor < stopPosition)
                Emit(mic, (int)Math.Min(640, stopPosition - mic.Cursor));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested && !_started.Task.IsCompleted)
        {
            _started.TrySetCanceled(_stop.Token);
        }
        catch (Exception error)
        {
            failures.Add(error);
            failureStage = _stage;
            _started.TrySetException(error);
        }
        finally
        {
            try { Interlocked.CompareExchange(ref _stoppedClockPosition, ClockSamplePosition, -1); }
            catch (Exception error)
            {
                failures.Add(error);
                Interlocked.CompareExchange(ref _stoppedClockPosition, Interlocked.Read(ref _samples), -1);
            }
            scanStop.Cancel();
            scanner?.Join();
            while (_scans.TryDequeue(out var scan))
            {
                if (scan is OutputPrepared prepared) Release(prepared.Source);
                else if (scan is OutputScannerFailed stopped) failures.Add(stopped.Error);
            }
            foreach (var output in _outputs) ReleaseOutput(output);
            _outputs.Clear();
            if (micStarted)
            {
                try { microphone!.Stop(); }
                catch (Exception error) { failures.Add(error); }
            }
            if (microphone is not null) Release(microphone);
            if (catalog is not null) Release(catalog);
            if (apartment is not null) Release(apartment);
            while (_cleanupFailures.TryDequeue(out var error)) failures.Add(error);
            if (_retainedResources.IsEmpty) Volatile.Write(ref _released, 1);
            if (failures.Count > 0)
            {
                _stage = failureStage ?? MeetingCaptureStage.Stop;
                if (TryPublishHealth(MeetingCaptureDiagnosticKind.Failed, failures[0].HResult) is { } publishError)
                    failures.Add(publishError);
            }
            _stage = MeetingCaptureStage.Stop;
            if (TryPublishHealth(MeetingCaptureDiagnosticKind.Stopped) is { } stoppedError) failures.Add(stoppedError);
            _audio.Writer.TryComplete(IsReleased ? failures.Count switch
            {
                0 => null,
                1 => failures[0],
                _ => new AggregateException(failures)
            } : new AggregateException("Meeting audio resources remain owned after release failed.", failures));
        }
    }
    private static bool IsOutputFailure(Exception error) => error is not (OutOfMemoryException or OperationCanceledException);
    private Exception? TryPublishHealth(MeetingCaptureDiagnosticKind kind, int? errorHResult = null)
    {
        try { PublishHealth(kind, errorHResult); return null; }
        catch (Exception error) { return error; }
    }
    private void Release(IDisposable resource)
    {
        try { resource.Dispose(); }
        catch (Exception error)
        {
            _retainedResources.Enqueue(resource);
            _cleanupFailures.Enqueue(error);
        }
    }

    // Output devices come and go during a meeting (headsets, docks, Bluetooth). A failed or missing
    // endpoint is dropped and logged; the meeting continues with the microphone and remaining outputs.
    // Later scans run on their own thread and hand prepared endpoints to the capture thread, which only starts them.
    private void ScanOutputsUntil(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            IDisposable? apartment = null;
            IMeetingOutputCatalog? catalog = null;
            try
            {
                apartment = _devices.EnterThread();
                catalog = _devices.OpenOutputCatalog();
                while (!token.WaitHandle.WaitOne(_outputScanInterval)) ScanOutputs(catalog);
                return;
            }
            catch (Exception error) when (IsOutputFailure(error)) { _scans.Enqueue(new OutputsUnavailable(error.HResult)); }
            catch (Exception error)
            {
                _scans.Enqueue(new OutputScannerFailed(error));
                return;
            }
            finally
            {
                if (catalog is not null) Release(catalog);
                if (apartment is not null) Release(apartment);
            }
            if (token.WaitHandle.WaitOne(_outputScanInterval)) return;
        }
    }
    private void ScanOutputs(IMeetingOutputCatalog catalog)
    {
        IReadOnlyList<string> ids;
        try { ids = catalog.ActiveIds(); }
        catch (Exception error) when (IsOutputFailure(error))
        {
            _scans.Enqueue(new OutputsUnavailable(error.HResult));
            return;
        }
        _scans.Enqueue(new OutputsActive(new(ids, StringComparer.Ordinal)));
        foreach (var id in ids)
        {
            if (!_claimedOutputs.TryAdd(id, 0)) continue;
            try
            {
                var result = PrepareOutput(catalog, id);
                if (result is OutputRejected) _claimedOutputs.TryRemove(id, out _);
                _scans.Enqueue(result);
            }
            catch
            {
                _claimedOutputs.TryRemove(id, out _);
                throw;
            }
        }
    }
    private static OutputScan PrepareOutput(IMeetingOutputCatalog catalog, string id)
    {
        var stage = MeetingCaptureStage.OpenPlayback;
        try { return new OutputPrepared(id, catalog.OpenLoopback(id, value => stage = value)); }
        catch (Exception error) when (IsOutputFailure(error))
        {
            // Exclusive-mode or in-use devices cannot be loopback captured. Later scans retry them.
            return new OutputRejected(id, stage, error.HResult);
        }
    }
    private void AdoptOutputs(long cursor)
    {
        while (_scans.TryDequeue(out var scan))
        {
            switch (scan)
            {
                case OutputScannerFailed stopped:
                    _stage = MeetingCaptureStage.EnumerateOutputs;
                    throw new IOException("Meeting output scanning stopped.", stopped.Error);
                case OutputsUnavailable unavailable:
                    _stage = MeetingCaptureStage.EnumerateOutputs;
                    if (!_enumerationFailed) PublishHealth(MeetingCaptureDiagnosticKind.OutputSkipped, unavailable.HResult);
                    _enumerationFailed = true;
                    break;
                case OutputsActive active:
                    _enumerationFailed = false;
                    for (var index = _outputs.Count - 1; index >= 0; index--)
                        if (!active.Ids.Contains(_outputs[index].Id)) DropOutput(_outputs[index], null);
                    _skippedOutputs.IntersectWith(active.Ids);
                    break;
                case OutputRejected rejected:
                    _stage = rejected.Stage;
                    if (_skippedOutputs.Add(rejected.Id)) PublishHealth(MeetingCaptureDiagnosticKind.OutputSkipped, rejected.HResult);
                    break;
                case OutputPrepared prepared:
                    StartOutput(prepared, cursor);
                    break;
            }
        }
    }
    private void StartOutput(OutputPrepared prepared, long cursor)
    {
        var output = new OutputEndpoint(prepared.Id, prepared.Source, cursor);
        try
        {
            _stage = MeetingCaptureStage.StartPlayback;
            output.Source.Start();
            output.Started = true;
        }
        catch (Exception error)
        {
            ReleaseOutput(output);
            _claimedOutputs.TryRemove(output.Id, out _);
            if (!IsOutputFailure(error)) throw;
            if (_skippedOutputs.Add(output.Id)) PublishHealth(MeetingCaptureDiagnosticKind.OutputSkipped, error.HResult);
            return;
        }
        _skippedOutputs.Remove(output.Id);
        _outputs.Add(output);
        PublishHealth(MeetingCaptureDiagnosticKind.OutputAdded);
    }
    private void DropOutput(OutputEndpoint output, int? errorHResult)
    {
        _outputs.Remove(output);
        ReleaseOutput(output);
        _claimedOutputs.TryRemove(output.Id, out _);
        _outputsLost++;
        PublishHealth(MeetingCaptureDiagnosticKind.OutputLost, errorHResult);
    }
    private void ReleaseOutput(OutputEndpoint output)
    {
        var started = output.Started;
        output.Started = false;
        try { if (started) output.Source.Stop(); }
        catch (Exception error) { _cleanupFailures.Enqueue(error); }
        Release(output.Source);
    }
    private void FillOutputs(long origin, long clockPosition)
    {
        for (var index = _outputs.Count - 1; index >= 0; index--)
        {
            var output = _outputs[index];
            try { Fill(output.Source, output.Timeline, output.Clock, origin, clockPosition, false); }
            catch (Exception error) when (IsOutputFailure(error)) { DropOutput(output, error.HResult); }
        }
    }
    private void Emit(MeetingPacketTimeline microphone, int samples)
    {
        _stage = MeetingCaptureStage.Mix;
        var start = microphone.Cursor;
        var playbackSamples = 0;
        var playback = new byte[samples * 2];
        foreach (var output in _outputs)
        {
            MeetingPcmMixer.Accumulate(playback, output.Timeline.Read(samples, out var captured));
            playbackSamples = Math.Max(playbackSamples, captured);
        }
        var packet = MeetingPcmMixer.Mix(microphone.Read(samples, out var micSamples),
            playback, out var micRms, out var playbackRms);
        _metrics.ObserveMix(samples, micSamples, playbackSamples, micRms, playbackRms);
        Activity?.Invoke(new(start, samples, micRms, playbackRms));
        if (!_audio.Writer.TryWrite(packet))
            throw new IOException("Meeting transcription is falling behind: the 30-second capture queue is full.");
        Interlocked.Add(ref _samples, samples);
        LevelChanged?.Invoke(AudioMeter.Pcm16Level(packet));
    }
    private void Fill(IMeetingAudioSource source, MeetingPacketTimeline target, MeetingPacketClock sourceClock,
        long origin, long clockPosition, bool microphone)
    {
        _stage = microphone ? MeetingCaptureStage.ReadMicrophone : MeetingCaptureStage.ReadPlayback;
        while (source.Read() is { } packet)
        {
            // WASAPI may flag the first packet after Start as discontinuous; any later gap means audio was lost.
            if ((packet.Flags & MeetingCapturePacket.Discontinuity) != 0 && sourceClock.ExpectedSample is not null)
                throw new IOException("Meeting audio capture reported a discontinuity. Audio may have been lost.");
            if ((packet.Flags & MeetingCapturePacket.TimestampError) != 0
                || packet.QpcPosition100Nanoseconds is 0 or > long.MaxValue)
                throw new IOException("Meeting capture reported an invalid audio timestamp.");
            var start = MeetingCaptureClock.Samples((long)packet.QpcPosition100Nanoseconds, origin);
            _metrics.ObservePacket(microphone, packet, ElapsedSeconds,
                new(start, sourceClock.ExpectedSample, packet.Pcm.Length / 2, packet.DevicePosition,
                    sourceClock.PreviousDevicePosition, packet.Flags));
            target.Add(sourceClock.Map(start, packet.DevicePosition, packet.Pcm.Length / 2), packet.Pcm, clockPosition);
        }
    }
    private void PublishHealth(MeetingCaptureDiagnosticKind? kind, int? errorHResult = null)
    {
        var health = _metrics.Snapshot(ElapsedSeconds, _diagnostics?.Error,
            _outputs.Count, _skippedOutputs.Count, _outputsLost) with { CaptureId = _captureId };
        Volatile.Write(ref _health, health);
        if (kind is { } value)
            _diagnostics?.Write(new(_captureId, DateTimeOffset.UtcNow, value, _stage, health, errorHResult));
        HealthChanged?.Invoke(health);
    }
    public async Task StopAsync()
    {
        lock (_lifecycle) { if (!_disposed) _stop.Cancel(); }
        await _worker.ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_lifecycle)
        {
            if (_disposed) return;
            _stop.Dispose();
            _disposed = true;
        }
    }

    private sealed class OutputEndpoint(string id, IMeetingAudioSource source, long cursor)
    {
        internal readonly string Id = id;
        internal readonly IMeetingAudioSource Source = source;
        internal readonly MeetingPacketTimeline Timeline = new(cursor);
        internal readonly MeetingPacketClock Clock = new();
        internal bool Started;
    }
    private abstract record OutputScan;
    private sealed record OutputsActive(HashSet<string> Ids) : OutputScan;
    private sealed record OutputsUnavailable(int HResult) : OutputScan;
    private sealed record OutputScannerFailed(Exception Error) : OutputScan;
    private sealed record OutputPrepared(string Id, IMeetingAudioSource Source) : OutputScan;
    private sealed record OutputRejected(string Id, MeetingCaptureStage Stage, int HResult) : OutputScan;
}

internal static class MeetingPcmMixer
{
    internal static void Accumulate(Span<byte> total, ReadOnlySpan<byte> source)
    {
        if (total.Length != source.Length || total.Length % 2 != 0)
            throw new ArgumentException("Meeting output inputs must have matching PCM16 frame lengths.");
        for (var index = 0; index < total.Length; index += 2)
        {
            var current = (short)(total[index] | total[index + 1] << 8);
            var added = (short)(source[index] | source[index + 1] << 8);
            var sum = (short)Math.Clamp(current + added, short.MinValue, short.MaxValue);
            total[index] = (byte)sum;
            total[index + 1] = (byte)(sum >> 8);
        }
    }

    internal static byte[] Mix(ReadOnlySpan<byte> microphone, ReadOnlySpan<byte> playback,
        out double microphoneRms, out double playbackRms)
    {
        if (microphone.Length != playback.Length || microphone.Length % 2 != 0)
            throw new ArgumentException("Meeting mix inputs must have matching PCM16 frame lengths.");
        var result = new byte[microphone.Length];
        double micPower = 0, playbackPower = 0;
        for (var index = 0; index < result.Length; index += 2)
        {
            var mic = (short)(microphone[index] | microphone[index + 1] << 8);
            var remote = (short)(playback[index] | playback[index + 1] << 8);
            micPower += (double)mic * mic;
            playbackPower += (double)remote * remote;
            var mixed = (short)Math.Clamp(mic + remote, short.MinValue, short.MaxValue);
            result[index] = (byte)mixed;
            result[index + 1] = (byte)(mixed >> 8);
        }
        microphoneRms = result.Length == 0 ? 0 : Math.Sqrt(micPower / (result.Length / 2)) / 32768;
        playbackRms = result.Length == 0 ? 0 : Math.Sqrt(playbackPower / (result.Length / 2)) / 32768;
        return result;
    }
}
