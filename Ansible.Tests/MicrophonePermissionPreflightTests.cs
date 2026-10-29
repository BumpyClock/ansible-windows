using Ansible.Core;

namespace Ansible.Tests;

public sealed class MicrophonePermissionPreflightTests
{
    [Fact]
    public async Task PromptRequiredRequestsOnceOnCallingThreadAndThenAllowsCapture()
    {
        var permission = new FakePermission { Current = MicrophonePermissionStatus.PromptRequired };
        var preflight = new MicrophonePermissionPreflight(permission);
        var thread = Environment.CurrentManagedThreadId;
        Assert.Equal(MicrophonePermissionStatus.Allowed, await preflight.InitializeAsync());
        Assert.Equal(MicrophonePermissionStatus.Allowed, await preflight.InitializeAsync());
        Assert.Equal(thread, permission.RequestThread);
        Assert.Equal(1, permission.Requests);
        preflight.RequireAccess();
    }

    [Fact]
    public async Task RelaunchWithGrantedPermissionDoesNotRequestAgain()
    {
        var permission = new FakePermission { Current = MicrophonePermissionStatus.Allowed };
        var preflight = new MicrophonePermissionPreflight(permission);
        Assert.Equal(MicrophonePermissionStatus.Allowed, await preflight.InitializeAsync());
        preflight.RequireAccess();
        Assert.Equal(0, permission.Requests);
    }

    [Theory]
    [InlineData(MicrophonePermissionStatus.DeniedByUser)]
    [InlineData(MicrophonePermissionStatus.DeniedBySystem)]
    [InlineData(MicrophonePermissionStatus.NotDeclared)]
    [InlineData(MicrophonePermissionStatus.Unavailable)]
    public async Task DeniedOrUnavailableAccessNeverPromptsAndPreventsCapture(MicrophonePermissionStatus status)
    {
        var permission = new FakePermission { Current = status };
        var preflight = new MicrophonePermissionPreflight(permission);
        Assert.Equal(status, await preflight.InitializeAsync());
        Assert.Throws<InvalidOperationException>(preflight.RequireAccess);
        Assert.Equal(0, permission.Requests);
    }

    [Fact]
    public async Task RevokedPermissionIsRecheckedWithoutPromptingAtCaptureStart()
    {
        var permission = new FakePermission { Current = MicrophonePermissionStatus.Allowed };
        var preflight = new MicrophonePermissionPreflight(permission);
        await preflight.InitializeAsync();
        permission.Current = MicrophonePermissionStatus.DeniedByUser;
        var error = Assert.Throws<InvalidOperationException>(preflight.RequireAccess);
        Assert.Contains("Privacy & security > Microphone", error.Message);
        Assert.Equal(MicrophonePermissionStatus.DeniedByUser, preflight.Status);
        permission.Current = MicrophonePermissionStatus.Allowed;
        preflight.RequireAccess();
        Assert.Equal(0, permission.Requests);
    }

    [Fact]
    public async Task PendingLaunchRequestBlocksCaptureAndAllCallersJoinSameOwnedRequest()
    {
        var completion = new TaskCompletionSource<MicrophonePermissionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var permission = new FakePermission { Current = MicrophonePermissionStatus.PromptRequired, Pending = completion.Task };
        var preflight = new MicrophonePermissionPreflight(permission);
        Assert.Equal(MicrophonePermissionStatus.Unavailable, preflight.Status);
        Assert.Throws<InvalidOperationException>(preflight.RequireAccess);
        var first = preflight.InitializeAsync();
        var second = preflight.InitializeAsync();
        Assert.Same(first, second);
        Assert.False(first.IsCompleted);
        Assert.Throws<InvalidOperationException>(preflight.RequireAccess);
        Assert.Equal(1, permission.Requests);
        completion.SetResult(MicrophonePermissionStatus.Allowed);
        await first;
        preflight.RequireAccess();
    }

    [Fact]
    public async Task PassiveAccessRefreshReflectsSettingsChangeWithoutPrompting()
    {
        var permission = new FakePermission { Current = MicrophonePermissionStatus.DeniedByUser };
        var preflight = new MicrophonePermissionPreflight(permission);
        await preflight.InitializeAsync();
        permission.Current = MicrophonePermissionStatus.Allowed;
        Assert.Equal(MicrophonePermissionStatus.Allowed, preflight.CheckAccess());
        Assert.Equal(MicrophonePermissionStatus.Allowed, preflight.Status);
        preflight.RequireAccess();
        Assert.Equal(0, permission.Requests);
    }

