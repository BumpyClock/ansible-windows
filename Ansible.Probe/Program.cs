using System.Diagnostics;
using System.Threading.Channels;
using Ansible.Core;

if (args is ["--cpu-check"])
{
    try
    {
        var features = NativeCpu.ReadFeatures();
        Console.WriteLine($"Hardware and OS features: {features}");
        Console.WriteLine(
            $"Managed intrinsic availability (not hardware eligibility): " +
            $"AVX2={System.Runtime.Intrinsics.X86.Avx2.IsSupported}, " +
            $"FMA={System.Runtime.Intrinsics.X86.Fma.IsSupported}, " +
            $"BMI1={System.Runtime.Intrinsics.X86.Bmi1.IsSupported}, BMI2={System.Runtime.Intrinsics.X86.Bmi2.IsSupported}");
        NativeCpu.Validate(features);
        Console.WriteLine("Native CPU eligibility: supported.");
        return 0;
    }
    catch (PlatformNotSupportedException error)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
}

if (args.Length < 4)
{
    Console.Error.WriteLine("Usage: Ansible.Probe --cpu-check | <native-dll> <models-directory> <catalog-json> <16k-mono-PCM16.wav> [model-id] [--file-only] [--dictionary <one-term-per-line>] [--backend cpu|vulkan] [--device <nonnegative-index>]");
    return 2;
}

var clock = Stopwatch.StartNew();
var stage = "native initialization";
try
{
    string? modelId = null;
    string? dictionary = null;
    var fileOnly = false;
    NativeBackend? backend = null;
    int? deviceIndex = null;
    for (var index = 4; index < args.Length; index++)
    {
        if (args[index] == "--file-only" && !fileOnly) { fileOnly = true; }
        else if (args[index] == "--backend" && backend is null && index + 1 < args.Length)
            backend = args[++index] switch
            {
                "cpu" => NativeBackend.Cpu,
                "vulkan" => NativeBackend.Vulkan,
                _ => throw new ArgumentException("Backend must be cpu or vulkan.")
            };
        else if (args[index] == "--device" && deviceIndex is null && index + 1 < args.Length)
            deviceIndex = int.TryParse(args[++index], out var device) && device >= 0
                ? device : throw new ArgumentException("Device index must be a nonnegative integer.");
        else if (args[index] == "--dictionary" && dictionary is null && index + 1 < args.Length &&
                 !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            dictionary = args[++index];
        else if (!args[index].StartsWith("--", StringComparison.Ordinal) && modelId is null)
            modelId = args[index];
        else { throw new ArgumentException($"Unexpected or incomplete probe argument '{args[index]}'."); }
    }
    var options = new RecognitionOptions(CustomDictionary: CustomVocabulary.Normalize(dictionary ?? ""),
        Backend: backend ?? NativeBackend.Cpu, DeviceIndex: deviceIndex ?? 0);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    Console.WriteLine($"Requested backend={backend ?? NativeBackend.Cpu}; device={deviceIndex ?? 0}");
    await using var engine = new NativeAudioEngine(args[0], args[2], args[1], threads: 4, memoryHeadroomMB: 512);
    var models = await engine.ConnectAsync(timeout.Token);
    var model = modelId is not null
        ? models.FirstOrDefault(model => model.Id == modelId) ??
            throw new ArgumentException($"Model '{modelId}' is not installed in the native catalog.")
        : models.FirstOrDefault(model => model.Mode == "streaming") ??
            models.FirstOrDefault() ?? throw new InvalidDataException("No ASR models are installed.");
    Console.WriteLine($"Native DLL={Path.GetFullPath(args[0])}; available models={string.Join(", ", models.Select(model => model.Id))}");
    Console.WriteLine($"Selected model={model.Id}; mode={model.Mode ?? "not reported"}; family={model.Family}; path={model.ModelPath}");
    Console.WriteLine($"Custom dictionary supported={model.SupportsCustomDictionary}; context supplied={options.CustomDictionary.Length > 0}");
    IAudioInputReader input = new AudioInputReader();
    var decoded = await input.ReadRecordingAsync(args[3], timeout.Token);

    stage = $"WAV transcription ({model.Id})";
    Console.WriteLine($"Attempting {stage}; timeout=180 seconds.");
    clock.Restart();
    timeout.CancelAfter(TimeSpan.FromMinutes(3));
    var transcript = await engine.TranscribeAsync(
        model, decoded, options, null, timeout.Token);
    if (string.IsNullOrWhiteSpace(transcript.DisplayText))
    {
        throw new InvalidDataException("The native engine returned no speech for the validation recording.");
    }
    Console.WriteLine($"WAV transcription completed in {clock.Elapsed.TotalMilliseconds:F0} ms: {transcript.DisplayText}");
    Console.WriteLine($"Spoken words={transcript.SpokenWords?.ToString() ?? "unknown"}");
    if (model.Mode == "offline" || fileOnly)
    {
        return 0;
    }

    stage = $"live stream ({model.Id})";
    var pcm = await input.ReadReplayAsync(args[3], timeout.Token);
    Console.WriteLine($"Attempting {stage}; timeout=180 seconds.");
    var audio = Channel.CreateBounded<byte[]>(8);
    var uploadFinished = false;
    var previewsDuringUpload = 0;
    clock.Restart();
    timeout.CancelAfter(TimeSpan.FromMinutes(3));
    var streaming = engine.StreamAsync(model, audio.Reader, options,
        new InlineProgress<TranscriptUpdate>(update =>
        {
            if (!update.IsFinal && !Volatile.Read(ref uploadFinished))
            {
                Interlocked.Increment(ref previewsDuringUpload);
            }
        }), timeout.Token);
    Exception? primaryFailure = null;
    try
    {
        for (var offset = 0; offset < pcm.Length; offset += 1280)
        {
            var write = audio.Writer.WriteAsync(
                pcm.AsSpan(offset, Math.Min(1280, pcm.Length - offset)).ToArray(), timeout.Token).AsTask();
            if (await Task.WhenAny(write, streaming) == streaming)
            {
                await streaming;
                throw new InvalidDataException("The native engine ended the live stream before audio capture finished.");
            }
            await write;
            await Task.Delay(40, timeout.Token);
        }
        Volatile.Write(ref uploadFinished, true);
        audio.Writer.Complete();
        var liveTranscript = await streaming;
        if (string.IsNullOrWhiteSpace(liveTranscript.DisplayText))
        {
            throw new InvalidDataException("The native live stream returned no speech.");
        }
        Console.WriteLine($"Live stream completed in {clock.Elapsed.TotalMilliseconds:F0} ms; previews during upload={previewsDuringUpload}");
        Console.WriteLine(liveTranscript.DisplayText);
        Console.WriteLine($"Spoken words={liveTranscript.SpokenWords?.ToString() ?? "unknown"}");
    }
    catch (Exception error)
    {
        primaryFailure = error;
        throw;
    }
    finally
    {
        audio.Writer.TryComplete();
        timeout.Cancel();
        try { await streaming; }
        catch (Exception cleanupError) when (primaryFailure is not null)
        {
            if (!ReferenceEquals(primaryFailure, cleanupError) && cleanupError is not OperationCanceledException)
                throw new AggregateException("Probe input and native recognition cleanup failed.", primaryFailure, cleanupError);
        }
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"{stage} failed after {clock.Elapsed.TotalMilliseconds:F0} ms: {error.GetType().Name}: {error.Message}");
    return 1;
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
