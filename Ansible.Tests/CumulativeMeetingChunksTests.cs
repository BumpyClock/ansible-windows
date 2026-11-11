using Ansible.Core;

namespace Ansible.Tests;

public sealed class CumulativeMeetingChunksTests
{
    [Fact]
    public void CumulativeMicrophoneSpeechClosesPriorUpdateBoundariesAndPreservesPunctuation()
    {
        var chunks = new CumulativeMeetingChunks();
        chunks.Update("Hello, world.", 160000, false);
        var turns = chunks.Update("Hello, world. Next!", 320000, true);

        Assert.Equal(["Hello, world.", "Next!"], turns.Select(turn => turn.Text));
        Assert.Equal([0d, 10d], turns.Select(turn => turn.StartSeconds));
        Assert.Equal([10d, 20d], turns.Select(turn => turn.EndSeconds));
        Assert.Equal("Hello, world. Next!", string.Join(" ", turns.Select(turn => turn.Text)));
        Assert.All(turns, turn =>
        {
            Assert.Equal("You", turn.SpeakerId);
            Assert.Equal(MeetingAudioSource.Microphone, turn.Source);
            Assert.Equal(MeetingTimingQuality.Chunk, turn.TimingQuality);
            Assert.False(turn.IsProvisional);
        });
    }

    [Fact]
    public void CumulativeRevisionRetractsOnlyUnstableTail()
    {
        var chunks = new CumulativeMeetingChunks();
        chunks.Update("First phrase", 160000, false);
        var turns = chunks.Update("First revised phrase", 320000, true);

        Assert.Equal(["First", "revised phrase"], turns.Select(turn => turn.Text));
        Assert.Equal("First revised phrase", string.Join(" ", turns.Select(turn => turn.Text)));
    }

    [Fact]
    public void TimedMicrophoneTurnsPreserveQuotedSpeechAndClosingPunctuation()
    {
        const string speech = "“Hello,” she said.";
        var words = new[]
        {
            new SpeechWordTiming(speech.IndexOf("Hello", StringComparison.Ordinal), 5, 0, 1600),
            new SpeechWordTiming(speech.IndexOf("she", StringComparison.Ordinal), 3, 1600, 3200),
            new SpeechWordTiming(speech.IndexOf("said", StringComparison.Ordinal), 4, 3200, 4800)
        };

        var turn = Assert.Single(NativeAudioEngine.BuildMicrophoneTurns(speech, words, true));
        Assert.Equal(speech, turn.Text);
        Assert.False(turn.IsProvisional);
    }
}
