using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace Ansible.Core;

internal readonly record struct NativeCpuFeatures(
    bool Avx, bool Avx2, bool Fma, bool F16c, bool Bmi1, bool Bmi2,
    bool Xsave, bool OsXsave, bool OsAvx, bool OsAvx2)
{
    public bool IsEligible => Avx && Avx2 && Fma && F16c && Bmi1 && Bmi2 &&
        Xsave && OsXsave && OsAvx && OsAvx2;
}

internal static partial class NativeCpu
{
    // Windows SDK winnt.h: PF_AVX_INSTRUCTIONS_AVAILABLE and PF_AVX2_INSTRUCTIONS_AVAILABLE.
    internal const uint ProcessorFeatureAvx = 39;
    internal const uint ProcessorFeatureAvx2 = 40;

    public static void RequireSupported() => Validate(ReadFeatures());

    public static NativeCpuFeatures ReadFeatures()
    {
        ValidatePlatform(OperatingSystem.IsWindows(), RuntimeInformation.ProcessArchitecture == Architecture.X64);
        // CPUID is baseline on x64. IsSupported describes managed AOT code, not the native DLL's CPU eligibility.
        var maximumLeaf = X86Base.CpuId(0, 0).Eax;
        var leaf1Ecx = maximumLeaf >= 1 ? X86Base.CpuId(1, 0).Ecx : 0;
        var leaf7Ebx = maximumLeaf >= 7 ? X86Base.CpuId(7, 0).Ebx : 0;
        return Decode(maximumLeaf, leaf1Ecx, leaf7Ebx,
            IsProcessorFeaturePresent(ProcessorFeatureAvx), IsProcessorFeaturePresent(ProcessorFeatureAvx2));
    }

    internal static NativeCpuFeatures Decode(int maximumLeaf, int leaf1Ecx, int leaf7Ebx, bool osAvx, bool osAvx2)
    {
        if (maximumLeaf < 1) leaf1Ecx = 0;
        if (maximumLeaf < 7) leaf7Ebx = 0;
        return new(
            Avx: (leaf1Ecx & (1 << 28)) != 0,
            Avx2: (leaf7Ebx & (1 << 5)) != 0,
            Fma: (leaf1Ecx & (1 << 12)) != 0,
            F16c: (leaf1Ecx & (1 << 29)) != 0,
            Bmi1: (leaf7Ebx & (1 << 3)) != 0,
            Bmi2: (leaf7Ebx & (1 << 8)) != 0,
            Xsave: (leaf1Ecx & (1 << 26)) != 0,
            OsXsave: (leaf1Ecx & (1 << 27)) != 0,
            OsAvx: osAvx,
            OsAvx2: osAvx2);
    }

    internal static void ValidatePlatform(bool windows, bool x64)
    {
        if (!windows || !x64)
            throw new PlatformNotSupportedException("The native CPU backend requires Windows x64. ARM64 and x86 are not qualified.");
    }

    internal static void Validate(NativeCpuFeatures features)
    {
        if (!features.IsEligible)
            throw new PlatformNotSupportedException(
                "The native CPU backend requires Windows x64 with AVX2, FMA, F16C, BMI1/BMI2 and OS-enabled AVX state. " +
                $"Detected hardware and OS features: {features}.");
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessorFeaturePresent(uint processorFeature);
}
