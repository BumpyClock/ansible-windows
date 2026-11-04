using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Ansible.Core;
using Ansible.Probe;

internal static class Program
{
    private const int Pairs = 5;
    private const int LeadInMs = 5000;
    private const int TailMs = 2000;

    private static async Task<int> Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        if (args is ["--self-check"])
        {
            var median = StreamingProbe.FormatSummary("fixture", [3, 1, 2, 5, 4], "ms");
            if (median != "fixture: median=3.0, min=1.0, max=5.0 ms (n=5)") throw new InvalidOperationException(median);
            if (Marshal.SizeOf<NativeCapture.Result>() != 88) throw new InvalidOperationException("C ABI result layout mismatch.");
            Console.WriteLine("PASS summary arithmetic and 88-byte capture ABI layout. No inference or screen capture.");
            return 0;
        }
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: OverheadSpike <repository-root> | --self-check");
            return 2;
        }
        var root = Path.GetFullPath(args[0]);
        var folder = Path.Combine(root, ".runtime", "record-spikes", "overhead", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
        Directory.CreateDirectory(folder);
        var rows = new List<Trial>();
        try
        {
            if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("Use the published NativeAOT executable.");
            var native = Path.Combine(root, ".runtime", "native", "audiocpp.dll");
            var catalog = Path.Combine(root, "tools", "audio-models.json");
            var sample = Path.Combine(root, ".runtime", "validation", "sample_16k.wav");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
            await using var engine = new NativeAudioEngine(native, catalog, Path.Combine(root, ".runtime", "models"), 4, 512);
            var models = await engine.ConnectAsync(timeout.Token);
            var model = models.Single(item => item.Id == AudioModelDefaults.RecognitionModelId);
            var pcm = await new AudioInputReader().ReadReplayAsync(sample, timeout.Token);
            var options = new RecognitionOptions(Backend: NativeBackend.Cpu);
            await engine.PrepareAsync(model, options, timeout.Token);
            File.WriteAllText(Path.Combine(folder, "identity.txt"), $"utc={DateTimeOffset.UtcNow:O}\n" +
                $"framework={RuntimeInformation.FrameworkDescription}\ndynamic-code={RuntimeFeature.IsDynamicCodeSupported}\n" +
                $"os={Environment.OSVersion}\nlogical-processors={Environment.ProcessorCount}\nmodel={model.Id}\nbackend=cpu\nthreads=4\nmemory-headroom-mb=512\n" +
                $"source-seconds={pcm.Length / 32000.0:F7}\nsampler-ms={MemorySampler.IntervalMs}\nlead-in-ms={LeadInMs}\ntail-ms={TailMs}\n" +
                string.Join('\n', new[] { native, sample, model.ModelPath!, Path.Combine(AppContext.BaseDirectory, "record_encoder_spike.dll"), Environment.ProcessPath! }
                    .Select(path =>
                    {
                        using var file = File.OpenRead(path);
                        return $"sha256={Convert.ToHexString(SHA256.HashData(file))} file={path}";
                    })));
            Console.WriteLine($"Output={folder}");
            Console.WriteLine("Claim under test: warm CPU recognition latency and sampled memory with current encoder off versus on. Screen activity is uncontrolled.");
            string? expected = null;
            for (int index = -2; index < Pairs * 2; index++)
            {
                bool capture = index % 2 != 0;
                var phase = index < 0 ? "diagnostic-warmup" : "measured";
                string name = $"{(index < 0 ? index + 3 : index + 1):00}-{phase}-{(capture ? "capture" : "baseline")}";
                Console.WriteLine($"Starting {name}");
                var row = await MeasureAsync(engine, model, pcm, options, capture, phase, name, folder, timeout.Token);
                expected ??= row.Speech;
                if (!StringComparer.Ordinal.Equals(expected, row.Speech))
                    throw new InvalidDataException($"Recognition text differs in {name}.");
                rows.Add(row);
                WriteRows(Path.Combine(folder, "trials.csv"), rows);
                Console.WriteLine($"PASS {name}: final-ms={row.FinalMs:F1}; source-final-ms={row.SourceToFinalMs:F1}; private-peak-mib={row.PeakPrivate / 1048576.0:F1}; frames={row.Frames}");
            }
            var measured = rows.Where(item => item.Phase == "measured").ToArray();
            var summary = new StringBuilder();
            foreach (var condition in new[] { false, true })
            {
                var side = measured.Where(item => item.Capture == condition).ToArray();
                summary.AppendLine(condition ? "capture" : "baseline");
                summary.AppendLine(StreamingProbe.FormatSummary("Final latency", side.Select(item => item.FinalMs), "ms"));
                summary.AppendLine(StreamingProbe.FormatSummary("Source duration to final", side.Select(item => item.SourceToFinalMs), "ms"));
                summary.AppendLine(StreamingProbe.FormatSummary("First preview", side.Where(item => item.FirstPreviewMs is not null).Select(item => item.FirstPreviewMs!.Value), "ms"));
                summary.AppendLine(StreamingProbe.FormatSummary("Sampled peak private", side.Select(item => item.PeakPrivate / 1048576.0), "MiB"));
                summary.AppendLine(StreamingProbe.FormatSummary("Sampled peak working set", side.Select(item => item.PeakWorking / 1048576.0), "MiB"));
                summary.AppendLine(StreamingProbe.FormatSummary("Process CPU during ASR", side.Select(item => item.CpuMs), "ms"));
                summary.AppendLine(StreamingProbe.FormatSummary("System active CPU during ASR", side.Select(item => item.SystemCpuPercent), "%"));
            }
            summary.AppendLine($"valid-measured={measured.Length}; errors=0; invalid=0; exact-speech-consistent=true; words={rows[0].Words}");
            File.WriteAllText(Path.Combine(folder, "summary.txt"), summary.ToString());
            File.WriteAllText(Path.Combine(folder, "outcome.txt"), "passed\n");
            Console.Write(summary);
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(folder, "outcome.txt"), $"failed hresult=0x{error.HResult:X8}\n{error}\ncompleted-trials={rows.Count}\n");
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static async Task<Trial> MeasureAsync(NativeAudioEngine engine, AudioModel model, byte[] pcm,
        RecognitionOptions options, bool capture, string phase, string name, string folder, CancellationToken token)
    {
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset);
        string video = Path.Combine(folder, name + ".mp4");
        Task<NativeCapture.Completion>? recording = capture ? Task.Factory.StartNew(
            () => NativeCapture.Record(video, stop.SafeWaitHandle.DangerousGetHandle()), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default) : null;
        NativeCapture.Completion? captured = null;
        StreamMeasurement? measurement = null;
        RecognitionResult? transcript = null;
        IReadOnlyList<(double Ms, long Private, long WorkingSet)> samples = [];
        long startQpc = 0, endQpc = 0;
        double cpuMs = 0, systemCpu = 0, finalizeMs = 0;
        try
        {
            // Equal lead-in controls the baseline schedule. Coverage is proved from actual frame timestamps below.
            await Task.Delay(LeadInMs, token);
            if (recording?.IsCompleted == true) throw new IOException("Capture ended before the recognition region.");
            using var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var systemBefore = NativeCapture.SystemTimes();
            using (var sampler = new MemorySampler())
            {
                startQpc = Stopwatch.GetTimestamp();
                (transcript, measurement) = await StreamingProbe.MeasureAsync(engine, model, pcm, options,
                    elapsed: () => Stopwatch.GetElapsedTime(startQpc));
                endQpc = Stopwatch.GetTimestamp();
                samples = sampler.Finish();
            }
            process.Refresh();
            cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            systemCpu = NativeCapture.SystemActivePercent(systemBefore, NativeCapture.SystemTimes());
            if (capture) await Task.Delay(TailMs, token);
        }
        finally
        {
            var stopped = Stopwatch.GetTimestamp();
            stop.Set();
            if (recording is not null) captured = await recording;
            finalizeMs = Stopwatch.GetElapsedTime(stopped).TotalMilliseconds;
        }
        var speech = transcript!.SpeechText ?? throw new InvalidDataException("No authoritative speech.");
        if (transcript.SpokenWords != 28 || !speech.Contains("Mother Nature", StringComparison.OrdinalIgnoreCase) ||
            transcript.ProcessedSamples != pcm.Length / 2)
            throw new InvalidDataException("Recognition did not consume and transcribe the complete public sample.");
        uint decoded = 0, audio = 0;
        long duration = 0;
        if (captured is not null)
        {
            Marshal.ThrowExceptionForHR(captured.HResult);
            var result = captured.Capture;
            var start100ns = startQpc * (10000000.0 / Stopwatch.Frequency);
            var end100ns = endQpc * (10000000.0 / Stopwatch.Frequency);
            Marshal.ThrowExceptionForHR(NativeCapture.Inspect(video, out decoded, out audio, out duration));
            var firstEncode100ns = result.FirstEncodeQpc * (10000000.0 / result.QpcFrequency);
            var lastEncode100ns = result.LastEncodeQpc * (10000000.0 / result.QpcFrequency);
            if (result.Frames == 0 || result.EncodedSamples == 0 || result.FirstFrame100ns > start100ns ||
                firstEncode100ns > start100ns || lastEncode100ns < end100ns ||
                result.FirstFrame100ns + duration < end100ns)
                throw new InvalidDataException($"Successful encoding and decoded video did not cover recognition. First-source={result.FirstFrame100ns}, first-encode={firstEncode100ns:F0}, ASR-start={start100ns:F0}, ASR-end={end100ns:F0}, last-encode={lastEncode100ns:F0}, video-end={result.FirstFrame100ns + duration}.");
            if (decoded == 0 || audio != 0) throw new InvalidDataException("Recorded MP4 is not decoded silent video.");
        }
        File.WriteAllLines(Path.Combine(folder, name + "-memory.csv"), new[] { "elapsedMs,privateBytes,workingSetBytes" }
            .Concat(samples.Select(item => $"{item.Ms:F3},{item.Private},{item.WorkingSet}")));
        return new(phase, name, capture, measurement!.FirstSpeechPreview?.TotalMilliseconds, measurement.EndOfInput.TotalMilliseconds,
            measurement.FinalResultLatency.TotalMilliseconds, measurement.SourceDurationToFinal.TotalMilliseconds,
            measurement.FullElapsed.TotalMilliseconds, samples.Max(item => item.Private), samples.Max(item => item.WorkingSet),
            samples.Count, cpuMs, systemCpu, transcript.SpokenWords.Value, speech,
            captured?.Capture.Frames ?? 0, decoded, duration / 10000.0, capture ? new FileInfo(video).Length : 0,
            captured?.WallMs ?? 0, captured?.WorkerCpuMs ?? 0, finalizeMs,
            startQpc, endQpc, captured?.Capture.FirstFrame100ns ?? 0, captured?.Capture.LastFrame100ns ?? 0,
            captured?.Capture.EncodedSamples ?? 0, captured?.Capture.HeldSamples ?? 0,
            captured?.Capture.FirstEncodeQpc ?? 0, captured?.Capture.LastEncodeQpc ?? 0, captured?.Capture.StopQpc ?? 0);
    }

    private static void WriteRows(string path, IReadOnlyList<Trial> rows)
    {
        File.WriteAllLines(path, new[] { "phase,name,capture,firstPreviewMs,endInputMs,finalMs,sourceToFinalMs,fullMs,peakPrivateBytes,peakWorkingBytes,memorySamples,cpuMs,systemCpuPercent,words,speechSha256,inputFrames,decodedFrames,videoDurationMs,videoBytes,captureWallMs,captureWorkerCpuMs,stopToFinalizedMs,asrStartQpc,asrEndQpc,firstFrame100ns,lastFrame100ns,encodedSamples,heldSamples,firstEncodeQpc,lastEncodeQpc,stopQpc" }
            .Concat(rows.Select(item => $"{item.Phase},{item.Name},{item.Capture},{item.FirstPreviewMs:F3},{item.EndInputMs:F3},{item.FinalMs:F3},{item.SourceToFinalMs:F3},{item.FullMs:F3},{item.PeakPrivate},{item.PeakWorking},{item.MemorySamples},{item.CpuMs:F3},{item.SystemCpuPercent:F3},{item.Words},{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Speech)))},{item.Frames},{item.Decoded},{item.VideoMs:F3},{item.VideoBytes},{item.CaptureWallMs:F3},{item.CaptureCpuMs:F3},{item.FinalizeMs:F3},{item.StartQpc},{item.EndQpc},{item.FirstFrame},{item.LastFrame},{item.EncodedSamples},{item.HeldSamples},{item.FirstEncodeQpc},{item.LastEncodeQpc},{item.StopQpc}")));
    }

    private sealed record Trial(string Phase, string Name, bool Capture, double? FirstPreviewMs, double EndInputMs,
        double FinalMs, double SourceToFinalMs, double FullMs, long PeakPrivate, long PeakWorking, int MemorySamples,
        double CpuMs, double SystemCpuPercent, int Words, string Speech, uint Frames, uint Decoded, double VideoMs,
        long VideoBytes, double CaptureWallMs, double CaptureCpuMs, double FinalizeMs,
        long StartQpc, long EndQpc, long FirstFrame, long LastFrame,
        uint EncodedSamples, uint HeldSamples, long FirstEncodeQpc, long LastEncodeQpc, long StopQpc);
}
