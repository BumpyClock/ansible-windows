using System.Threading.Channels;
using DictationPoc.Core;

namespace DictationPoc.Tests;

public sealed class NativeStreamingDeliveryTests
{
    [Fact]
    [Trait("Category", "NativeIntegration")]
    public async Task NemotronEmitsAuthoritativeSpeechWhileAudioInputIsStillOpen()
    {
        var root = FindWorkspace();
        var directory = Environment.GetEnvironmentVariable("DICTATION_MODELS_DIRECTORY")
            ?? Path.Combine(root, ".runtime", "models");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var engine = new NativeAudioEngine(Path.Combine(root, ".runtime", "native", "audiocpp.dll"),
            Path.Combine(root, "tools", "audio-models.json"), directory, threads: 2);
        var models = await engine.ConnectAsync(timeout.Token);
        var model = Assert.Single(models, candidate => candidate.Id == "nemotron-asr-0.6b-q8");
        Assert.True(model.EmitsAuthoritativeLiveSpeech);
        var audio = Channel.CreateUnbounded<byte[]>();
        var firstSpeech = new TaskCompletionSource<TranscriptUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? lastLiveSpeech = null;
        var progress = new InlineProgress(update =>
        {
            if (!update.IsFinal && !string.IsNullOrWhiteSpace(update.SpeechText))
            {
                lastLiveSpeech = update.SpeechText;
                firstSpeech.TrySetResult(update);
            }
        });
        var recognition = engine.StreamAsync(model, audio.Reader, new(), progress, timeout.Token);
        RecognitionResult final;
        try
        {
            var pcm = await new AudioInputReader().ReadReplayAsync(
                Path.Combine(root, ".runtime", "validation", "sample_16k.wav"), timeout.Token);
            await audio.Writer.WriteAsync(pcm, timeout.Token);
            var update = await firstSpeech.Task.WaitAsync(timeout.Token);
            Assert.False(recognition.IsCompleted);
            Assert.False(update.IsFinal);
            Assert.False(string.IsNullOrWhiteSpace(update.SpeechText));
            var cursor = new LiveTextCursor();
            Assert.Equal(update.SpeechText, cursor.Pending(update.SpeechText));
            cursor.ConfirmSent(update.SpeechText);
        }
        finally
        {
            audio.Writer.TryComplete();
            final = await recognition;
        }

        // The pinned Nemotron streaming adapter is append-only: the authoritative final speech extends the last
        // published live partial. Guard that contract so a regression that revises already-typed text is caught.
        Assert.NotNull(lastLiveSpeech);
        Assert.NotNull(final.SpeechText);
        Assert.StartsWith(lastLiveSpeech!, final.SpeechText!, StringComparison.Ordinal);
        var replay = new LiveTextCursor();
        replay.ConfirmSent(lastLiveSpeech!);
        Assert.Equal(final.SpeechText![lastLiveSpeech!.Length..], replay.Pending(final.SpeechText!));
    }

    private static string FindWorkspace()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tools", "audio-models.json"))) { return directory.FullName; }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Run the native streaming test in the POC workspace.");
    }

    private sealed class InlineProgress(Action<TranscriptUpdate> report) : IProgress<TranscriptUpdate>
    {
        public void Report(TranscriptUpdate value) => report(value);
    }
}
