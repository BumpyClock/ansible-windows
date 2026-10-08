using System.Buffers.Binary;

namespace Ansible.Core;

public sealed class MicrophoneBoost
{
    public const int MaxDecibels = 24;
    private readonly double _gain;

    public MicrophoneBoost(int decibels)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decibels);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decibels, MaxDecibels);
        _gain = Math.Pow(10, decibels / 20.0);
    }

    public void Apply(Span<byte> pcm)
    {
        if (pcm.Length % 2 != 0)
            throw new ArgumentException("Microphone boost requires complete PCM16 samples.", nameof(pcm));
        if (_gain == 1) { return; }
        for (var offset = 0; offset < pcm.Length; offset += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[offset..]);
            var boosted = (short)Math.Clamp(Math.Round(sample * _gain), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm[offset..], boosted);
        }
    }
}
