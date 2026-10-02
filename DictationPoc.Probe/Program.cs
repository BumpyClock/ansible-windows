using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using DictationPoc.Core;

if (args.Length is < 4 or > 6 || args.Length == 6 && (args[5] != "--file-only" || args[4] == "--file-only"))
{
    Console.Error.WriteLine("Usage: DictationPoc.Probe <native-dll> <models-directory> <catalog-json> <16k-mono-PCM16.wav> [model-id] [--file-only]");
    return 2;
}

var clock = Stopwatch.StartNew();
var stage = "native initialization";
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    await using var engine = new NativeAudioEngine(args[0], args[2], args[1], threads: 4, memoryHeadroomMB: 512);
    var models = await engine.ConnectAsync(timeout.Token);
    var fileOnly = args[^1] == "--file-only";
    var model = args.Length >= 5 && args[4] != "--file-only"
        ? models.FirstOrDefault(model => model.Id == args[4]) ??
            throw new ArgumentException($"Model '{args[4]}' is not installed in the native catalog.")
        : models.FirstOrDefault(model => model.Mode == "streaming") ??
            models.FirstOrDefault() ?? throw new InvalidDataException("No ASR models are installed.");
    Console.WriteLine($"Native DLL={Path.GetFullPath(args[0])}; available models={string.Join(", ", models.Select(model => model.Id))}");
    Console.WriteLine($"Selected model={model.Id}; mode={model.Mode ?? "not reported"}; family={model.Family}; path={model.ModelPath}");
    var pcm = ReadPcmWave(args[3]);

    stage = $"WAV transcription ({model.Id})";
    Console.WriteLine($"Attempting {stage}; timeout=180 seconds.");
    clock.Restart();
    timeout.CancelAfter(TimeSpan.FromMinutes(3));
    var transcript = await engine.TranscribeFileAsync(
        model, args[3], null, null, timeout.Token);
    if (string.IsNullOrWhiteSpace(transcript))
    {
        throw new InvalidDataException("The native engine returned no speech for the validation recording.");
    }
    Console.WriteLine($"WAV transcription completed in {clock.Elapsed.TotalMilliseconds:F0} ms: {transcript}");
    if (model.Mode == "offline" || fileOnly)
    {
        return 0;
    }

    stage = $"live stream ({model.Id})";
    Console.WriteLine($"Attempting {stage}; timeout=180 seconds.");
    var audio = Channel.CreateBounded<byte[]>(8);
    var uploadFinished = false;
    var previewsDuringUpload = 0;
    clock.Restart();
    timeout.CancelAfter(TimeSpan.FromMinutes(3));
    var streaming = engine.StreamAsync(model, audio.Reader, null,
        new InlineProgress<TranscriptUpdate>(update =>
        {
            if (!update.IsFinal && !Volatile.Read(ref uploadFinished))
            {
                Interlocked.Increment(ref previewsDuringUpload);
            }
        }), timeout.Token);
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
        if (string.IsNullOrWhiteSpace(liveTranscript))
        {
            throw new InvalidDataException("The native live stream returned no speech.");
        }
        Console.WriteLine($"Live stream completed in {clock.Elapsed.TotalMilliseconds:F0} ms; previews during upload={previewsDuringUpload}");
        Console.WriteLine(liveTranscript);
    }
    finally
    {
        audio.Writer.TryComplete();
        timeout.Cancel();
        try { await streaming; }
        catch { /* The original failure is reported below. */ }
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"{stage} failed after {clock.Elapsed.TotalMilliseconds:F0} ms: {error.GetType().Name}: {error.Message}");
    return 1;
}

static byte[] ReadPcmWave(string path)
{
    using var reader = new BinaryReader(File.OpenRead(path), Encoding.ASCII);
    if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
    {
        throw new InvalidDataException("Expected a RIFF WAV recording.");
    }
    reader.ReadUInt32();
    if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
    {
        throw new InvalidDataException("Expected a WAV recording.");
    }
    var validFormat = false;
    while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
    {
        var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
        var size = reader.ReadUInt32();
        var next = reader.BaseStream.Position + size + (size & 1);
        if (next > reader.BaseStream.Length)
        {
            throw new InvalidDataException("Truncated WAV chunk.");
        }
        if (id == "fmt ")
        {
            if (size < 16)
            {
                throw new InvalidDataException("Invalid WAV format.");
            }
            var format = reader.ReadUInt16();
            var channels = reader.ReadUInt16();
            var rate = reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt16();
            var bits = reader.ReadUInt16();
            validFormat = format == 1 && channels == 1 && rate == 16000 && bits == 16;
        }
        else if (id == "data")
        {
            if (!validFormat || size % 2 != 0)
            {
                throw new InvalidDataException("The live probe requires 16 kHz mono PCM16 WAV.");
            }
            return reader.ReadBytes(checked((int)size));
        }
        reader.BaseStream.Position = next;
    }
    throw new InvalidDataException("WAV recording has no audio data.");
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
