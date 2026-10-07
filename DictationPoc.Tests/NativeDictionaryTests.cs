using System.Reflection;
using System.Threading.Channels;
using DictationPoc.Core;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace DictationPoc.Tests;

public sealed class NativeDictionaryTests(ITestOutputHelper output)
{
    [Theory]
    [Trait("Category", "NativeIntegration")]
    [InstalledDictionaryModel("vibevoice-streaming-1.5b-q4", "vibevoice-asr-streaming-1.5b-q4_k.gguf",
        "vibevoice_asr_streaming", "streaming", false)]
    [InstalledDictionaryModel("vibevoice-asr-7b-q8", "vibevoice-asr-q8_0.gguf",
        "vibevoice_asr", "offline", false)]
    [InstalledDictionaryModel("qwen3-asr-0.6b-q8", "qwen3-asr-0.6b-q8_0.gguf",
        "qwen3_asr", "streaming", true)]
    public async Task RecognizesPublicSampleWithNonEmptyDictionary(
        string modelId, string filename, string family, string mode, bool plainSpeech)
    {
        var root = FindWorkspace();
        var directory = ModelsDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var catalogPath = Path.Combine(root, "tools", "audio-models.json");
        var entries = await NativeModelCatalog.LoadAsync(catalogPath, timeout.Token);
        var entry = Assert.Single(entries, candidate => candidate.Id == modelId);
        Assert.Equal(filename, entry.Filename);
        Assert.Equal(family, entry.Family);
        Assert.Equal(mode, entry.Mode);
        Assert.True(entry.SupportsCustomDictionary);
        await using var engine = new NativeAudioEngine(
            Path.Combine(root, ".runtime", "native", "audiocpp.dll"),
            catalogPath, directory, threads: 2);
        var models = await engine.ConnectAsync(timeout.Token);
        var model = Assert.Single(models, candidate => candidate.Id == modelId);
        Assert.True(model.SupportsCustomDictionary);
        Assert.Equal(plainSpeech, model.HasPlainSpeechText);
        Assert.False(model.EmitsAuthoritativeLiveSpeech);
        var options = new RecognitionOptions(CustomDictionary: "Mother Nature\nUnited States");
        var recording = Path.Combine(root, ".runtime", "validation", "sample_16k.wav");
        var input = new AudioInputReader();
        var decoded = await input.ReadRecordingAsync(recording, timeout.Token);
        var wav = await engine.TranscribeAsync(model, decoded, options, null, timeout.Token);
        AssertResult(wav, plainSpeech);
        output.WriteLine($"WAV succeeded: model={modelId}; verified bytes={entry.Bytes}; SHA256={entry.Sha256}; dictionary nonempty=true; authoritative speech={wav.SpeechText is not null}.");
        if (mode == "streaming")
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var audio = Channel.CreateBounded<byte[]>(1);
            await audio.Writer.WriteAsync(await input.ReadReplayAsync(recording, timeout.Token), timeout.Token);
            audio.Writer.Complete();
            var live = await engine.StreamAsync(model, audio.Reader, options, null, timeout.Token);
            AssertResult(live, plainSpeech);
            output.WriteLine($"Stream succeeded: model={modelId}; authoritative speech={live.SpeechText is not null}; spoken words={live.SpokenWords?.ToString() ?? "unknown"}.");
        }
    }

    private static void AssertResult(RecognitionResult result, bool plainSpeech)
    {
        Assert.False(string.IsNullOrWhiteSpace(result.DisplayText));
        if (plainSpeech)
            Assert.False(string.IsNullOrWhiteSpace(result.SpeechText));
        if (result.SpeechText is null)
            Assert.Null(result.SpokenWords);
        else
            Assert.Equal(AudioMeter.CountWords(result.SpeechText), result.SpokenWords);
    }

    private static string ModelsDirectory(string root) =>
        Environment.GetEnvironmentVariable("DICTATION_MODELS_DIRECTORY") ?? Path.Combine(root, ".runtime", "models");

    private static string FindWorkspace()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tools", "audio-models.json")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Run dictionary integration tests from the POC workspace.");
    }

    public sealed class InstalledDictionaryModelAttribute : DataAttribute
    {
        private readonly object[] _data;

        public InstalledDictionaryModelAttribute(string modelId, string filename, string family, string mode, bool plainSpeech)
        {
            _data = [modelId, filename, family, mode, plainSpeech];
            var path = Path.Combine(ModelsDirectory(FindWorkspace()), filename);
            if (!File.Exists(path))
                Skip = $"Native dictionary qualification unavailable for {modelId}: model weights are not installed at '{path}'. No download is performed.";
        }

        public override IEnumerable<object[]> GetData(MethodInfo testMethod) => [_data];
    }
}
