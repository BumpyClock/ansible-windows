using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace DictationPoc.Core;

public sealed class NativeAudioEngine : IAsyncDisposable
{
    private readonly string _libraryPath;
    private readonly string _catalogPath;
    private readonly int _threads;
    private readonly int _headroom;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NativeAudioHandle? _registry;
    private NativeAudioHandle? _model;
    private NativeAudioHandle? _session;
    private string? _loadedPath;
    private string? _mode;
    private bool _languageOption;
    private bool _disposed;

    public NativeAudioEngine(string libraryPath, string catalogPath, string modelsDirectory, int threads = 4, int memoryHeadroomMB = 512)
    {
        if (threads is < 1 or > 64 || memoryHeadroomMB < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(threads));
        }
        _libraryPath = Path.GetFullPath(libraryPath);
        _catalogPath = Path.GetFullPath(catalogPath);
        ModelsDirectory = Path.GetFullPath(modelsDirectory);
        _threads = threads;
        _headroom = memoryHeadroomMB;
    }

    public string ModelsDirectory { get; }
    public string Version { get; private set; } = "";

    public async Task<IReadOnlyList<AudioModel>> ConnectAsync(CancellationToken cancellationToken)
    {
        var models = await NativeModelCatalog.ReadAsync(_catalogPath, ModelsDirectory, cancellationToken);
        await RunExclusiveAsync(() =>
        {
            NativeAudioApi.Initialize(_libraryPath);
            Version = Marshal.PtrToStringUTF8(NativeAudioApi.BuildVersion())
                ?? throw new InvalidDataException("The native library returned no build version.");
            ReleaseModel();
            _registry?.Dispose();
            NativeAudioApi.Check(NativeAudioApi.RegistryCreate(0, out var pointer), "create the model registry");
            _registry = new NativeAudioHandle(pointer, NativeHandleKind.Registry);
            var families = new HashSet<string>(StringComparer.Ordinal);
            for (nuint index = 0; index < NativeAudioApi.FamilyCount(pointer); index++)
            {
                NativeAudioApi.Check(NativeAudioApi.Family(pointer, index, out var family), "inspect compiled model families");
                families.Add(Marshal.PtrToStringUTF8(family) ?? "");
            }
            foreach (var model in models)
            {
                if (model.Family is null || !families.Contains(model.Family))
                {
                    throw new NotSupportedException($"The native DLL does not include '{model.Family}'. Rebuild the ASR integration.");
                }
            }
            return true;
        }, cancellationToken);
        return models;
    }

    public Task<string> TranscribeFileAsync(
        AudioModel model, string path, string? language,
        IProgress<TranscriptUpdate>? progress, CancellationToken cancellationToken) =>
        RunExclusiveAsync(() =>
        {
            EnsureSession(model, "offline");
            var audio = WaveAudio.Read(path);
            using var request = CreateRequest(language);
            SetRequestAudio(request, audio);
            cancellationToken.ThrowIfCancellationRequested();
            NativeAudioApi.Check(NativeAudioApi.SessionRun(_session!.DangerousGetHandle(), request.DangerousGetHandle(), out var result),
                "transcribe the recording");
            using var owned = new NativeAudioHandle(result, NativeHandleKind.Result);
            cancellationToken.ThrowIfCancellationRequested();
            var text = ReadText(result, false)!;
            progress?.Report(new TranscriptUpdate(text, true));
            return text;
        }, cancellationToken);

    public Task<string> StreamAsync(
        AudioModel model, ChannelReader<byte[]> audio, string? language,
        IProgress<TranscriptUpdate>? progress, CancellationToken cancellationToken, Action? onReady = null) =>
        RunExclusiveAsync(() => StreamCore(model, audio, language, progress, cancellationToken, onReady), cancellationToken);

    private string StreamCore(
        AudioModel model, ChannelReader<byte[]> audio, string? language,
        IProgress<TranscriptUpdate>? progress, CancellationToken token, Action? onReady)
    {
        EnsureSession(model, "streaming");
        using var request = CreateRequest(language);
        var session = _session!.DangerousGetHandle();
        NativeAudioApi.Check(NativeAudioApi.StreamPolicy(session, 0, 0, out var frames, out var seconds), "read the streaming cadence");
        var preferred = seconds > 0 ? checked((int)Math.Round(seconds * 16000)) : checked((int)frames);
        if (preferred <= 0)
        {
            preferred = 1600;
        }
        if (preferred > 16000 * 30)
        {
            throw new InvalidDataException("The model requests an unsupported streaming chunk size.");
        }
        var buffer = new byte[preferred * 2];
        var used = 0;
        long offset = 0;
        var transcript = new StringBuilder();
        var completed = false;
        Exception? failure = null;
        SetStreamingAudioContract(request);
        NativeAudioApi.Check(NativeAudioApi.StreamStart(session, request.DangerousGetHandle()), "start native dictation");
        try
        {
            token.ThrowIfCancellationRequested();
            onReady?.Invoke();
            while (audio.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult())
            {
                while (audio.TryRead(out var packet))
                {
                    if (packet.Length == 0 || packet.Length % 2 != 0)
                    {
                        throw new InvalidDataException("Dictation requires complete PCM16 samples.");
                    }
                    var index = 0;
                    while (index < packet.Length)
                    {
                        var count = Math.Min(buffer.Length - used, packet.Length - index);
                        packet.AsSpan(index, count).CopyTo(buffer.AsSpan(used));
                        used += count;
                        index += count;
                        if (used == buffer.Length)
                        {
                            Push(session, buffer.AsSpan(0, used), offset, transcript, progress, token);
                            offset += used / 2;
                            used = 0;
                        }
                    }
                }
            }
            if (used > 0)
            {
                Push(session, buffer.AsSpan(0, used), offset, transcript, progress, token);
            }
            token.ThrowIfCancellationRequested();
            NativeAudioApi.Check(NativeAudioApi.StreamFinish(session, out var result), "finish native dictation");
            using var owned = new NativeAudioHandle(result, NativeHandleKind.Result);
            token.ThrowIfCancellationRequested();
            var text = ReadText(result, false)!;
            completed = true;
            progress?.Report(new TranscriptUpdate(text, true));
            return text;
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            if (!completed)
            {
                try
                {
                    NativeAudioApi.Check(NativeAudioApi.StreamReset(session), "reset the interrupted dictation session");
                }
                catch (Exception resetError) when (failure is not null)
                {
                    throw new AggregateException("Dictation and native cleanup failed.", failure, resetError);
                }
            }
        }
    }

    private static unsafe void Push(
        nint session, ReadOnlySpan<byte> bytes, long offset, StringBuilder transcript,
        IProgress<TranscriptUpdate>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var frames = bytes.Length / 2;
        var samples = ArrayPool<float>.Shared.Rent(frames);
        try
        {
            for (var index = 0; index < frames; index++)
            {
                samples[index] = (short)(bytes[index * 2] | bytes[index * 2 + 1] << 8) / 32768f;
            }
            fixed (float* pointer = samples)
            {
                NativeAudioApi.Check(NativeAudioApi.StreamPush(session, pointer, (nuint)frames, 16000, 1, offset, out var streamEvent),
                    "process a dictation audio chunk");
                AppendEvent(streamEvent, transcript, progress);
            }
            while (true)
            {
                NativeAudioApi.Check(NativeAudioApi.StreamNextEvent(session, out var streamEvent), "read queued transcript events");
                if (streamEvent == 0)
                {
                    break;
                }
                AppendEvent(streamEvent, transcript, progress);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(samples, true);
        }
    }

    private static void AppendEvent(nint pointer, StringBuilder transcript, IProgress<TranscriptUpdate>? progress)
    {
        if (pointer == 0)
        {
            return;
        }
        using var owned = new NativeAudioHandle(pointer, NativeHandleKind.Event);
        var text = ReadText(NativeAudioApi.EventResult(pointer), true);
        if (!string.IsNullOrEmpty(text))
        {
            transcript.Append(text);
            progress?.Report(new TranscriptUpdate(transcript.ToString(), false));
        }
    }

    private static string? ReadText(nint result, bool optional)
    {
        var status = NativeAudioApi.ResultText(result, out var text, out _);
        if (optional && status == 7)
        {
            return null;
        }
        NativeAudioApi.Check(status, "read the transcript");
        return Marshal.PtrToStringUTF8(text) ?? throw new InvalidDataException("The native transcript pointer is null.");
    }

    private void EnsureSession(AudioModel model, string mode)
    {
        if (_registry is null || model.ModelPath is null || model.Family is null)
        {
            throw new InvalidOperationException("Connect the native backend and choose an installed model first.");
        }
        if (_loadedPath != model.ModelPath)
        {
            ReleaseModel();
            NativeMemory.CheckModelBudget(model.ModelPath, _headroom);
            var family = Marshal.StringToCoTaskMemUTF8(model.Family);
            try
            {
                var config = new NativeAudioApi.ModelConfig { Family = family };
                NativeAudioApi.Check(NativeAudioApi.ModelLoad(_registry.DangerousGetHandle(), model.ModelPath, in config, 0, out var pointer),
                    $"load {model.Id}");
                _model = new NativeAudioHandle(pointer, NativeHandleKind.Model);
                _loadedPath = model.ModelPath;
                _languageOption = false;
                for (nuint index = 0; index < NativeAudioApi.OptionCount(pointer, 0); index++)
                {
                    NativeAudioApi.Check(NativeAudioApi.Option(pointer, 0, index, out var name, 0, 0, 0, 0, 0, 0), "inspect recognition options");
                    _languageOption |= Marshal.PtrToStringUTF8(name) == "language";
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(family);
            }
        }
        if (_session is not null && _mode == mode)
        {
            return;
        }
        _session?.Dispose();
        _session = null;
        _mode = null;
        if (NativeAudioApi.Supports(_model!.DangerousGetHandle(), "asr", mode) == 0)
        {
            throw new NotSupportedException($"{model.Id} does not support {mode} speech recognition.");
        }
        var backend = new NativeAudioApi.BackendConfig { Threads = _threads };
        NativeAudioApi.Check(NativeAudioApi.SessionCreate(_model.DangerousGetHandle(), "asr", mode, in backend, 0, out var session),
            "create the native recognition session");
        _session = new NativeAudioHandle(session, NativeHandleKind.Session);
        _mode = mode;
    }

    private NativeAudioHandle CreateRequest(string? language)
    {
        var request = new NativeAudioHandle(NativeAudioApi.RequestCreate(), NativeHandleKind.Request);
        try
        {
            NativeAudioApi.Check(NativeAudioApi.SetText(request.DangerousGetHandle(), "", null), "initialize recognition input");
            if (!string.IsNullOrWhiteSpace(language))
            {
                NativeAudioApi.Check(NativeAudioApi.SetTextLanguage(request.DangerousGetHandle(), language.Trim()), "set the transcript language");
                if (_languageOption)
                {
                    NativeAudioApi.Check(NativeAudioApi.SetOption(request.DangerousGetHandle(), "language", language.Trim()), "set the recognition language");
                }
            }
            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private static unsafe void SetRequestAudio(NativeAudioHandle request, WaveAudio audio)
    {
        fixed (float* samples = audio.Samples)
        {
            NativeAudioApi.Check(NativeAudioApi.SetAudio(request.DangerousGetHandle(), samples,
                (nuint)(audio.Samples.Length / audio.Channels), audio.SampleRate, audio.Channels), "set recording audio");
        }

    }

    private static unsafe void SetStreamingAudioContract(NativeAudioHandle request) =>
        NativeAudioApi.Check(NativeAudioApi.SetAudio(request.DangerousGetHandle(), null, 0, 16000, 1),
            "declare the live audio format");

    private async Task<T> RunExclusiveAsync<T>(Func<T> work, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Factory.StartNew(work, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ReleaseModel()
    {
        _session?.Dispose();
        _session = null;
        _model?.Dispose();
        _model = null;
        _loadedPath = null;
        _mode = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            ReleaseModel();
            _registry?.Dispose();
            _registry = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
