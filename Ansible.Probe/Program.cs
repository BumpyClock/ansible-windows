using System.Diagnostics;
using Ansible.Core;
using Ansible.Probe;

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
    Console.Error.WriteLine("Usage: Ansible.Probe --cpu-check | <native-dll> <models-directory> <catalog-json> <16k-mono-PCM16.wav> [model-id] [--file-only | --stream-only] [--repeat <1..20>] [--dictionary <one-term-per-line>] [--backend cpu|vulkan|auto] [--device <nonnegative-index>] [--device-probe <executable>] [--benchmark-cache <json>]");
    return 2;
}

var clock = Stopwatch.StartNew();
var stage = "native initialization";
try
{
    string? modelId = null;
    string? dictionary = null;
    var fileOnly = false;
    var streamOnly = false;
    int? repeat = null;
    NativeBackend? backend = null;
    int? deviceIndex = null;
    string? deviceProbe = null;
    string? benchmarkCache = null;
    for (var index = 4; index < args.Length; index++)
    {
        if (args[index] == "--file-only" && !fileOnly) { fileOnly = true; }
        else if (args[index] == "--stream-only" && !streamOnly) { streamOnly = true; }
        else if (args[index] == "--repeat" && repeat is null && index + 1 < args.Length)
            repeat = int.TryParse(args[++index], out var attempts) && attempts is >= 1 and <= 20
                ? attempts : throw new ArgumentException("Repeat must be an integer from 1 to 20.");
        else if (args[index] == "--backend" && backend is null && index + 1 < args.Length)
            backend = args[++index] switch
            {
                "cpu" => NativeBackend.Cpu,
                "vulkan" => NativeBackend.Vulkan,
                "auto" => NativeBackend.Auto,
                _ => throw new ArgumentException("Backend must be cpu, vulkan or auto.")
            };
        else if (args[index] == "--device-probe" && deviceProbe is null && index + 1 < args.Length)
            deviceProbe = args[++index];
        else if (args[index] == "--benchmark-cache" && benchmarkCache is null && index + 1 < args.Length)
            benchmarkCache = args[++index];
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
    if (fileOnly && (streamOnly || repeat is not null))
        throw new ArgumentException("--file-only cannot be combined with --stream-only or --repeat.");
    var options = new RecognitionOptions(CustomDictionary: CustomVocabulary.Normalize(dictionary ?? ""),
        Backend: backend ?? NativeBackend.Cpu, DeviceIndex: deviceIndex ?? 0);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    Console.WriteLine($"Requested backend={backend ?? NativeBackend.Cpu}; device={deviceIndex ?? 0}");
    await using var engine = new NativeAudioEngine(args[0], args[2], args[1], threads: 4, memoryHeadroomMB: 512,
        deviceProbePath: deviceProbe, benchmarkSamplePath: args[3], benchmarkCachePath: benchmarkCache);
    var connectedModels = await engine.ConnectAsync(timeout.Token);
    if (modelId is not null && connectedModels.Any(model => model.Id == modelId && ModelSelectionPolicy.IsDiarizationTask(model.Task)))
        throw new NotSupportedException($"Model '{modelId}' is a diarization companion, not an ASR model; ASR transcription is unsupported.");
    var models = ModelSelectionPolicy.DictationModels(connectedModels);
    var model = modelId is not null
        ? models.FirstOrDefault(model => model.Id == modelId) ??
            throw new ArgumentException($"Model '{modelId}' is not installed in the native catalog.")
        : models.FirstOrDefault(model => model.Mode == "streaming") ??
            models.FirstOrDefault() ?? throw new InvalidDataException("No ASR models are installed.");
    Console.WriteLine($"Native DLL={Path.GetFullPath(args[0])}; available models={string.Join(", ", models.Select(model => model.Id))}");
    Console.WriteLine($"Selected model={model.Id}; mode={model.Mode ?? "not reported"}; family={model.Family}; path={model.ModelPath}");
    Console.WriteLine($"Custom dictionary supported={model.SupportsCustomDictionary}; context supplied={options.CustomDictionary.Length > 0}");
    if (model.Mode == "offline" && repeat is not null && !streamOnly)
        throw new ArgumentException("Use --stream-only with --repeat to measure buffered dictation for an offline model.");
    IAudioInputReader input = new AudioInputReader();
    if (!streamOnly)
    {
        var decoded = await input.ReadRecordingAsync(args[3], timeout.Token);
        Console.WriteLine($"WAV input duration={decoded.Samples.Length * 1000.0 / decoded.SampleRate / decoded.Channels:F1} ms");
        stage = $"WAV transcription ({model.Id})";
        Console.WriteLine($"Attempting {stage}; timeout=180 seconds.");
        clock.Restart();
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var transcript = await engine.TranscribeAsync(
            model, decoded, options, null, timeout.Token);
        if (string.IsNullOrWhiteSpace(transcript.SpeechText))
        {
            throw new InvalidDataException("The native engine returned no authoritative speech for the validation recording.");
        }
        Console.WriteLine($"WAV transcription completed in {clock.Elapsed.TotalMilliseconds:F0} ms: {transcript.DisplayText}");
        Console.WriteLine($"Spoken words={transcript.SpokenWords?.ToString() ?? "unknown"}");
        if (backend == NativeBackend.Auto) { Console.WriteLine(engine.DeviceSelectionSummary); }
        if (model.Mode == "offline" || fileOnly)
        {
            return 0;
        }
    }

    stage = "read stream input";
    clock.Restart();
    timeout.CancelAfter(TimeSpan.FromMinutes(3));
    var pcm = await input.ReadReplayAsync(args[3], timeout.Token);
    Console.WriteLine($"Stream input duration={pcm.Length * 1000.0 / 32000:F1} ms (16 kHz mono PCM16)");
    stage = $"stream preparation ({model.Id})";
    Console.WriteLine($"Attempting {stage}; timeout=180 seconds.");
    clock.Restart();
    timeout.CancelAfter(TimeSpan.FromMinutes(3));
    await engine.PrepareAsync(model, options, timeout.Token);
    Console.WriteLine($"Stream preparation latency={clock.Elapsed.TotalMilliseconds:F1} ms (excluded from stream timings; includes loading/backend selection)");
    if (backend == NativeBackend.Auto) { Console.WriteLine(engine.DeviceSelectionSummary); }
    Console.WriteLine("Streams reuse the prepared session. Timings are paced wall-clock measurements, not pure compute latency.");
    Console.WriteLine("Packets: up to 1280 bytes (40 ms), available at absolute packet-end timestamps; first full packet at 40 ms; backpressure may delay acceptance.");
    Console.WriteLine("End of input: immediately after the final scheduled packet is accepted by the bounded channel, before channel completion; not native consumption.");
    Console.WriteLine("Speech previews exclude final events and empty/unknown speech; the first non-final preview may arrive after end of input.");
    var measurements = new List<StreamMeasurement>();
    try
    {
        for (var attempt = 1; attempt <= (repeat ?? 1); attempt++)
        {
            stage = $"live stream ({model.Id}), attempt {attempt}/{repeat ?? 1}";
            Console.WriteLine($"Attempting {stage}; timeout=180 seconds.");
            clock.Restart();
            var (transcript, measurement) = await StreamingProbe.MeasureAsync(engine, model, pcm, options);
            measurements.Add(measurement);
            Console.WriteLine($"End of input from stream start={measurement.EndOfInput.TotalMilliseconds:F1} ms (pacing + backpressure)");
            var firstPreview = measurement.FirstSpeechPreview is { } first
                ? $"{first.TotalMilliseconds:F1} ms ({(first < measurement.EndOfInput ? "before" : "at/after")} end of input)"
                : "none";
            Console.WriteLine($"First nonempty speech preview from stream start={firstPreview}");
            Console.WriteLine($"Nonempty speech previews before end of input={measurement.PreviewsBeforeEndOfInput}");
            Console.WriteLine($"End-of-input-to-final result latency={measurement.FinalResultLatency.TotalMilliseconds:F1} ms (includes queued audio processing)");
            Console.WriteLine($"Source-duration-to-final latency={measurement.SourceDurationToFinal.TotalMilliseconds:F1} ms (includes capture backlog)");
            Console.WriteLine($"Full stream elapsed={measurement.FullElapsed.TotalMilliseconds:F1} ms");
            Console.WriteLine(transcript.DisplayText);
            Console.WriteLine($"Spoken words={transcript.SpokenWords?.ToString() ?? "unknown"}");
            if (backend == NativeBackend.Auto) { Console.WriteLine(engine.DeviceSelectionSummary); }
        }
    }
    finally
    {
        Console.WriteLine($"Stream summary: {measurements.Count}/{repeat ?? 1} completed attempts; preparation excluded.");
        if (measurements.Count > 0)
        {
            PrintSummary("End of input from stream start", measurements.Select(item => item.EndOfInput.TotalMilliseconds), "ms");
            PrintSummary("First nonempty speech preview from stream start",
                measurements.Where(item => item.FirstSpeechPreview is not null)
                    .Select(item => item.FirstSpeechPreview!.Value.TotalMilliseconds), "ms");
            Console.WriteLine($"Attempts without a nonempty speech preview={measurements.Count(item => item.FirstSpeechPreview is null)}/{measurements.Count}");
            PrintSummary("Nonempty speech previews before end of input", measurements.Select(item => (double)item.PreviewsBeforeEndOfInput), "previews");
            PrintSummary("End-of-input-to-final result latency", measurements.Select(item => item.FinalResultLatency.TotalMilliseconds), "ms");
            PrintSummary("Source-duration-to-final latency (includes capture backlog)",
                measurements.Select(item => item.SourceDurationToFinal.TotalMilliseconds), "ms");
            PrintSummary("Full stream elapsed", measurements.Select(item => item.FullElapsed.TotalMilliseconds), "ms");
        }
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"{stage} failed after {clock.Elapsed.TotalMilliseconds:F0} ms: {error.GetType().Name}: {error.Message}");
    Console.Error.WriteLine(error.StackTrace);
    return 1;
}

static void PrintSummary(string name, IEnumerable<double> samples, string unit) =>
    Console.WriteLine(StreamingProbe.FormatSummary(name, samples, unit));
