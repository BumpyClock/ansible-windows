using System.Diagnostics;
using System.Threading.Channels;
using Ansible.Core;
using Xunit.Abstractions;

namespace Ansible.Tests;

public sealed class NativeSpeakerModelTests(ITestOutputHelper output)
{
    [Fact]
    public async Task KrokoRejectsSlashDictionaryPhrasesBeforeNativeWork()
    {
        await using var engine = new NativeAudioEngine("missing.dll", "missing.json", "models");
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.TranscribeAsync(
            new() { Id = "kroko", Family = "kroko_asr", SupportsCustomDictionary = true },
            new([0f], 16000, 1), new(CustomDictionary: "and/or", Backend: NativeBackend.Cpu), null, CancellationToken.None));
    }

    [Theory, Trait("Category", "NativeIntegration")]
    [InlineData("nemotron-asr-0.6b-q8", "")]
    [InlineData("kroko-en-64l-q8", "Mother Nature\nUnited States")]
    public async Task PairedCpuStreamProducesLiveSpeechAndFinalTimedSpeakers(string modelId, string dictionary)
    {
        var root = FindWorkspace();
        var directory = Environment.GetEnvironmentVariable("DICTATION_MODELS_DIRECTORY") ?? Path.Combine(root, ".runtime", "models");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var engine = new NativeAudioEngine(Path.Combine(root, ".runtime", "native", "audiocpp.dll"),
            Path.Combine(root, "tools", "audio-models.json"), directory, threads: 4);
        var models = await engine.ConnectAsync(timeout.Token);
        var model = Assert.Single(models, candidate => candidate.Id == modelId);
        Assert.Contains(models, candidate => candidate.Id == AudioModelDefaults.DiarizationModelId && candidate.Task == "diar");
        var pcm = await new AudioInputReader().ReadReplayAsync(
            Path.Combine(root, ".runtime", "validation", "sample_16k.wav"), timeout.Token);
        var audio = Channel.CreateBounded<byte[]>(2);
        var live = new TaskCompletionSource<TranscriptUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        TranscriptUpdate? final = null;
        var progress = new InlineProgress(update =>
        {
            if (update.IsFinal) final = update;
            else if (!string.IsNullOrWhiteSpace(update.SpeechText)) live.TrySetResult(update);
        });
        var timer = Stopwatch.StartNew();
        var options = new RecognitionOptions(CustomDictionary: dictionary, Backend: NativeBackend.Cpu);
        await engine.PrepareMeetingAsync(model, options, timeout.Token);
        var loadedSeconds = timer.Elapsed.TotalSeconds;
        var recognition = engine.StreamMeetingAsync(model, audio.Reader, options, progress, timeout.Token);
        try
        {
            await audio.Writer.WriteAsync(pcm, timeout.Token);
            var winner = await Task.WhenAny(live.Task, recognition).WaitAsync(timeout.Token);
            if (winner == recognition)
            {
                await recognition;
                Assert.Fail("Paired recognition finished before publishing live speech.");
            }
            var partial = await live.Task.WaitAsync(timeout.Token);
            Assert.False(partial.IsFinal);
            Assert.NotNull(partial.SpeakerTurns);
            Assert.False(recognition.IsCompleted);
            output.WriteLine($"{modelId} load={loadedSeconds:F3}s first speech={timer.Elapsed.TotalSeconds:F3}s input={pcm.Length / 32000.0:F3}s");
        }
        finally
        {
            audio.Writer.TryComplete();
            await recognition;
        }
        Assert.NotNull(final);
        Assert.Contains("mother nature", final.SpeechText!, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(final.SpeakerTurns);
        Assert.NotEmpty(final.SpeakerTurns);
        Assert.Contains(final.SpeakerTurns, turn => turn.SpeakerId.StartsWith("Speaker ", StringComparison.Ordinal));
        Assert.All(final.SpeakerTurns, turn =>
        {
            Assert.False(turn.IsProvisional);
            Assert.InRange(turn.StartSeconds, 0, pcm.Length / 32000.0);
            Assert.InRange(turn.EndSeconds, turn.StartSeconds, pcm.Length / 32000.0);
        });
        output.WriteLine($"{modelId} total={timer.Elapsed.TotalSeconds:F3}s working set={Process.GetCurrentProcess().WorkingSet64 / 1048576.0:F1}MiB turns={final.SpeakerTurns.Count}; dictionary={dictionary.Length > 0}");
    }

    private static string FindWorkspace()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tools", "audio-models.json"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Run speaker model qualification in the application workspace.");
    }

    private sealed class InlineProgress(Action<TranscriptUpdate> report) : IProgress<TranscriptUpdate>
    {
        public void Report(TranscriptUpdate update) => report(update);
    }
}
