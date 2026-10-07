using Ansible.Core;

namespace Ansible.Tests;

[Trait("Category", "NativeBoundary")]
public sealed class NativeCpuTests
{
    private const int Leaf1Ecx = (1 << 12) | (1 << 26) | (1 << 27) | (1 << 28) | (1 << 29);
    private const int Leaf7Ebx = (1 << 3) | (1 << 5) | (1 << 8);

    [Fact]
    public void SupportedHardwareAndWindowsAvxStateAreEligible()
    {
        var features = NativeCpu.Decode(7, Leaf1Ecx, Leaf7Ebx, osAvx: true, osAvx2: true);
        Assert.Equal(new NativeCpuFeatures(true, true, true, true, true, true, true, true, true, true), features);
        Assert.True(features.IsEligible);
        NativeCpu.Validate(features);
    }

    [Theory]
    [InlineData(1, 12)]
    [InlineData(1, 26)]
    [InlineData(1, 27)]
    [InlineData(1, 28)]
    [InlineData(1, 29)]
    [InlineData(7, 3)]
    [InlineData(7, 5)]
    [InlineData(7, 8)]
    public void EveryRequiredHardwareAndXsaveBitIsEnforced(int leaf, int bit)
    {
        var features = NativeCpu.Decode(7,
            leaf == 1 ? Leaf1Ecx & ~(1 << bit) : Leaf1Ecx,
            leaf == 7 ? Leaf7Ebx & ~(1 << bit) : Leaf7Ebx, osAvx: true, osAvx2: true);
        Assert.False(features.IsEligible);
        var error = Assert.Throws<PlatformNotSupportedException>(() => NativeCpu.Validate(features));
        Assert.Contains("Detected hardware and OS features", error.Message);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void HardwareBitsAloneCannotBypassDisabledWindowsAvxState(bool osAvx, bool osAvx2)
    {
        var features = NativeCpu.Decode(7, Leaf1Ecx, Leaf7Ebx, osAvx, osAvx2);
        Assert.True(features.Avx2);
        Assert.False(features.IsEligible);
        Assert.Throws<PlatformNotSupportedException>(() => NativeCpu.Validate(features));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    public void UnavailableCpuidLeavesCannotSupplyFeatureBits(int maximumLeaf)
    {
        var features = NativeCpu.Decode(maximumLeaf, Leaf1Ecx, Leaf7Ebx, osAvx: true, osAvx2: true);
        Assert.False(features.Avx2);
        Assert.False(features.Bmi1);
        Assert.False(features.Bmi2);
        Assert.False(features.IsEligible);
        Assert.Throws<PlatformNotSupportedException>(() => NativeCpu.Validate(features));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void NonWindowsOrNonX64ProcessesRemainUnsupported(bool windows, bool x64) =>
        Assert.Throws<PlatformNotSupportedException>(() => NativeCpu.ValidatePlatform(windows, x64));

    [Fact]
    public void WindowsFeatureNumbersMatchTheSdk()
    {
        Assert.Equal(39u, NativeCpu.ProcessorFeatureAvx);
        Assert.Equal(40u, NativeCpu.ProcessorFeatureAvx2);
    }
}
