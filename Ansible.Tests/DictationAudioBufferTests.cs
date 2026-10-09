using System.Threading.Channels;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class DictationAudioBufferTests
{
    [Fact]
    public async Task BuffersUntilFinishAndConvertsPcm16WithoutLosingEarlyPackets()
    {
        var audio = Channel.CreateUnbounded<byte[]>();
        audio.Writer.TryWrite([0, 128, 255, 127]);
        var reading = DictationAudioBuffer.ReadAsync(audio.Reader, CancellationToken.None);
        Assert.False(reading.IsCompleted);
        audio.Writer.TryWrite([0, 0, 0, 64]);
        audio.Writer.Complete();
        var recording = await reading;
        Assert.Equal(16000, recording.SampleRate);
        Assert.Equal(1, recording.Channels);
        Assert.Equal(new float[] { -1, 32767 / 32768f, 0, 0.5f }, recording.Samples);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task RejectsIncompletePackets(int size)
    {
        var audio = Channel.CreateUnbounded<byte[]>();
        audio.Writer.TryWrite(new byte[size]);
        audio.Writer.Complete();
        await Assert.ThrowsAsync<InvalidDataException>(() => DictationAudioBuffer.ReadAsync(audio.Reader, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsAudioBeyondFiveMinutes()
    {
        var audio = Channel.CreateUnbounded<byte[]>();
        audio.Writer.TryWrite(new byte[checked((int)NativeMemory.MaximumStreamFrames * 2 + 2)]);
        audio.Writer.Complete();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => DictationAudioBuffer.ReadAsync(audio.Reader, CancellationToken.None));
        Assert.Contains("five-minute", error.Message);
    }

    [Fact]
    public async Task CancellationInterruptsWaitingForFinish()
    {
        var audio = Channel.CreateUnbounded<byte[]>();
        using var cancellation = new CancellationTokenSource();
        var reading = DictationAudioBuffer.ReadAsync(audio.Reader, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
    }

    [Fact]
    public async Task CaptureFailurePropagatesInsteadOfTranscribingPartialAudio()
    {
        var audio = Channel.CreateUnbounded<byte[]>();
        audio.Writer.TryWrite([0, 0]);
        audio.Writer.Complete(new IOException("microphone failed"));
        await Assert.ThrowsAsync<IOException>(() => DictationAudioBuffer.ReadAsync(audio.Reader, CancellationToken.None));
    }
}
