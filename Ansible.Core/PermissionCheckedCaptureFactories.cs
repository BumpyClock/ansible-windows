namespace Ansible.Core;

public sealed class PermissionCheckedAudioCaptureFactory(IAudioCaptureFactory inner, MicrophonePermissionPreflight permission)
    : IAudioCaptureFactory
{
    private readonly IAudioCaptureFactory _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly MicrophonePermissionPreflight _permission = permission ?? throw new ArgumentNullException(nameof(permission));

    public Task<IAudioCapture> StartAsync(CancellationToken cancellationToken, int microphoneBoostDecibels = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _permission.RequireAccess();
        return _inner.StartAsync(cancellationToken, microphoneBoostDecibels);
    }
}

public sealed class PermissionCheckedMeetingCaptureFactory(IMeetingCaptureFactory inner, MicrophonePermissionPreflight permission)
    : IMeetingCaptureFactory
{
    private readonly IMeetingCaptureFactory _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly MicrophonePermissionPreflight _permission = permission ?? throw new ArgumentNullException(nameof(permission));

    public Task<IMeetingCapture> StartAsync(MeetingCaptureRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _permission.RequireAccess();
        return _inner.StartAsync(request, cancellationToken);
    }
}
