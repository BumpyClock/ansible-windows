namespace Ansible.Core.Windows;

/// <summary>
/// Device boundary for the meeting capture worker. Each thread that calls a device enters the device runtime first and
/// exits it on the same thread. A source opened on one entered thread may be started, read, stopped, and released on another.
/// </summary>
internal interface IMeetingAudioDevices
{
    IDisposable EnterThread();
    IMeetingAudioSource OpenMicrophone(Action<MeetingCaptureStage> stage);
    IMeetingOutputCatalog OpenOutputCatalog();
}

/// <summary>Active playback endpoints. Endpoint IDs stay in memory to match devices across rescans; never log them.</summary>
internal interface IMeetingOutputCatalog : IDisposable
{
    IReadOnlyList<string> ActiveIds();
    IMeetingAudioSource OpenLoopback(string id, Action<MeetingCaptureStage> stage);
}

/// <summary>One initialized 16 kHz mono PCM16 stream. <see cref="Read"/> returns null when no packet is ready.</summary>
internal interface IMeetingAudioSource : IDisposable
{
    void Start();
    void Stop();
    MeetingCapturePacket? Read();
}

internal sealed class WasapiMeetingAudioDevices : IMeetingAudioDevices
{
    internal static readonly WasapiMeetingAudioDevices Instance = new();
    private WasapiMeetingAudioDevices() { }

    public IDisposable EnterThread()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Meeting capture requires Windows.");
        MeetingWasapi.Check(MeetingWasapi.CoInitializeEx(0, 0));
        return new ComApartment();
    }

    public IMeetingAudioSource OpenMicrophone(Action<MeetingCaptureStage> stage) =>
        Open(stage, MeetingCaptureStage.OpenMicrophone, MeetingCaptureStage.InitializeMicrophone,
            MeetingCaptureStage.MicrophoneService, () => MeetingWasapi.OpenDefault(1), loopback: false);

    public IMeetingOutputCatalog OpenOutputCatalog() => new OutputCatalog(MeetingWasapi.CreateEnumerator());

    private static Source Open(Action<MeetingCaptureStage> stage, MeetingCaptureStage open, MeetingCaptureStage initialize,
        MeetingCaptureStage service, Func<nint> activate, bool loopback)
    {
        nint client = 0, capture = 0;
        try
        {
            stage(open);
            client = activate();
            stage(initialize);
            MeetingWasapi.Initialize(client, loopback);
            stage(service);
            capture = MeetingWasapi.CaptureService(client);
            return new Source(client, capture);
        }
        catch
        {
            MeetingWasapi.Release(capture);
            MeetingWasapi.Release(client);
            throw;
        }
    }

    private sealed class ComApartment : IDisposable
    {
        private bool _exited;
        public void Dispose()
        {
            if (_exited) return;
            _exited = true;
            MeetingWasapi.CoUninitialize();
        }
    }

    private sealed class OutputCatalog(nint enumerator) : IMeetingOutputCatalog
    {
        private nint _enumerator = enumerator;
        public IReadOnlyList<string> ActiveIds() => MeetingWasapi.ActiveRenderEndpointIds(Enumerator);
        public IMeetingAudioSource OpenLoopback(string id, Action<MeetingCaptureStage> stage) =>
            Open(stage, MeetingCaptureStage.OpenPlayback, MeetingCaptureStage.InitializePlayback,
                MeetingCaptureStage.PlaybackService, () => MeetingWasapi.OpenEndpoint(Enumerator, id), loopback: true);
        public void Dispose()
        {
            MeetingWasapi.Release(_enumerator);
            _enumerator = 0;
        }
        private nint Enumerator => _enumerator != 0 ? _enumerator : throw new ObjectDisposedException(nameof(OutputCatalog));
    }

    private sealed class Source(nint client, nint capture) : IMeetingAudioSource
    {
        private nint _client = client, _capture = capture;
        public void Start() => MeetingWasapi.Start(Client);
        public void Stop() => MeetingWasapi.Stop(Client);
        public MeetingCapturePacket? Read() => MeetingWasapi.Read(_capture != 0 ? _capture : throw new ObjectDisposedException(nameof(Source)));
        public void Dispose()
        {
            MeetingWasapi.Release(_capture);
            _capture = 0;
            MeetingWasapi.Release(_client);
            _client = 0;
        }
        private nint Client => _client != 0 ? _client : throw new ObjectDisposedException(nameof(Source));
    }
}
