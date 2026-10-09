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

internal sealed class MeetingCapture : IAudioCapture
{
    private readonly Channel<byte[]> _audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1500)
        { SingleWriter = true, SingleReader = true, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource _stop;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    private readonly object _lifecycle = new();
    private bool _disposed;
    private long _samples;
    private int _released;
    public ChannelReader<byte[]> Audio => _audio.Reader;
    public double CapturedSeconds => Interlocked.Read(ref _samples) / 16000.0;
    public bool IsReleased => Volatile.Read(ref _released) != 0;
    public event Action<double>? LevelChanged;
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
            _stop.Token.ThrowIfCancellationRequested();
            MeetingWasapi.Start(microphone); micStarted = true;
            MeetingWasapi.Start(playback); playbackStarted = true;
            _started.TrySetResult();
            var mic = new Queue<byte>();
            var remote = new Queue<byte>();
            bool firstMicPacket = true, firstPlaybackPacket = true;
            var clock = Stopwatch.StartNew();
            long nextMilliseconds = 20;
            while (!_stop.IsCancellationRequested)
            {
                Fill(micCapture, mic, ref firstMicPacket);
                Fill(playbackCapture, remote, ref firstPlaybackPacket);
                if (clock.ElapsedMilliseconds < nextMilliseconds)
                {
                    _stop.Token.WaitHandle.WaitOne(2);
                    continue;
                }
                var packet = MeetingPcmMixer.Mix(mic, remote, 320);
                if (!_audio.Writer.TryWrite(packet)) throw new IOException("Meeting transcription is falling behind: the 30-second capture queue is full.");
                Interlocked.Add(ref _samples, 320);
                LevelChanged?.Invoke(AudioMeter.Pcm16Level(packet));
                nextMilliseconds += 20;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { _started.TrySetCanceled(_stop.Token); }
        catch (Exception error) { failure = error; _started.TrySetException(error); }
        finally
        {
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
    private static void Fill(nint capture, Queue<byte> target, ref bool firstPacket)
    {
        while (MeetingWasapi.Read(capture, ref firstPacket) is { } packet)
        {
            if (target.Count + packet.Length > 32000) throw new IOException("Meeting capture timing exceeded its one-second source buffer.");
            foreach (var value in packet) target.Enqueue(value);
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
    internal static byte[] Mix(Queue<byte> microphone, Queue<byte> playback, int samples)
    {
        var result = new byte[checked(samples * 2)];
        for (var index = 0; index < samples; index++)
        {
            int mixed = Read(microphone) + Read(playback);
            short sample = (short)Math.Clamp(mixed, short.MinValue, short.MaxValue);
            result[index * 2] = (byte)sample;
            result[index * 2 + 1] = (byte)(sample >> 8);
        }
        return result;
    }
    private static short Read(Queue<byte> source)
    {
        if (source.Count < 2) return 0;
        return (short)(source.Dequeue() | source.Dequeue() << 8);
    }
}


