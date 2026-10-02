using System.Text;
using DictationPoc.Core;

namespace DictationPoc.Tests;

public sealed class WaveAudioTests
{
    [Fact]
    public void KeepsStereoFramesAndSampleRateForNativeFileRecognition()
    {
        var path = WriteWave(1, 2, 48000, 16, [0, 128, 255, 127, 0, 0, 0, 64]);
        try
        {
            var audio = WaveAudio.Read(path);
            Assert.Equal(2, audio.Channels);
            Assert.Equal(48000, audio.SampleRate);
            Assert.Equal([-1f, 32767 / 32768f, 0f, 0.5f], audio.Samples);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadsUnsignedPcm8WithoutChangingSilence()
    {
        var path = WriteWave(1, 1, 16000, 8, [0, 128, 255, 128]);
        try
        {
            Assert.Equal([-1f, 0f, 127 / 128f, 0f], WaveAudio.Read(path).Samples);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RejectsNonFiniteFloatingPointAudio()
    {
        var path = WriteWave(3, 1, 16000, 32, BitConverter.GetBytes(float.NaN));
        try
        {
            var error = Assert.Throws<InvalidDataException>(() => WaveAudio.Read(path));
            Assert.Contains("non-finite", error.Message);
        }
        finally { File.Delete(path); }
    }

    private static string WriteWave(ushort format, ushort channels, uint rate, ushort bits, byte[] samples)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wave-test-" + Guid.NewGuid().ToString("N") + ".wav");
        using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write(format);
        writer.Write(channels);
        writer.Write(rate);
        writer.Write(checked((uint)(rate * channels * (bits / 8))));
        writer.Write((ushort)(channels * (bits / 8)));
        writer.Write(bits);
        writer.Write("data"u8);
        writer.Write(samples.Length);
        writer.Write(samples);
        return path;
    }
}
