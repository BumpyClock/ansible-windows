namespace Ansible.Core;

public enum MicrophonePermissionStatus
{
    Allowed,
    PromptRequired,
    DeniedByUser,
    DeniedBySystem,
    NotDeclared,
    Unavailable
}

public interface IMicrophonePermission
{
    MicrophonePermissionStatus CheckAccess();

    /// <summary>Complete only after the owned platform request has finished, including cancellation.</summary>
    Task<MicrophonePermissionStatus> RequestAccessAsync(CancellationToken cancellationToken);
}

/// <summary>Checks permission at launch or explicit retry; capture admission never requests permission.</summary>
public sealed class MicrophonePermissionPreflight(IMicrophonePermission permission)
{
    private readonly IMicrophonePermission _permission = permission ?? throw new ArgumentNullException(nameof(permission));
    private readonly object _gate = new();
    private Task<MicrophonePermissionStatus>? _initialization;
    private MicrophonePermissionStatus _status = MicrophonePermissionStatus.Unavailable;
    private bool _initialized;

    public MicrophonePermissionStatus Status { get { lock (_gate) return _status; } }

    /// <summary>Refreshes access without prompting or completing launch initialization.</summary>
    public MicrophonePermissionStatus CheckAccess()
    {
        lock (_gate)
        {
            if (_initialization is { IsCompleted: false }) return _status;
            try { _status = _permission.CheckAccess(); }
            catch { _status = MicrophonePermissionStatus.Unavailable; }
            return _status;
        }
    }

    // Do not dispatch this method: the platform request must begin on its caller's UI thread.
    public Task<MicrophonePermissionStatus> InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_initialization is { IsCompleted: false }) return _initialization;
            _initialized = false;
            return _initialization = InitializeCoreAsync(cancellationToken);
        }
    }

    private async Task<MicrophonePermissionStatus> InitializeCoreAsync(CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var status = _permission.CheckAccess();
            if (status == MicrophonePermissionStatus.PromptRequired)
                status = await _permission.RequestAccessAsync(token);
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _status = status;
                _initialized = true;
            }
            return status;
        }
        catch
        {
            lock (_gate) _status = MicrophonePermissionStatus.Unavailable;
            throw;
        }
    }

    public void RequireAccess()
    {
        lock (_gate)
        {
            if (!_initialized)
                throw new InvalidOperationException("Microphone permission is not ready. Finish the launch permission request before starting recording.");
            // Recheck at every capture start: Windows privacy settings can change after launch.
            CheckAccess();
            if (_status == MicrophonePermissionStatus.Allowed) return;
            throw new InvalidOperationException(_status switch
            {
                MicrophonePermissionStatus.DeniedByUser => "Microphone access is disabled. Enable Ansible in Windows Settings > Privacy & security > Microphone, then try again.",
                MicrophonePermissionStatus.DeniedBySystem => "Windows blocks microphone access. Enable microphone access in Windows Settings > Privacy & security > Microphone, or contact your administrator.",
                MicrophonePermissionStatus.NotDeclared => "This Ansible package does not declare microphone access. Install a package with the microphone capability.",
                MicrophonePermissionStatus.PromptRequired => "Microphone permission has not been granted. Use Check again in Ansible’s microphone notice to request access, then try again.",
                _ => "Microphone permission is unavailable. Check Windows Settings > Privacy & security > Microphone, then use Check again in Ansible’s microphone notice."
            });
        }
    }
}
