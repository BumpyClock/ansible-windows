using System.Collections.Concurrent;
using System.Threading.Channels;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class NativeMeetingSourceTests
{
    [SpeakerModelFact("kroko-en-64l-q8"), Trait("Category", "NativeIntegration")]
    public Task MicrophoneSpeechIsLocalAndSilentPlaybackDoesNotBlockLiveText() => VerifySourcesAsync(false);

    [SpeakerModelFact("kroko-en-64l-q8"), Trait("Category", "NativeIntegration")]
    public Task SimultaneousChannelsRetainBothLocalAndRemoteSpeech() => VerifySourcesAsync(true);

    private static async Task VerifySourcesAsync(bool includePlayback)
    {
        var paths = SpeakerModelFactAttribute.Paths();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var engine = new NativeAudioEngine(paths.Library, paths.Catalog, paths.Models);
        var model = Assert.Single(await engine.ConnectAsync(timeout.Token), model => model.Id == "kroko-en-64l-q8");
        var pcm = await new AudioInputReader().ReadReplayAsync(paths.Sample, timeout.Token);
        var audio = Channel.CreateBounded<MeetingAudioPacket>(2);
        var updates = new ConcurrentQueue<TranscriptUpdate>();
        var live = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new ProgressSink(update =>
        {
            updates.Enqueue(update);
            if (!update.IsFinal && update.SpeakerTurns?.Any(turn => turn.Source == MeetingAudioSource.Microphone &&
                turn.Text.Contains("mother nature", StringComparison.OrdinalIgnoreCase)) == true)
                live.TrySetResult();
        });
        var options = new RecognitionOptions(Backend: NativeBackend.Cpu);
        await engine.PrepareMeetingAsync(model, options, timeout.Token);
        var recognition = engine.StreamMeetingAsync(model, audio.Reader, options, progress, timeout.Token);
        try
        {
            await audio.Writer.WriteAsync(new(pcm, includePlayback ? pcm : new byte[pcm.Length]), timeout.Token);
            var first = await Task.WhenAny(live.Task, recognition).WaitAsync(timeout.Token);
            if (first == recognition) await recognition;
            Assert.Same(live.Task, first);
            Assert.False(recognition.IsCompleted);
        }
        finally
        {
            audio.Writer.TryComplete();
            await recognition;
        }

        var final = Assert.Single(updates, update => update.IsFinal);
        Assert.Equal(pcm.Length / 2L, final.ProcessedSamples);
        var finalTurns = Assert.IsAssignableFrom<IReadOnlyList<MeetingTurn>>(final.SpeakerTurns);
        var local = finalTurns.Where(turn => turn.Source == MeetingAudioSource.Microphone).ToArray();
        Assert.NotEmpty(local);
        Assert.All(local, turn => Assert.Equal("You", turn.SpeakerId));
        Assert.Contains("mother nature", string.Join(" ", local.Select(turn => turn.Text)), StringComparison.OrdinalIgnoreCase);
        var remote = finalTurns.Where(turn => turn.Source == MeetingAudioSource.Playback).ToArray();
        if (includePlayback)
        {
            Assert.NotEmpty(remote);
            Assert.Contains("mother nature", string.Join(" ", remote.Select(turn => turn.Text)), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(remote, turn => turn.SpeakerId.StartsWith("Speaker ", StringComparison.Ordinal));
        }
        else Assert.Empty(remote);
        Assert.DoesNotContain(finalTurns, turn => turn.Source == MeetingAudioSource.Mixed);
        Assert.All(finalTurns, turn => Assert.False(turn.IsProvisional));
    }

    private sealed class ProgressSink(Action<TranscriptUpdate> report) : IProgress<TranscriptUpdate>
    {
        public void Report(TranscriptUpdate update) => report(update);
    }
}
