using System.Threading.Channels;

namespace Ansible.Core;

internal static class DictationAudioBuffer
{
    internal static async Task<WaveAudio> ReadAsync(ChannelReader<byte[]> audio, CancellationToken token)
    {
        using var recording = new MemoryStream();
        await foreach (var packet in audio.ReadAllAsync(token))
        {
            token.ThrowIfCancellationRequested();
            if (packet is null || packet.Length == 0 || packet.Length % 2 != 0)
                throw new InvalidDataException("Dictation requires complete PCM16 samples.");
            if (recording.Length + packet.Length > NativeMemory.MaximumStreamFrames * sizeof(short))
                throw new InvalidDataException("Dictation audio exceeds the five-minute limit.");
            recording.Write(packet);
        }
        token.ThrowIfCancellationRequested();
        if (recording.Length == 0)
            throw new InvalidDataException("Dictation contains no audio samples.");
        var bytes = recording.GetBuffer();
        var samples = new float[checked((int)recording.Length / sizeof(short))];
        for (var index = 0; index < samples.Length; index++)
        {
            if (index % 16000 == 0) { token.ThrowIfCancellationRequested(); }
            samples[index] = (short)(bytes[index * 2] | bytes[index * 2 + 1] << 8) / 32768f;
        }
        token.ThrowIfCancellationRequested();
        return new WaveAudio(samples, 16000, 1);
    }
}
