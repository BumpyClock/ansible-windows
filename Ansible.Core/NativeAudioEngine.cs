using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace Ansible.Core;

public sealed class NativeAudioEngine : IRecognitionEngine
{
    private readonly string _libraryPath;
    private readonly string _catalogPath;
    private readonly int _threads;
    private readonly int _headroom;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NativeAudioHandle? _registry;
    private NativeAudioHandle? _model;
    private NativeAudioHandle? _session;
    private readonly NativeModelIntegrity _integrity = new();
    private NativeModelInventory? _inventory;
    private FileStream? _modelLease;
    private string? _loadedId;
    private bool _languageOption;
    private bool _streamStartAttempted;
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

    public static Task<IReadOnlySet<string>> GetSupportedFamiliesAsync(
        string libraryPath, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlySet<string>>(() =>
        {
            NativeOperation.Step(cancellationToken, () => NativeAudioApi.Initialize(Path.GetFullPath(libraryPath)));
            NativeAudioApi.Check(NativeAudioApi.RegistryCreate(0, out var pointer), "inspect the compiled model registry");
            using var registry = new NativeAudioHandle(pointer, NativeHandleKind.Registry);
            return ReadFamilies(registry, cancellationToken);
        }, cancellationToken);

    private static HashSet<string> ReadFamilies(NativeAudioHandle registry, CancellationToken token)
    {
        var families = new HashSet<string>(StringComparer.Ordinal);
        var count = NativeAudioApi.FamilyCount(registry.DangerousGetHandle());
        for (nuint index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            NativeAudioApi.Check(NativeAudioApi.Family(registry.DangerousGetHandle(), index, out var family),
                "inspect compiled model families");
            families.Add(Marshal.PtrToStringUTF8(family) ?? throw new InvalidDataException("A compiled model family has no name."));
        }
        token.ThrowIfCancellationRequested();
        return families;
    }

    public async Task<IReadOnlyList<AudioModel>> ConnectAsync(CancellationToken cancellationToken)
    {
        var inventory = await NativeModelCatalog.ReadAsync(_catalogPath, ModelsDirectory, cancellationToken);
        await RunExclusiveAsync(() =>
        {
            ReleaseModel();
            _registry?.Dispose();
            _registry = null;
            _inventory = null;
            try
            {
                NativeOperation.Step(cancellationToken, () => NativeAudioApi.Initialize(_libraryPath));
                Version = Marshal.PtrToStringUTF8(NativeAudioApi.BuildVersion())
                    ?? throw new InvalidDataException("The native library returned no build version.");
                NativeOperation.Step(cancellationToken, () =>
                {
                    NativeAudioApi.Check(NativeAudioApi.RegistryCreate(0, out var pointer), "create the model registry");
                    _registry = new NativeAudioHandle(pointer, NativeHandleKind.Registry);
                });
                var families = ReadFamilies(_registry!, cancellationToken);
                foreach (var model in inventory.Models)
                {
                    if (model.Family is null || !families.Contains(model.Family))
                        throw new NotSupportedException($"The native DLL does not include '{model.Family}'. Rebuild the ASR integration.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                _inventory = inventory;
                return true;
            }
            catch
            {
                _registry?.Dispose();
                _registry = null;
                throw;
            }
        }, cancellationToken);
        return inventory.Models;
    }

    public async Task<RecognitionResult> TranscribeAsync(
        AudioModel model, WaveAudio audio, RecognitionOptions options,
        IProgress<TranscriptUpdate>? progress, CancellationToken cancellationToken)
    {
        options = ValidateOptions(model, options);
        var result = await RunExclusiveAsync(() => NativeOperation.Run(() =>
        {
            var bytes = NativeMemory.ValidateAudio(audio, cancellationToken);
            EnsureSession(model, "offline", bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using var request = CreateRequest(options, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            NativeOperation.Step(cancellationToken, () => SetRequestAudio(request, audio));
            NativeAudioApi.Check(NativeAudioApi.SessionRun(_session!.DangerousGetHandle(), request.DangerousGetHandle(), out var pointer),
                "transcribe the recording");
            using var owned = new NativeAudioHandle(pointer, NativeHandleKind.Result);
            cancellationToken.ThrowIfCancellationRequested();
            return ReadResult(pointer, model.Family!, cancellationToken);
        }, CleanupOperation), cancellationToken);
        progress?.Report(new TranscriptUpdate(result.DisplayText, true, result.SpeechText));
        return result;
    }

    public async Task<RecognitionResult> StreamAsync(
        AudioModel model, ChannelReader<byte[]> audio, RecognitionOptions options,
        IProgress<TranscriptUpdate>? progress, CancellationToken cancellationToken, Action? onReady = null)
    {
        ArgumentNullException.ThrowIfNull(audio);
        options = ValidateOptions(model, options);
        var result = await RunExclusiveAsync(() => NativeOperation.Run(
            () => StreamCore(model, audio, options, progress, cancellationToken, onReady), CleanupOperation), cancellationToken);
        progress?.Report(new TranscriptUpdate(result.DisplayText, true, result.SpeechText));
        return result;
    }

    private RecognitionResult StreamCore(
        AudioModel model, ChannelReader<byte[]> audio, RecognitionOptions options,
        IProgress<TranscriptUpdate>? progress, CancellationToken token, Action? onReady)
    {
        EnsureSession(model, "streaming", NativeMemory.MaximumStreamFrames * sizeof(float), token);
        token.ThrowIfCancellationRequested();
        using var request = CreateRequest(options, token);
        token.ThrowIfCancellationRequested();
        var session = _session!.DangerousGetHandle();
        long frames = 0;
        double seconds = 0;
        NativeOperation.Step(token, () => NativeAudioApi.Check(
            NativeAudioApi.StreamPolicy(session, 0, 0, out frames, out seconds), "read the streaming cadence"));
        if (!double.IsFinite(seconds) || seconds < 0 || frames < 0 || seconds > 30 || frames > 16000 * 30)
            throw new InvalidDataException("The model requests an unsupported streaming chunk size.");
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
        long receivedFrames = 0;
        var transcript = new StreamingTranscript();
        NativeOperation.Step(token, () => SetStreamingAudioContract(request));
        NativeOperation.Step(token, () =>
        {
            _streamStartAttempted = true;
            NativeAudioApi.Check(NativeAudioApi.StreamStart(session, request.DangerousGetHandle()), "start native dictation");
        });
        onReady?.Invoke();
        while (audio.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult())
        {
            while (audio.TryRead(out var packet))
            {
                token.ThrowIfCancellationRequested();
                if (packet is null || packet.Length == 0 || packet.Length % 2 != 0)
                    throw new InvalidDataException("Dictation requires complete PCM16 samples.");
                receivedFrames = checked(receivedFrames + packet.Length / 2);
                if (receivedFrames > NativeMemory.MaximumStreamFrames)
                    throw new InvalidDataException("Dictation audio exceeds the five-minute limit.");
                var index = 0;
                while (index < packet.Length)
                {
                    token.ThrowIfCancellationRequested();
                    var count = Math.Min(buffer.Length - used, packet.Length - index);
                    packet.AsSpan(index, count).CopyTo(buffer.AsSpan(used));
                    used += count;
                    index += count;
                    if (used == buffer.Length)
                    {
                        Push(session, buffer.AsSpan(0, used), offset, model.Family!, transcript, progress, token);
                        offset += used / 2;
                        used = 0;
                    }
                }
            }
        }
        if (used > 0)
            Push(session, buffer.AsSpan(0, used), offset, model.Family!, transcript, progress, token);
        token.ThrowIfCancellationRequested();
        NativeAudioApi.Check(NativeAudioApi.StreamFinish(session, out var result), "finish native dictation");
        using var owned = new NativeAudioHandle(result, NativeHandleKind.Result);
        token.ThrowIfCancellationRequested();
        return ReadResult(result, model.Family!, token);
    }

    private static unsafe void Push(
        nint session, ReadOnlySpan<byte> bytes, long offset, string family, StreamingTranscript transcript,
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
                AppendEvent(streamEvent, family, transcript, progress, token);
            }
            token.ThrowIfCancellationRequested();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                NativeAudioApi.Check(NativeAudioApi.StreamNextEvent(session, out var streamEvent), "read queued transcript events");
                if (streamEvent == 0)
                {
                    break;
                }
                AppendEvent(streamEvent, family, transcript, progress, token);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(samples, true);
        }
    }

    private static void AppendEvent(nint pointer, string family, StreamingTranscript transcript,
        IProgress<TranscriptUpdate>? progress, CancellationToken token)
    {
        if (pointer == 0)
        {
            return;
        }
        using var owned = new NativeAudioHandle(pointer, NativeHandleKind.Event);
        var resultPointer = NativeAudioApi.EventResult(pointer);
        var result = ReadResult(resultPointer, family, token, optionalText: true);
        if (!string.IsNullOrEmpty(result.DisplayText) || !string.IsNullOrEmpty(result.SpeechText))
        {
            var speechMetadata = NativeAudioApi.SegmentCount(resultPointer) > 0 ||
                NativeAudioApi.SpeakerTurnCount(resultPointer) > 0;
            progress?.Report(transcript.Append(result, speechMetadata));
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

    private static RecognitionResult ReadResult(nint result, string family, CancellationToken token, bool optionalText = false)
    {
        var text = ReadText(result, optionalText) ?? "";
        List<string> segments = [];
        List<string> turns = [];
        var segmentCount = NativeAudioApi.SegmentCount(result);
        var turnCount = NativeAudioApi.SpeakerTurnCount(result);
        if (segmentCount > 100000 || turnCount > 100000)
            throw new InvalidDataException("The native transcript has too many speech metadata entries.");
        for (nuint index = 0; index < segmentCount; index++)
        {
            token.ThrowIfCancellationRequested();
            NativeAudioApi.Check(NativeAudioApi.Segment(result, index, 0, 0, 0, out var piece), "read speech segments");
            segments.Add(Marshal.PtrToStringUTF8(piece)
                ?? throw new InvalidDataException("The native speech segment pointer is null."));
        }
        for (nuint index = 0; index < turnCount; index++)
        {
            token.ThrowIfCancellationRequested();
            NativeAudioApi.Check(NativeAudioApi.SpeakerTurn(result, index, 0, 0, 0, 0, out var piece), "read speaker turns");
            turns.Add(Marshal.PtrToStringUTF8(piece)
                ?? throw new InvalidDataException("The native speaker turn pointer is null."));
        }
        token.ThrowIfCancellationRequested();
        return NativeTranscript.Normalize(family, text, segments, turns);
    }

    private void EnsureSession(AudioModel model, string mode, long decodedBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_registry is null || _inventory is null)
            throw new InvalidOperationException("Connect the native backend and choose an installed model first.");
        var admitted = _inventory.Models.FirstOrDefault(candidate => candidate.Id == model.Id);
        if (admitted is null || admitted != model || model.ModelPath is null || model.Family is null)
            throw new InvalidDataException("Recognition requires an unchanged model selection from the connected catalog.");
        var entry = _inventory.Entries[model.Id];
        if (_loadedId != model.Id)
        {
            ReleaseModel();
            NativeOperation.Step(token, () => NativeMemory.CheckBudget(entry.Bytes, decodedBytes, _headroom));
            _modelLease = _integrity.OpenVerified(model.ModelPath, entry, token);
            token.ThrowIfCancellationRequested();
            var family = Marshal.StringToCoTaskMemUTF8(model.Family);
            try
            {
                var config = new NativeAudioApi.ModelConfig { Family = family };
                NativeOperation.Step(token, () =>
                {
                    NativeAudioApi.Check(NativeAudioApi.ModelLoad(_registry.DangerousGetHandle(), model.ModelPath, in config, 0, out var pointer),
                        $"load {model.Id}");
                    _model = new NativeAudioHandle(pointer, NativeHandleKind.Model);
                });
                _languageOption = false;
                nuint count = 0;
                NativeOperation.Step(token, () => count = NativeAudioApi.OptionCount(_model!.DangerousGetHandle(), 0));
                for (nuint index = 0; index < count; index++)
                {
                    NativeOperation.Step(token, () =>
                    {
                        NativeAudioApi.Check(NativeAudioApi.Option(_model!.DangerousGetHandle(), 0, index, out var name, 0, 0, 0, 0, 0, 0),
                            "inspect recognition options");
                        _languageOption |= Marshal.PtrToStringUTF8(name) == "language";
                    });
                }
                _loadedId = model.Id;
            }
            finally
            {
                Marshal.FreeCoTaskMem(family);
            }
        }
        NativeOperation.Step(token, () =>
        {
            if (NativeAudioApi.Supports(_model!.DangerousGetHandle(), "asr", mode) == 0)
                throw new NotSupportedException($"{model.Id} does not support {mode} speech recognition.");
        });
        var backend = new NativeAudioApi.BackendConfig { Threads = _threads };
        NativeOperation.Step(token, () =>
        {
            // A cached model handle does not retain session-owned execution weights.
            NativeMemory.CheckBudget(entry.Bytes, decodedBytes, _headroom);
            token.ThrowIfCancellationRequested();
            NativeAudioApi.Check(NativeAudioApi.SessionCreate(_model!.DangerousGetHandle(), "asr", mode, in backend, 0, out var session),
                "create the native recognition session");
            _session = new NativeAudioHandle(session, NativeHandleKind.Session);
        });
    }

    private static RecognitionOptions ValidateOptions(AudioModel model, RecognitionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var dictionary = CustomVocabulary.Normalize(options.CustomDictionary);
        if (dictionary.Length > 0 && !model.SupportsCustomDictionary)
            throw new NotSupportedException($"{model.Id} does not support custom dictionary hints in this native backend.");
        return options with { CustomDictionary = dictionary };
    }

    private NativeAudioHandle CreateRequest(RecognitionOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var request = new NativeAudioHandle(NativeAudioApi.RequestCreate(), NativeHandleKind.Request);
        try
        {
            NativeOperation.Step(token, () =>
                NativeAudioApi.Check(NativeAudioApi.SetText(request.DangerousGetHandle(), options.CustomDictionary, null),
                    "set recognition context"));
            if (!string.IsNullOrWhiteSpace(options.Language))
            {
                NativeOperation.Step(token, () =>
                    NativeAudioApi.Check(NativeAudioApi.SetTextLanguage(request.DangerousGetHandle(), options.Language.Trim()), "set the transcript language"));
                if (_languageOption)
                {
                    NativeOperation.Step(token, () =>
                        NativeAudioApi.Check(NativeAudioApi.SetOption(request.DangerousGetHandle(), "language", options.Language.Trim()), "set the recognition language"));
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

    private void CleanupOperation(bool success)
    {
        var session = _session;
        _session = null;
        var reset = _streamStartAttempted;
        _streamStartAttempted = false;
        NativeOperation.Cleanup(success,
            reset && session is not null
                ? () => NativeAudioApi.Check(NativeAudioApi.StreamReset(session.DangerousGetHandle()), "reset the dictation session")
                : null,
            () => session?.Dispose(), ReleaseModel);
    }

    private void ReleaseModel()
    {
        _session?.Dispose();
        _session = null;
        _streamStartAttempted = false;
        _model?.Dispose();
        _model = null;
        _modelLease?.Dispose();
        _modelLease = null;
        _loadedId = null;
        _languageOption = false;
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
