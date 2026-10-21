using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Ansible.Core;
using Xunit.Abstractions;

namespace Ansible.Tests;

public sealed class NativeSpeakerModelTests(ITestOutputHelper output)
{
    [Fact]
    public void CancelledConstructionDoesNotTouchNativeRuntimeOrWeights()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new NativeDiarizationSession(
            0, null!, null!, null!, new(), 4, 512, "nemotron_asr", cancellation.Token));
    }

    [SpeakerModelFact("nemotron-asr-0.6b-q8"), Trait("Category", "NativeIntegration")]
    public Task NemotronPairedCpuStreamProducesLiveSpeechAndFinalTimedSpeakers() =>
        QualifyAsync("nemotron-asr-0.6b-q8", "");

    [SpeakerModelFact("kroko-en-64l-q8"), Trait("Category", "NativeIntegration")]
    public Task KrokoPairedCpuStreamProducesLiveSpeechAndFinalTimedSpeakers() =>
        QualifyAsync("kroko-en-64l-q8", "Mother Nature\nUnited States");

    private async Task QualifyAsync(string modelId, string dictionary)
    {
        var paths = SpeakerModelFactAttribute.Paths();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var engine = new NativeAudioEngine(paths.Library, paths.Catalog, paths.Models, threads: 4);
        var models = await engine.ConnectAsync(timeout.Token);
        var model = Assert.Single(models, candidate => candidate.Id == modelId);
        Assert.Contains(models, candidate => candidate.Id == AudioModelDefaults.DiarizationModelId && candidate.Task == "diar");
        var pcm = await new AudioInputReader().ReadReplayAsync(paths.Sample, timeout.Token);
        var audio = Channel.CreateBounded<byte[]>(2);
        var live = new TaskCompletionSource<TranscriptUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = new List<TranscriptUpdate>();
        var progress = new InlineProgress(update =>
        {
            updates.Add(update);
            if (!update.IsFinal && !string.IsNullOrWhiteSpace(update.SpeechText)) live.TrySetResult(update);
        });
        var timer = Stopwatch.StartNew();
        var options = new RecognitionOptions(CustomDictionary: dictionary, Backend: NativeBackend.Cpu);
        await engine.PrepareMeetingAsync(model, options, timeout.Token);
        var recognition = engine.StreamMeetingAsync(model, audio.Reader, options, progress, timeout.Token);
        try
        {
            await audio.Writer.WriteAsync(pcm, timeout.Token);
            if (await Task.WhenAny(live.Task, recognition).WaitAsync(timeout.Token) == recognition)
            {
                await recognition;
                Assert.Fail("Paired recognition finished without publishing speech while input was open.");
            }
            var partial = await live.Task.WaitAsync(timeout.Token);
            Assert.False(partial.IsFinal);
            Assert.NotEmpty(partial.SpeakerTurns!);
            Assert.False(recognition.IsCompleted);
        }
        finally
        {
            audio.Writer.TryComplete();
            await recognition;
        }

        var final = Assert.Single(updates, update => update.IsFinal);
        Assert.Same(final, updates[^1]);
        Assert.Equal(final.SpeechText, final.Text);
        Assert.Contains("mother nature", final.SpeechText!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(pcm.Length / 2L, final.ProcessedSamples);
        Assert.NotNull(final.SpeakerTurns);
        Assert.NotEmpty(final.SpeakerTurns);
        Assert.Contains(final.SpeakerTurns, turn => turn.SpeakerId.StartsWith("Speaker ", StringComparison.Ordinal));
        var partials = updates.Where(update => !update.IsFinal && !string.IsNullOrWhiteSpace(update.SpeechText)).ToArray();
        Assert.True(partials.Length >= 2, "Qualification requires multiple cumulative live snapshots.");
        Assert.True(partials[^1].ProcessedSamples > partials[0].ProcessedSamples);
        long previousSamples = 0;
        foreach (var update in updates)
        {
            Assert.NotNull(update.ProcessedSamples);
            var processed = update.ProcessedSamples.Value;
            Assert.InRange(processed, previousSamples, pcm.Length / 2L);
            previousSamples = processed;
            Assert.NotNull(update.SpeakerTurns);
            for (var index = 0; index < update.SpeakerTurns.Count; index++)
            {
                var turn = update.SpeakerTurns[index];
                Assert.InRange(turn.StartSeconds, 0, processed / 16000d);
                Assert.InRange(turn.EndSeconds, turn.StartSeconds, processed / 16000d);
                Assert.Equal(!update.IsFinal && index == update.SpeakerTurns.Count - 1, turn.IsProvisional);
                Assert.False(string.IsNullOrWhiteSpace(turn.Text));
                Assert.True(turn.SpeakerId == "Unknown speaker" || turn.SpeakerId is
                    "Speaker 0" or "Speaker 1" or "Speaker 2" or "Speaker 3" or
                    "Speaker 4" or "Speaker 5" or "Speaker 6" or "Speaker 7");
            }
        }
        // Reusing the paired engine for ordinary PCM streaming must not retain meeting labels.
        var dictation = Channel.CreateBounded<byte[]>(1);
        Assert.True(dictation.Writer.TryWrite(pcm));
        dictation.Writer.TryComplete();
        var plainUpdates = new List<TranscriptUpdate>();
        var plain = await engine.StreamAsync(model, dictation.Reader, options,
            new InlineProgress(plainUpdates.Add), timeout.Token);
        Assert.False(string.IsNullOrWhiteSpace(plain.SpeechText));
        Assert.All(plainUpdates, update => Assert.Null(update.SpeakerTurns));

        var wave = await new AudioInputReader().ReadRecordingAsync(paths.Sample, timeout.Token);
        plainUpdates.Clear();
        var file = await engine.TranscribeAsync(model, wave, options, new InlineProgress(plainUpdates.Add), timeout.Token);
        Assert.False(string.IsNullOrWhiteSpace(file.SpeechText));
        Assert.All(plainUpdates, update => Assert.Null(update.SpeakerTurns));
        output.WriteLine($"{modelId}: CPU paired stream passed; {partials.Length} live snapshots; " +
            $"{final.SpeakerTurns.Count} final turns; ordinary PCM/WAV remained unlabeled. " +
            $"Unpaced public sample, not a latency or speaker-accuracy qualification ({timer.Elapsed.TotalSeconds:F1}s test elapsed).");
    }

    private sealed class InlineProgress(Action<TranscriptUpdate> report) : IProgress<TranscriptUpdate>
    {
        public void Report(TranscriptUpdate update) => report(update);
    }
}

