using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace DictationPoc.Core.Windows;

internal sealed class WaveInCapture(IWaveInApi api) : IAudioCapture
{
    internal const int PacketBytes = 1280;
    internal const int PacketCapacity = 7500;
    private const int NativeBufferCount = 6;
    private const int MaximumCapturedBytes = PacketBytes * PacketCapacity;
    private const uint DataMessage = 0x3C0;
    private readonly object _gate = new();
    private readonly Channel<nint> _returnedBuffers = Channel.CreateBounded<nint>(
        new BoundedChannelOptions(NativeBufferCount) { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Channel<byte[]> _audio = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(PacketCapacity) { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
    private readonly List<CaptureBuffer> _buffers = [];
    private GCHandle _instance;
    private nint _device;
    private bool _stopping;
    private bool _pumpJoined;
    private Task _pump = Task.CompletedTask;
    private Task? _stopAttempt;
    private int _released = 1;
    private int _activeCallbacks;
    private int _initializeStarted;
    private long _capturedBytes;

    public ChannelReader<byte[]> Audio => _audio.Reader;
    public double CapturedSeconds => Interlocked.Read(ref _capturedBytes) / 32000.0;
    public bool IsReleased => Volatile.Read(ref _released) != 0;
    public event Action<double>? LevelChanged;

    internal unsafe void Initialize(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _initializeStarted, 1) != 0)
        {
            throw new InvalidOperationException("A microphone capture cannot be restarted.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _released, 0);
        _instance = GCHandle.Alloc(this);
        var format = new WaveInFormat
        {
            FormatTag = 1, Channels = 1, SamplesPerSecond = 16000,
            AverageBytesPerSecond = 32000, BlockAlign = 2, BitsPerSample = 16
        };
        var callback = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nuint, nuint, void>)&OnWaveMessage;
        cancellationToken.ThrowIfCancellationRequested();
        Check(api.Open(out _device, in format, callback, (nuint)GCHandle.ToIntPtr(_instance)), "open the default microphone");
        for (var index = 0; index < NativeBufferCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var buffer = CaptureBuffer.Allocate();
            _buffers.Add(buffer);
            cancellationToken.ThrowIfCancellationRequested();
            Check(api.Prepare(_device, buffer.Header), "prepare microphone buffers");
            buffer.Prepared = true;
            cancellationToken.ThrowIfCancellationRequested();
            Check(api.AddBuffer(_device, buffer.Header), "queue microphone buffers");
        }
        cancellationToken.ThrowIfCancellationRequested();
        _pump = Task.Run(PumpAsync);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopping)
            {
                throw new IOException("Microphone capture failed before recording started.");
            }
            Check(api.Start(_device), "start recording");
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnWaveMessage(nint device, uint message, nuint instance, nuint header, nuint reserved)
    {
        if (message != DataMessage)
        {
            return;
        }
        var capture = (WaveInCapture)GCHandle.FromIntPtr((nint)instance).Target!;
        Interlocked.Increment(ref capture._activeCallbacks);
        try
        {
            // The callback only posts pointers. Driver calls and header reads belong to the pump.
            if (!capture._returnedBuffers.Writer.TryWrite((nint)header) &&
                !Volatile.Read(ref capture._stopping))
            {
                capture._returnedBuffers.Writer.TryComplete(
                    new IOException("The microphone returned-buffer queue overflowed."));
            }
        }
        finally
        {
            Interlocked.Decrement(ref capture._activeCallbacks);
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var pointer in _returnedBuffers.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var buffer = _buffers.Find(item => item.Header == pointer) ??
                    throw new IOException("The microphone returned an unknown buffer.");
                var header = Marshal.PtrToStructure<WaveInHeader>(buffer.Header);
                if (header.BytesRecorded > PacketBytes || header.BytesRecorded % 2 != 0)
                {
                    throw new IOException("The microphone returned invalid PCM16 buffer boundaries.");
                }
                if (header.BytesRecorded > 0)
                {
                    if (Interlocked.Read(ref _capturedBytes) + header.BytesRecorded > MaximumCapturedBytes)
                    {
                        throw new IOException("The five-minute microphone recording limit was reached.");
                    }
                    var bytes = new byte[checked((int)header.BytesRecorded)];
                    Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
                    if (!_audio.Writer.TryWrite(bytes))
                    {
                        throw new IOException("The bounded five-minute audio backlog is full. Recording stopped without dropping samples.");
                    }
                    Interlocked.Add(ref _capturedBytes, bytes.Length);
                    LevelChanged?.Invoke(AudioMeter.Pcm16Level(bytes));
                }
                lock (_gate)
                {
                    if (!_stopping)
                    {
                        Check(api.AddBuffer(_device, pointer), "requeue microphone audio");
                    }
                }
            }
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                _stopping = true;
            }
            _audio.Writer.TryComplete(error);
            throw;
        }
        finally
        {
            _audio.Writer.TryComplete();
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopAttempt is { IsCompleted: false })
            {
                return _stopAttempt;
            }
            if (IsReleased)
            {
                return Task.CompletedTask;
            }
            _stopping = true;
            return _stopAttempt = Task.Run(StopCoreAsync);
        }
    }

    private async Task StopCoreAsync()
    {
        List<Exception> failures = [];
        if (_device != 0)
        {
            Attempt(() => api.Stop(_device), "stop recording", failures);
            Attempt(() => api.Reset(_device), "return microphone buffers", failures);
        }
        // Late callbacks can only fail to post. No header is freed until the pump has joined.
        _returnedBuffers.Writer.TryComplete();
        if (!_pumpJoined)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
            _pumpJoined = true;
        }
        foreach (var buffer in _buffers.ToArray())
        {
            if (buffer.Prepared && !Attempt(() => api.Unprepare(_device, buffer.Header),
                "release a microphone buffer still owned by the driver", failures))
            {
                continue;
            }
            buffer.Free();
            _buffers.Remove(buffer);
        }
        // Keep the device usable for unprepare retries instead of closing beneath retained headers.
        if (_buffers.Count == 0 && _device != 0 &&
            Attempt(() => api.Close(_device), "close the microphone device", failures))
        {
            _device = 0;
        }
        if (_buffers.Count == 0 && _device == 0)
        {
            while (Volatile.Read(ref _activeCallbacks) != 0)
            {
                await Task.Yield();
            }
            if (_instance.IsAllocated)
            {
                _instance.Free();
            }
            Volatile.Write(ref _released, 1);
        }
        if (failures.Count > 0)
        {
            var error = new AggregateException("Microphone stop or cleanup failed.", failures);
            _audio.Writer.TryComplete(error);
            throw error;
        }
        _audio.Writer.TryComplete();
    }

    private static bool Attempt(Func<uint> action, string operation, List<Exception> failures)
    {
        try
        {
            Check(action(), operation);
            return true;
        }
        catch (Exception error)
        {
            failures.Add(error);
            return false;
        }
    }

    private static void Check(uint result, string operation)
    {
        if (result != 0)
        {
            throw new Win32Exception((int)result, $"Cannot {operation} (waveIn error {result}).");
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private sealed class CaptureBuffer(nint header, nint data)
    {
        public nint Header { get; } = header;
        public nint Data { get; } = data;
        public bool Prepared { get; set; }

        public static CaptureBuffer Allocate()
        {
            var header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveInHeader>());
            nint data = 0;
            try
            {
                data = Marshal.AllocHGlobal(PacketBytes);
                Marshal.StructureToPtr(new WaveInHeader { Data = data, BufferLength = PacketBytes }, header, false);
                return new CaptureBuffer(header, data);
            }
            catch
            {
                if (data != 0) { Marshal.FreeHGlobal(data); }
                Marshal.FreeHGlobal(header);
                throw;
            }
        }

        public void Free()
        {
            Marshal.FreeHGlobal(Header);
            Marshal.FreeHGlobal(Data);
        }
    }
}