    [Fact]
    public async Task PassiveAccessRefreshCannotBypassLaunchOrPendingRequest()
    {
        var completion = new TaskCompletionSource<MicrophonePermissionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var permission = new FakePermission { Current = MicrophonePermissionStatus.Allowed, Pending = completion.Task };
        var preflight = new MicrophonePermissionPreflight(permission);
        Assert.Equal(MicrophonePermissionStatus.Allowed, preflight.CheckAccess());
        Assert.Throws<InvalidOperationException>(preflight.RequireAccess);
        permission.Current = MicrophonePermissionStatus.PromptRequired;
        var initialize = preflight.InitializeAsync();
        permission.Current = MicrophonePermissionStatus.Allowed;
        preflight.CheckAccess();
        Assert.False(initialize.IsCompleted);
        Assert.Throws<InvalidOperationException>(preflight.RequireAccess);
        Assert.Equal(1, permission.Requests);
        completion.SetResult(MicrophonePermissionStatus.Allowed);
        await initialize;
        preflight.RequireAccess();
    }

    [Fact]
    public async Task ExplicitRetryRechecksPrivacySettings()
    {
        var permission = new FakePermission { Current = MicrophonePermissionStatus.DeniedByUser };
        var preflight = new MicrophonePermissionPreflight(permission);
        await preflight.InitializeAsync();
        permission.Current = MicrophonePermissionStatus.Allowed;
        Assert.Equal(MicrophonePermissionStatus.Allowed, await preflight.InitializeAsync());
        preflight.RequireAccess();
        Assert.Equal(0, permission.Requests);
    }

    [Fact]
    public async Task BothCaptureFactoriesBlockInnerCaptureUntilAllowedAndAfterRevocation()
    {
        var permission = new FakePermission { Current = MicrophonePermissionStatus.DeniedByUser };
        var preflight = new MicrophonePermissionPreflight(permission);
        var inner = new FakeCaptureFactory();
        var audio = new PermissionCheckedAudioCaptureFactory(inner, preflight);
        var meeting = new PermissionCheckedMeetingCaptureFactory(inner, preflight);
        var request = new MeetingCaptureRequest();
        await Assert.ThrowsAsync<InvalidOperationException>(() => audio.StartAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => meeting.StartAsync(request, default));
        await preflight.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => audio.StartAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => meeting.StartAsync(request, default));
        Assert.Equal(0, inner.Starts);
        permission.Current = MicrophonePermissionStatus.Allowed;
        await preflight.InitializeAsync();
        Assert.Same(inner.Capture, await audio.StartAsync(default, 6));
        Assert.Equal(6, inner.Boost);
        Assert.Same(inner.Capture, await meeting.StartAsync(request, default));
        Assert.Same(request, inner.Request);
        Assert.Equal(2, inner.Starts);
        permission.Current = MicrophonePermissionStatus.DeniedBySystem;
        await Assert.ThrowsAsync<InvalidOperationException>(() => audio.StartAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => meeting.StartAsync(request, default));
        Assert.Equal(2, inner.Starts);
        Assert.Equal(0, permission.Requests);
    }

    [Fact]
    public async Task CancelledRequestRemainsOwnedUntilAdapterCompletes()
    {
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<MicrophonePermissionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var permission = new FakePermission { Current = MicrophonePermissionStatus.PromptRequired, Pending = completion.Task };
        var preflight = new MicrophonePermissionPreflight(permission);
        var initialize = preflight.InitializeAsync(cancellation.Token);
        cancellation.Cancel();
        Assert.False(initialize.IsCompleted);
        Assert.Throws<InvalidOperationException>(preflight.RequireAccess);
        completion.SetCanceled(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialize);
        Assert.Equal(MicrophonePermissionStatus.Unavailable, preflight.Status);
        Assert.Throws<InvalidOperationException>(preflight.RequireAccess);
        Assert.Equal(1, permission.Requests);
    }

    private sealed class FakePermission : IMicrophonePermission
    {
        public MicrophonePermissionStatus Current { get; set; }
        public Task<MicrophonePermissionStatus>? Pending { get; init; }
        public int Requests { get; private set; }
        public int RequestThread { get; private set; }
        public MicrophonePermissionStatus CheckAccess() => Current;
        public async Task<MicrophonePermissionStatus> RequestAccessAsync(CancellationToken cancellationToken)
        {
            Requests++;
            RequestThread = Environment.CurrentManagedThreadId;
            Current = Pending is null ? MicrophonePermissionStatus.Allowed : await Pending;
            return Current;
        }
    }

    private sealed class FakeCaptureFactory : IAudioCaptureFactory, IMeetingCaptureFactory
    {
        public FakeMeetingCapture Capture { get; } = new();
        public int Starts { get; private set; }
        public int Boost { get; private set; }
        public MeetingCaptureRequest? Request { get; private set; }
        public Task<IAudioCapture> StartAsync(CancellationToken cancellationToken, int microphoneBoostDecibels = 0)
        { Starts++; Boost = microphoneBoostDecibels; return Task.FromResult<IAudioCapture>(Capture); }
        public Task<IMeetingCapture> StartAsync(MeetingCaptureRequest request, CancellationToken cancellationToken)
        { Starts++; Request = request; return Task.FromResult<IMeetingCapture>(Capture); }
    }
}