public sealed class SpeakerModelFactAttribute : FactAttribute
{
    public SpeakerModelFactAttribute(string modelId)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Skip = "Native speaker integration requires Windows x64.";
            return;
        }
        var paths = Paths();
        var missing = new List<string>();
        if (!File.Exists(paths.Library)) missing.Add(paths.Library);
        if (!File.Exists(paths.Sample)) missing.Add(paths.Sample);
        // Missing assets skip; malformed catalogs, corrupt weights and native failures must fail the test.
        var entries = NativeModelCatalog.LoadAsync(paths.Catalog).GetAwaiter().GetResult();
        foreach (var id in new[] { modelId, AudioModelDefaults.DiarizationModelId })
        {
            var entry = entries.Single(value => value.Id == id);
            var path = Path.Combine(paths.Models, entry.Filename);
            if (!File.Exists(path)) missing.Add(path);
        }
        if (missing.Count > 0) Skip = "Native integration assets absent: " + string.Join("; ", missing);
    }

    internal static (string Library, string Catalog, string Models, string Sample) Paths()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tools", "audio-models.json")))
            directory = directory.Parent;
        var root = directory?.FullName
            ?? throw new DirectoryNotFoundException("Run speaker model qualification in the application workspace.");
        return (
            Path.Combine(Environment.GetEnvironmentVariable("DICTATION_NATIVE_DIRECTORY")
                ?? Path.Combine(root, ".runtime", "native"), "audiocpp.dll"),
            Path.Combine(root, "tools", "audio-models.json"),
            Environment.GetEnvironmentVariable("DICTATION_MODELS_DIRECTORY") ?? Path.Combine(root, ".runtime", "models"),
            Environment.GetEnvironmentVariable("DICTATION_SAMPLE_PATH") ?? Path.Combine(root, ".runtime", "validation", "sample_16k.wav"));
    }
}
