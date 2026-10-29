using System.Threading.Channels;
using Ansible.Core;

namespace Ansible.Tests;

internal sealed class FakeMeetingCaptureFactory : IMeetingCaptureFactory
{
    public List<FakeMeetingCapture> Captures { get; } = [];
    public Exception? StartupFailure { get; init; }
    public Func<FakeMeetingCapture>? Create { get; init; }
    public MeetingCaptureRequest? LastRequest { get; private set; }

    public Task<IMeetingCapture> StartAsync(MeetingCaptureRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        request.Validate();
        LastRequest = request;
        if (StartupFailure is { } error) return Task.FromException<IMeetingCapture>(error);
        var capture = Create?.Invoke() ?? new FakeMeetingCapture();
        Captures.Add(capture);
        return Task.FromResult<IMeetingCapture>(capture);
    }
}

/// <summary>
/// Meeting capture fake that follows the <see cref="IMeetingCapture"/> contract: contiguous activity from sample zero,
/// raised before the packet enters <see cref="Audio"/>, and a clock that defaults to the emitted sample count.
/// </summary>
internal sealed class FakeMeetingCapture(Channel<byte[]> audio) : IMeetingCapture
{
    private readonly object _lifecycle = new();
    private long _emittedSamples;
    private long? _clock;
    private bool _released;
    private bool _blockRelease;
    private int _stopCalls;
    private Task? _stop;

    public FakeMeetingCapture() : this(Channel.CreateUnbounded<byte[]>()) { }

    public ChannelReader<byte[]> Audio => audio.Reader;
    public double CapturedSeconds => Interlocked.Read(ref _emittedSamples) / 16000.0;
    public long ClockSamplePosition
    {
        get { lock (_lifecycle) return _clock ?? Interlocked.Read(ref _emittedSamples); }
        set { lock (_lifecycle) _clock = value; }
    }
    public bool IsReleased
    {
        get { lock (_lifecycle) return _released; }
    }
    public bool BlockRelease
    {
        get { lock (_lifecycle) return _blockRelease; }
        set { lock (_lifecycle) _blockRelease = value; }
    }
    public double MicrophoneRms { get; set; }
    public double PlaybackRms { get; set; }
    public MeetingCaptureHealth? Health { get; private set; }
    public TaskCompletionSource? StopBarrier { get; set; }
    public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Action? Stopping { get; set; }
    public int StopCalls
    {
        get { lock (_lifecycle) return _stopCalls; }
    }

    public event Action<MeetingAudioActivity>? Activity;
    public event Action<MeetingCaptureHealth>? HealthChanged;
    public event Action<double>? LevelChanged { add { } remove { } }

    public void Send(int bytes) => Send(new byte[bytes]);

    public void Send(byte[] packet)
    {
        var samples = packet.Length / 2;
        var start = Interlocked.Read(ref _emittedSamples);
        Activity?.Invoke(new(start, samples, MicrophoneRms, PlaybackRms));
        Interlocked.Add(ref _emittedSamples, samples);
        if (!audio.Writer.TryWrite(packet)) throw new InvalidOperationException("The fake capture audio channel is full or complete.");
    }

    public void ReportActivity(MeetingAudioActivity activity) => Activity?.Invoke(activity);

    public void ReportHealth(MeetingCaptureHealth health)
    {
        Health = health;
        HealthChanged?.Invoke(health);
    }

    public Task StopAsync()
    {
        TaskCompletionSource completion;
        lock (_lifecycle)
        {
            // A retained capture can retry release only after the test removes its release failure.
            if (_stop is { } stop && (!stop.IsCompleted || _released || _blockRelease)) return stop;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stop = completion.Task;
            _stopCalls++;
        }
        _ = StopCoreAsync(completion);
        return completion.Task;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task StopCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            StopEntered.TrySetResult();
            Stopping?.Invoke();
            if (StopBarrier is { } barrier) await barrier.Task.ConfigureAwait(false);
            bool released;
            lock (_lifecycle) released = _released = !_blockRelease;
            audio.Writer.TryComplete(released ? null : new InvalidOperationException("Fake meeting audio resources remain owned."));
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }
}
