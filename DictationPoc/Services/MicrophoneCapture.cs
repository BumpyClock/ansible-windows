using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using DictationPoc.Core;

namespace DictationPoc.Services;

internal sealed partial class MicrophoneCapture : IAsyncDisposable
{
    private const uint DataMessage = 0x3C0;
    private const int BufferSize = 1280;
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();
    private readonly object _gate = new();
    private readonly Channel<nint> _returnedBuffers = Channel.CreateUnbounded<nint>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Channel<byte[]> _audio = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(7500) { SingleReader = true, SingleWriter = true });
    private readonly List<CaptureBuffer> _buffers = [];
    private GCHandle _instance;
    private nint _device;
    private bool _stopping;
    private Task _pump = Task.CompletedTask;
    private Task? _stopTask;
    private long _capturedBytes;

    public ChannelReader<byte[]> Audio => _audio.Reader;
    public double CapturedSeconds => Interlocked.Read(ref _capturedBytes) / 32000.0;
    public event Action<double>? LevelChanged;

    public static async Task<MicrophoneCapture> StartAsync()
    {
        var capture = new MicrophoneCapture();
        try
        {
            capture.Start();
            return capture;
        }
        catch (Exception startError)
        {
            try
            {
                await capture.DisposeAsync();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Microphone startup and cleanup failed.", startError, cleanupError);
            }
            throw;
        }
    }

    private unsafe void Start()
    {
        _instance = GCHandle.Alloc(this);
        var format = new WaveFormat
        {
            FormatTag = 1,
            Channels = 1,
            SamplesPerSecond = 16000,
            AverageBytesPerSecond = 32000,
            BlockAlign = 2,
            BitsPerSample = 16
        };
        var callback = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nuint, nuint, void>)&OnWaveMessage;
        Check(WaveInOpen(out _device, uint.MaxValue, in format, callback,
            (nuint)GCHandle.ToIntPtr(_instance), 0x30000), "open the default microphone");
        for (var index = 0; index < 6; index++)
        {
            var buffer = new CaptureBuffer(
                Marshal.AllocHGlobal(sizeof(WaveHeader)),
                Marshal.AllocHGlobal(BufferSize));
            _buffers.Add(buffer);
            *(WaveHeader*)buffer.Header = new WaveHeader { Data = buffer.Data, BufferLength = BufferSize };
            Check(WaveInPrepareHeader(_device, buffer.Header, (uint)sizeof(WaveHeader)), "prepare microphone buffers");
            buffer.Prepared = true;
            Check(WaveInAddBuffer(_device, buffer.Header, (uint)sizeof(WaveHeader)), "queue microphone buffers");
        }
        _pump = Task.Run(PumpAsync);
        Check(WaveInStart(_device), "start recording");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnWaveMessage(nint device, uint message, nuint instance, nuint header, nuint reserved)
    {
        if (message != DataMessage)
        {
            return;
        }
        var capture = (MicrophoneCapture)GCHandle.FromIntPtr((nint)instance).Target!;
        // Requeue outside the driver callback; waveIn calls here can deadlock.
        capture._returnedBuffers.Writer.TryWrite((nint)header);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var pointer in _returnedBuffers.Reader.ReadAllAsync())
            {
                var header = Marshal.PtrToStructure<WaveHeader>(pointer);
                if (header.BytesRecorded > 0)
                {
                    var bytes = new byte[checked((int)header.BytesRecorded)];
                    Marshal.Copy(header.Data, bytes, 0, bytes.Length);
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
                        Check(WaveInAddBuffer(_device, pointer, HeaderSize), "requeue microphone audio");
                    }
                }
            }
        }
        catch (Exception error)
        {
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
            return _stopTask ??= Task.Run(StopCoreAsync);
        }
    }

    private async Task StopCoreAsync()
    {
        try
        {
            lock (_gate)
            {
                _stopping = true;
            }
            if (_device != 0)
            {
                Check(WaveInStop(_device), "stop recording");
                Check(WaveInReset(_device), "return microphone buffers");
            }
        }
        finally
        {
            _returnedBuffers.Writer.TryComplete();
            try
            {
                await _pump;
            }
            finally
            {
                ReleaseNativeResources();
                _audio.Writer.TryComplete();
            }
        }
    }

    private void ReleaseNativeResources()
    {
        List<Exception> failures = [];
        foreach (var buffer in _buffers)
        {
            var result = buffer.Prepared
                ? WaveInUnprepareHeader(_device, buffer.Header, HeaderSize)
                : 0;
            if (result != 0)
            {
                failures.Add(new Win32Exception((int)result, "Cannot release a microphone buffer still owned by the driver."));
                continue;
            }
            Marshal.FreeHGlobal(buffer.Header);
            Marshal.FreeHGlobal(buffer.Data);
        }
        _buffers.Clear();
        var closeResult = _device != 0 ? WaveInClose(_device) : 0;
        if (closeResult != 0)
        {
            failures.Add(new Win32Exception((int)closeResult, "Cannot close the microphone device."));
        }
        else
        {
            _device = 0;
            if (_instance.IsAllocated)
            {
                _instance.Free();
            }
        }
        if (failures.Count > 0)
        {
            throw new AggregateException("Microphone cleanup failed.", failures);
        }
    }

    private static void Check(uint result, string operation)
    {
        if (result != 0)
        {
            throw new Win32Exception((int)result,
                $"Cannot {operation} (waveIn error {result}). Check microphone access in Windows privacy settings.");
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private sealed class CaptureBuffer(nint header, nint data)
    {
        public nint Header { get; } = header;
        public nint Data { get; } = data;
        public bool Prepared { get; set; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nuint User;
        public uint Flags;
        public uint Loops;
        public nint Next;
        public nuint Reserved;
    }

    [LibraryImport("winmm.dll", EntryPoint = "waveInOpen")]
    private static partial uint WaveInOpen(out nint device, uint deviceId, in WaveFormat format, nint callback, nuint instance, uint flags);
    [LibraryImport("winmm.dll", EntryPoint = "waveInPrepareHeader")]
    private static partial uint WaveInPrepareHeader(nint device, nint header, uint size);
    [LibraryImport("winmm.dll", EntryPoint = "waveInUnprepareHeader")]
    private static partial uint WaveInUnprepareHeader(nint device, nint header, uint size);
    [LibraryImport("winmm.dll", EntryPoint = "waveInAddBuffer")]
    private static partial uint WaveInAddBuffer(nint device, nint header, uint size);
    [LibraryImport("winmm.dll", EntryPoint = "waveInStart")]
    private static partial uint WaveInStart(nint device);
    [LibraryImport("winmm.dll", EntryPoint = "waveInStop")]
    private static partial uint WaveInStop(nint device);
    [LibraryImport("winmm.dll", EntryPoint = "waveInReset")]
    private static partial uint WaveInReset(nint device);
    [LibraryImport("winmm.dll", EntryPoint = "waveInClose")]
    private static partial uint WaveInClose(nint device);
}
