using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;

namespace Ansible.Core;

public static partial class DeviceBenchmarkWorker
{
    [LibraryImport("kernel32.dll")]
    private static partial uint SetErrorMode(uint mode);

    public static async Task<int> RunAsync(string[] args)
    {
        if (OperatingSystem.IsWindows()) SetErrorMode(0x0001 | 0x0002);
        DeviceBenchmarkRequest? request = null;
        DeviceBenchmarkResult result;
        try
        {
            if (args.Length != 2 || args[0] != "--device-benchmark" || args[1].Length > 32768)
                throw new InvalidDataException("Expected a bounded device benchmark request.");
            request = JsonSerializer.Deserialize(Convert.FromBase64String(args[1]), DeviceBenchmarkJson.Default.DeviceBenchmarkRequest)
                ?? throw new InvalidDataException("Device benchmark request is null.");
            if (request.Backend is not NativeBackend.Cpu and not NativeBackend.Vulkan ||
                request.Mode is not "offline" and not "streaming" || request.History is not 0 and not 4096 ||
                request.Mode == "offline" && request.History != 0 || request.Device < 0)
                throw new InvalidDataException("Device benchmark request contains invalid options.");
            var engine = new NativeAudioEngine(request.Library, request.Catalog, request.Models, request.Threads, request.Headroom);
            try
            {
                var models = await engine.ConnectAsync(CancellationToken.None);
                var model = models.SingleOrDefault(value => value.Id == request.ModelId)
                    ?? throw new InvalidDataException("Benchmark model is not admitted.");
                var audio = await new AudioInputReader().ReadRecordingAsync(request.Sample, CancellationToken.None);
                if (audio.SampleRate != 16000 || audio.Channels != 1 || audio.Samples.Length > 16000 * 30)
                    throw new InvalidDataException("Benchmark requires at most 30 seconds of public 16 kHz mono audio.");
                var milliseconds = await engine.BenchmarkAsync(model, audio,
                    CreateOptions(request, model), request.Mode, request.History);
                result = new(request.Backend, milliseconds, null);
            }
            finally
            {
                // Worker success is emitted only after reset, native disposal, and engine disposal succeed.
                try { await engine.DisposeAsync(); }
                catch (Exception error) { throw new AggregateException("Device probe teardown failed.", error); }
            }
        }
        catch (Exception error)
        {
            result = ClassifyFailure(request?.Backend ?? NativeBackend.Cpu, error);
        }
        Console.WriteLine(DeviceBenchmark.ResultPrefix + JsonSerializer.Serialize(result, DeviceBenchmarkJson.Default.DeviceBenchmarkResult));
        return result.Working ? 0 : 1;
    }

    internal static RecognitionOptions CreateOptions(DeviceBenchmarkRequest request, AudioModel model)
    {
        if (request.UseDictionary && (model.Family != "kroko_asr" || !model.SupportsCustomDictionary))
            throw new InvalidDataException("Hotword qualification requires the Kroko dictionary adapter.");
        // Benchmark public wording, never place the user's vocabulary in worker arguments or caches.
        return new(CustomDictionary: request.UseDictionary ? "Mother Nature" : "",
            Backend: request.Backend, DeviceIndex: request.Device);
    }

    internal static DeviceBenchmarkResult ClassifyFailure(NativeBackend backend, Exception error)
    {
        var message = error.Message.Length <= 4096 ? error.Message : error.Message[..4096];
        return new(backend, null, message, error is not NativeExecutionException { IsQualificationFatal: false });
    }
}

public sealed partial class NativeAudioEngine
{
    internal Task<double> BenchmarkAsync(AudioModel model, WaveAudio audio, RecognitionOptions options, string mode, int history) =>
        RunExclusiveAsync(() => NativeOperation.Run(() =>
        {
            var decodedBytes = NativeMemory.ValidateAudio(audio, CancellationToken.None);
            if (history > 0 && UsesDiarization(model))
                EnsurePairedSession(model, options, CancellationToken.None);
            else EnsureSession(model, mode, decodedBytes, options, CancellationToken.None, history);
            RecognitionResult Recognize()
            {
                if (mode == "offline")
                {
                    using var request = CreateRequest(options, CancellationToken.None);
                    SetRequestAudio(request, audio);
                    NativeAudioApi.Check(NativeAudioApi.SessionRun(_session!.DangerousGetHandle(), request.DangerousGetHandle(), out var pointer),
                        "benchmark public sample");
                    using var owned = new NativeAudioHandle(pointer, NativeHandleKind.Result);
                    return ReadResult(pointer, model.Family!, CancellationToken.None);
                }
                var channel = Channel.CreateBounded<byte[]>(1);
                var pcm = new byte[audio.Samples.Length * 2];
                for (var index = 0; index < audio.Samples.Length; index++)
                {
                    var sample = (short)Math.Clamp((int)Math.Round(audio.Samples[index] * 32768), short.MinValue, short.MaxValue);
                    pcm[index * 2] = (byte)sample;
                    pcm[index * 2 + 1] = (byte)(sample >> 8);
                }
                channel.Writer.TryWrite(pcm);
                channel.Writer.TryComplete();
                var result = StreamCore(model, channel.Reader, options, null, CancellationToken.None, null,
                    history == 0 ? NativeMemory.MaximumStreamFrames : 16000L * 120 * 60, history).Result;
                if (history > 0 && UsesDiarization(model) && _diarization?.HasSpeakerActivity != true)
                    throw new InvalidDataException("The public sample produced no speaker activity during paired qualification.");
                return result;
            }
            static void Validate(RecognitionResult result)
            {
                var speech = result.SpeechText;
                if (string.IsNullOrWhiteSpace(speech) || !speech.Contains("mother nature", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Public sample recognition did not contain the required Mother Nature speech anchor.");
            }
            Validate(Recognize());
            if (mode == "streaming")
            {
                try { CleanupStream(true); }
                catch (Exception error) { throw new AggregateException("Benchmark warmup reset failed.", error); }
            }
            var stopwatch = Stopwatch.StartNew();
            var measured = Recognize();
            stopwatch.Stop();
            Validate(measured);
            return stopwatch.Elapsed.TotalMilliseconds;
        }, success =>
        {
            try { CleanupOperation(success); }
            catch (Exception error) { throw new AggregateException("Benchmark reset or teardown failed.", error); }
        }), CancellationToken.None);
}
