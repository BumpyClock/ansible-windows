using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace DictationPoc.Core;

internal interface INativeModuleLoader
{
    nint Load(string path);
    uint ReadAbi(nint handle);
    void RegisterResolver(Func<string, nint> resolve);
    void Free(nint handle);
}

internal sealed class NativeModule(INativeModuleLoader loader)
{
    private readonly object _gate = new();
    private nint _handle;
    private string? _path;
    private ExceptionDispatchInfo? _terminalFailure;

    public void Initialize(string path)
    {
        lock (_gate)
        {
            _terminalFailure?.Throw();
            path = Path.GetFullPath(path);
            if (_handle != 0)
            {
                if (!string.Equals(_path, path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("One audio.cpp native library version can be used per application process.");
                return;
            }

            var candidate = loader.Load(path);
            try
            {
                var version = loader.ReadAbi(candidate);
                if (version >> 16 != 0 || (version & 0xFFFF) < 0x0200)
                    throw new NotSupportedException("This application requires the audio.cpp C ABI 0.2 or newer in major version 0.");

                try
                {
                    loader.RegisterResolver(name => name == "audiocpp" ? Volatile.Read(ref _handle) : 0);
                }
                catch (Exception error)
                {
                    // The assembly resolver cannot be unregistered. Do not retry an uncertain registration.
                    _terminalFailure = ExceptionDispatchInfo.Capture(error);
                    throw;
                }
                _path = path;
                Volatile.Write(ref _handle, candidate);
                candidate = 0;
            }
            finally
            {
                if (candidate != 0)
                    loader.Free(candidate);
            }
        }
    }
}

internal sealed class NativeModuleLoader : INativeModuleLoader
{
    public nint Load(string path)
    {
        NativeCpu.RequireSupported();
        if (!File.Exists(path))
            throw new FileNotFoundException("Build the native backend with tools\\Build-AudioNative.ps1, then publish the app.", path);
        return NativeLibrary.Load(path, typeof(NativeAudioApi).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
    }

    public unsafe uint ReadAbi(nint handle) =>
        ((delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(handle, "audiocpp_abi_version"))();

    public void RegisterResolver(Func<string, nint> resolve) =>
        NativeLibrary.SetDllImportResolver(typeof(NativeAudioApi).Assembly, (name, _, _) => resolve(name));

    public void Free(nint handle) => NativeLibrary.Free(handle);
}

internal static class NativeCpu
{
    public static void RequireSupported()
    {
        var f16c = X86Base.IsSupported && (X86Base.CpuId(1, 0).Ecx & (1 << 29)) != 0;
        Validate(OperatingSystem.IsWindows(), RuntimeInformation.ProcessArchitecture == Architecture.X64,
            Avx2.IsSupported, Fma.IsSupported, Avx.IsSupported && f16c, Bmi1.IsSupported, Bmi2.IsSupported);
    }

    internal static void Validate(bool windows, bool x64, bool avx2, bool fma, bool f16c, bool bmi1 = true, bool bmi2 = true)
    {
        if (!windows || !x64 || !avx2 || !fma || !f16c || !bmi1 || !bmi2)
            throw new PlatformNotSupportedException(
                "The native CPU backend requires Windows x64 with AVX2, FMA, F16C, BMI1/BMI2 and OS-enabled AVX state. ARM64 and x86 are not qualified.");
    }
}
