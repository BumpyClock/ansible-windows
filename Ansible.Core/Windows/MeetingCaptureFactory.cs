using System.Diagnostics;
using System.Threading.Channels;

namespace Ansible.Core.Windows;

public sealed class MeetingCaptureFactory : IMeetingCaptureFactory
{
    public async Task<IAudioCapture> StartAsync(MeetingCaptureRequest request, CancellationToken cancellationToken)
    {
        request.Validate();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Meeting capture requires Windows.");
        var capture = new MeetingCapture(request, cancellationToken);
        try { await capture.Started.ConfigureAwait(false); return capture; }
        catch { await capture.StopAsync().ConfigureAwait(false); throw; }
    }
}

internal sealed class MeetingCapture : IAudioCapture, IMeetingAudioSourceMetadata
{
    private readonly Channel<byte[]> _audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(750)
        { SingleWriter = true, SingleReader = true, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource _stop;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    private readonly object _lifecycle = new();
    private bool _disposed;
    private long _samples;
    private long _origin100Nanoseconds;
    private long _stoppedClockPosition = -1;
    private int _released;
    public ChannelReader<byte[]> Audio => _audio.Reader;
    public double CapturedSeconds => Interlocked.Read(ref _samples) / 16000.0;
    public long ClockSamplePosition
    {
        get
        {
            var stopped = Interlocked.Read(ref _stoppedClockPosition);
            if (stopped >= 0) return stopped;
            var origin = Interlocked.Read(ref _origin100Nanoseconds);
            return origin == 0 ? 0 : Math.Max(0, MeetingCaptureClock.Samples(MeetingCaptureClock.Now100Nanoseconds, origin));
        }
    }
    public bool IsReleased => Volatile.Read(ref _released) != 0;
    public event Action<double>? LevelChanged;
    public event Action<MeetingAudioActivity>? Activity;
    internal Task Started => _started.Task;

    internal MeetingCapture(MeetingCaptureRequest request, CancellationToken token)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _worker = Task.Factory.StartNew(() => Run(request), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    private void Run(MeetingCaptureRequest request)
    {
        nint microphone = 0, playback = 0, micCapture = 0, playbackCapture = 0;
        bool comInitialized = false, micStarted = false, playbackStarted = false;
        Exception? failure = null;
        try
        {
            _stop.Token.ThrowIfCancellationRequested();
            MeetingWasapi.Check(MeetingWasapi.CoInitializeEx(0, 0));
            comInitialized = true;
            microphone = MeetingWasapi.OpenDefault(1);
            playback = request.PlaybackSource == MeetingPlaybackSource.MeetingApp
                ? MeetingProcessActivation.Open(request.ProcessId!.Value) : MeetingWasapi.OpenDefault(0);
            MeetingWasapi.Initialize(microphone, false);
            MeetingWasapi.Initialize(playback, true);
            micCapture = MeetingWasapi.CaptureService(microphone);
            playbackCapture = MeetingWasapi.CaptureService(playback);
            var mic = new MeetingPacketTimeline();
            var remote = new MeetingPacketTimeline();
            var micClock = new MeetingPacketClock();
            var playbackClock = new MeetingPacketClock();
            _stop.Token.ThrowIfCancellationRequested();
            var origin = MeetingCaptureClock.Now100Nanoseconds;
            Interlocked.Exchange(ref _origin100Nanoseconds, origin);
            MeetingWasapi.Start(microphone); micStarted = true;
            MeetingWasapi.Start(playback); playbackStarted = true;
            _started.TrySetResult();
            bool firstMicPacket = true, firstPlaybackPacket = true;
            while (!_stop.IsCancellationRequested)
            {
                var clockPosition = ClockSamplePosition;
                Fill(micCapture, mic, micClock, origin, clockPosition, ref firstMicPacket);
                Fill(playbackCapture, remote, playbackClock, origin, clockPosition, ref firstPlaybackPacket);
                var availableThrough = clockPosition - 4000;
                if (mic.Cursor + 640 > availableThrough)
                {
                    _stop.Token.WaitHandle.WaitOne(2);
                    continue;
                }
                Emit(mic, remote, 640);
            }
            var stopPosition = ClockSamplePosition;
            Interlocked.Exchange(ref _stoppedClockPosition, stopPosition);
            MeetingWasapi.Stop(playback); playbackStarted = false;
            MeetingWasapi.Stop(microphone); micStarted = false;
            Fill(micCapture, mic, micClock, origin, stopPosition, ref firstMicPacket);
            Fill(playbackCapture, remote, playbackClock, origin, stopPosition, ref firstPlaybackPacket);
            while (mic.Cursor < stopPosition)
                Emit(mic, remote, (int)Math.Min(640, stopPosition - mic.Cursor));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { _started.TrySetCanceled(_stop.Token); }
        catch (Exception error) { failure = error; _started.TrySetException(error); }
        finally
        {
            Interlocked.CompareExchange(ref _stoppedClockPosition, ClockSamplePosition, -1);
            try { if (playbackStarted) MeetingWasapi.Stop(playback); }
            catch (Exception error) { failure = Combine(failure, error); }
            try { if (micStarted) MeetingWasapi.Stop(microphone); }
            catch (Exception error) { failure = Combine(failure, error); }
            MeetingWasapi.Release(playbackCapture); MeetingWasapi.Release(micCapture);
            MeetingWasapi.Release(playback); MeetingWasapi.Release(microphone);
            if (comInitialized) MeetingWasapi.CoUninitialize();
            Volatile.Write(ref _released, 1);
            _audio.Writer.TryComplete(failure);
        }
    }
    private static Exception Combine(Exception? first, Exception next) => first is null ? next : new AggregateException(first, next);
    private void Emit(MeetingPacketTimeline microphone, MeetingPacketTimeline playback, int samples)
    {
        var start = microphone.Cursor;
        var packet = MeetingPcmMixer.Mix(microphone.Read(samples), playback.Read(samples), out var micRms, out var playbackRms);
        Activity?.Invoke(new(start, samples, micRms, playbackRms));
        if (!_audio.Writer.TryWrite(packet))
            throw new IOException("Meeting transcription is falling behind: the 30-second capture queue is full.");
        Interlocked.Add(ref _samples, samples);
        LevelChanged?.Invoke(AudioMeter.Pcm16Level(packet));
    }
    private static void Fill(nint capture, MeetingPacketTimeline target, MeetingPacketClock sourceClock,
        long origin, long clockPosition, ref bool firstPacket)
    {
        while (MeetingWasapi.Read(capture, ref firstPacket) is { } packet)
        {
            var start = MeetingCaptureClock.Samples((long)packet.QpcPosition100Nanoseconds, origin);
            target.Add(sourceClock.Map(start, packet.DevicePosition, packet.Pcm.Length / 2), packet.Pcm, clockPosition);
        }
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
}

internal static class MeetingPcmMixer
{
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
