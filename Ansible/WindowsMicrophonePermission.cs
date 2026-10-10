using Ansible.Core;
using System.Diagnostics;
using System.Runtime.Versioning;
using Windows.Devices.Enumeration;
using Windows.Foundation.Metadata;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace Ansible;

internal sealed class WindowsMicrophonePermission : IMicrophonePermission
{
    public MicrophonePermissionStatus CheckAccess()
    {
        try
        {
            if (HasCapabilityApi) return Map(AppCapability.Create("microphone").CheckAccess());
            return DeviceAccessInformation.CreateFromDeviceClass(DeviceClass.AudioCapture).CurrentStatus switch
            {
                DeviceAccessStatus.Allowed => MicrophonePermissionStatus.Allowed,
                DeviceAccessStatus.DeniedByUser => MicrophonePermissionStatus.DeniedByUser,
                DeviceAccessStatus.DeniedBySystem => MicrophonePermissionStatus.DeniedBySystem,
                _ => MicrophonePermissionStatus.Unavailable
            };
        }
        catch (Exception error) when (IsExpectedInteropFailure(error))
        {
            Debug.WriteLine($"Ansible: microphone permission check failed, HRESULT 0x{error.HResult:X8}.");
            return MicrophonePermissionStatus.Unavailable;
        }
    }

    public async Task<MicrophonePermissionStatus> RequestAccessAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasCapabilityApi) return CheckAccess();
        try
        {
            // Begin on the caller's UI thread, and join the actual WinRT operation after cancellation.
            var operation = AppCapability.Create("microphone").RequestAccessAsync();
            using var registration = cancellationToken.Register(() =>
            {
                try { operation.Cancel(); }
                catch (System.Runtime.InteropServices.COMException) { } // Still join the operation below.
                catch (ObjectDisposedException) { }
            });
            var result = await operation.AsTask();
            cancellationToken.ThrowIfCancellationRequested();
            return Map(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (IsExpectedInteropFailure(error))
        {
            Debug.WriteLine($"Ansible: microphone permission request failed, HRESULT 0x{error.HResult:X8}.");
            return MicrophonePermissionStatus.Unavailable;
        }
    }

    private static bool IsExpectedInteropFailure(Exception error) => error is
        System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException;

    [SupportedOSPlatformGuard("windows10.0.18362")]
    private static bool HasCapabilityApi => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362) &&
        ApiInformation.IsTypePresent("Windows.Security.Authorization.AppCapabilityAccess.AppCapability");

    [SupportedOSPlatform("windows10.0.18362")]
    private static MicrophonePermissionStatus Map(AppCapabilityAccessStatus status) => status switch
    {
        AppCapabilityAccessStatus.Allowed => MicrophonePermissionStatus.Allowed,
        AppCapabilityAccessStatus.UserPromptRequired => MicrophonePermissionStatus.PromptRequired,
        AppCapabilityAccessStatus.DeniedByUser => MicrophonePermissionStatus.DeniedByUser,
        AppCapabilityAccessStatus.DeniedBySystem => MicrophonePermissionStatus.DeniedBySystem,
        AppCapabilityAccessStatus.NotDeclaredByApp => MicrophonePermissionStatus.NotDeclared,
        _ => MicrophonePermissionStatus.Unavailable
    };
}
