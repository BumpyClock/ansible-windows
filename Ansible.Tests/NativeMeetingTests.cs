using System.Diagnostics;
using System.Threading.Channels;
using Ansible.Core;
using Xunit.Abstractions;

namespace Ansible.Tests;

public sealed class NativeMeetingTests(ITestOutputHelper output)
{
    [Fact, Trait("Category", "NativeIntegration")]
    public async Task NemotronCpuAnchorsUnchangedPublicSamplePhrase()
    {
        var root = FindWorkspace();
        var directory = Environment.GetEnvironmentVariable("DICTATION_MODELS_DIRECTORY")
            ?? Path.Combine(root, ".runtime", "models");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var aligner = new NativeAudioEngine(Path.Combine(root, ".runtime", "native", "audiocpp.dll"),
            Path.Combine(root, "tools", "audio-models.json"), directory);
        var audio = await new AudioInputReader().ReadRecordingAsync(
            Path.Combine(root, ".runtime", "validation", "sample_16k.wav"), timeout.Token);
        var model = (await aligner.ConnectAsync(timeout.Token)).First(candidate => candidate.Family == "nemotron_asr");
        await aligner.PrepareAlignmentAsync(model, timeout.Token);
        var tokens = await aligner.ReadAlignmentTokensAsync(model, audio, timeout.Token);
        var turn = new MeetingTurn("Speaker 0", 0, audio.Samples.Length / 16000.0,
            "some call me nature") { TimingQuality = MeetingTimingQuality.Chunk };
        var result = Assert.Single(MeetingWordAlignment.Align([turn], tokens, audio.Samples.Length / 16000.0, timeout.Token));
        Assert.Equal(turn.Text, result.Text);
        Assert.Equal(turn.SpeakerId, result.SpeakerId);
        Assert.Equal(MeetingTimingQuality.Aligned, result.TimingQuality);
        Assert.True(result.EndSeconds > result.StartSeconds);
        Assert.InRange(result.StartSeconds, 0, turn.EndSeconds);
        Assert.InRange(result.EndSeconds, 0, turn.EndSeconds);
        output.WriteLine($"Unchanged public phrase anchored at {result.StartSeconds:F2}–{result.EndSeconds:F2}s.");
    }

    [Fact, Trait("Category", "NativeIntegration")]
    public async Task VibeVoiceMeetingProducesSpeechBeforeInputEndsAndFinalizes()
    {
        var root = FindWorkspace();
        var directory = Environment.GetEnvironmentVariable("DICTATION_MODELS_DIRECTORY")
            ?? Path.Combine(root, ".runtime", "models");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await using var engine = new NativeAudioEngine(Path.Combine(root, ".runtime", "native", "audiocpp.dll"),
            Path.Combine(root, "tools", "audio-models.json"), directory, threads: 4);
        var models = await engine.ConnectAsync(timeout.Token);
        var model = Assert.Single(models, candidate => candidate.Id == "vibevoice-streaming-1.5b-q4");
        var audio = Channel.CreateBounded<byte[]>(4);
        var firstSpeech = new TaskCompletionSource<TranscriptUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        TranscriptUpdate? final = null;
        var timer = Stopwatch.StartNew();
        var initialMemory = Process.GetCurrentProcess().WorkingSet64;
        var progress = new InlineProgress(update =>
        {
            if (update.IsFinal) final = update;
            else if (!string.IsNullOrWhiteSpace(update.SpeechText)) firstSpeech.TrySetResult(update);
        });
        var recognition = engine.StreamMeetingAsync(model, audio.Reader, new(), progress, timeout.Token);
        try
        {
            var pcm = await new AudioInputReader().ReadReplayAsync(
                Path.Combine(root, ".runtime", "validation", "sample_16k.wav"), timeout.Token);
            await audio.Writer.WriteAsync(pcm, timeout.Token);
            if (await Task.WhenAny(firstSpeech.Task, recognition).WaitAsync(timeout.Token) == recognition)
            {
                await recognition;
                Assert.Fail("Meeting recognition finished before delivering live speech.");
            }
            var update = await firstSpeech.Task.WaitAsync(timeout.Token);
            output.WriteLine($"First speech: {timer.Elapsed.TotalSeconds:F2}s; input: {pcm.Length / 32000.0:F2}s; working set: {Process.GetCurrentProcess().WorkingSet64 / 1048576.0:F1}MiB (initial {initialMemory / 1048576.0:F1}MiB)");
            Assert.False(recognition.IsCompleted);
            Assert.False(update.IsFinal);
        }
        finally { audio.Writer.TryComplete(); await recognition; }
        Assert.NotNull(final);
        Assert.False(string.IsNullOrWhiteSpace(final.SpeechText));
        output.WriteLine($"Finalization: {timer.Elapsed.TotalSeconds:F2}s; speaker label present: {final.Text.Contains("Speaker", StringComparison.OrdinalIgnoreCase)}");
    }
    private static string FindWorkspace()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tools", "audio-models.json"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Run this test in the application workspace.");
    }
    private sealed class InlineProgress(Action<TranscriptUpdate> report) : IProgress<TranscriptUpdate>
    {
        public void Report(TranscriptUpdate value) => report(value);
    }
}
