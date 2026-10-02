using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

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
