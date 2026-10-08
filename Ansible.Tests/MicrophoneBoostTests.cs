using Ansible.Core;

namespace Ansible.Tests;

public sealed class MicrophoneBoostTests
{
    [Theory]
    [InlineData(0, 1000)]
    [InlineData(6, 1995)]
    [InlineData(12, 3981)]
    [InlineData(24, 15849)]
    public void BoostAmplifiesBothPolarities(int decibels, short expected)
    {
        byte[] samples = [0xe8, 0x03, 0x18, 0xfc]; // +1000, -1000
        new MicrophoneBoost(decibels).Apply(samples);

        Assert.Equal(4, samples.Length);
        Assert.Equal(expected, System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(samples));
        Assert.Equal(-expected, System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(2)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(25)]
    public void InvalidBoostIsRejected(int decibels) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MicrophoneBoost(decibels));

    [Theory]
    [InlineData(0)]
    [InlineData(24)]
    public void PartialSamplesAreRejected(int decibels) =>
        Assert.Throws<ArgumentException>(() => new MicrophoneBoost(decibels).Apply(new byte[] { 0 }));
}
