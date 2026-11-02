namespace Ansible.Core.Windows;

internal sealed class MeetingCaptureMetrics(MeetingCaptureRequest request)
{
    internal const double SignalThreshold = 0.001;
    private readonly Source _microphone = new();
    private readonly Source _playback = new();

    internal void ObservePacket(bool microphone, MeetingCapturePacket packet, double elapsedSeconds,
        MeetingPacketTiming? timing = null)
    {
        var source = microphone ? _microphone : _playback;
        source.Packets++;
        source.Samples += packet.Pcm.Length / 2;
        if ((packet.Flags & MeetingCapturePacket.Silent) != 0) source.SilentSamples += packet.Pcm.Length / 2;
        source.LastPacket = elapsedSeconds;
        source.LastTiming = timing;
        var rms = AudioMeter.Pcm16Level(packet.Pcm);
        source.Peak = Math.Max(source.Peak, rms);
        source.WindowInput = Math.Max(source.WindowInput, rms);
        if (rms >= SignalThreshold) source.LastSignal = elapsedSeconds;
    }

    internal void ObserveMix(int samples, int microphoneSamples, int playbackSamples,
        double microphoneRms, double playbackRms)
    {
        _microphone.MissingSamples += samples - microphoneSamples;
        _playback.MissingSamples += samples - playbackSamples;
        _microphone.WindowMixed = Math.Max(_microphone.WindowMixed, microphoneRms);
        _playback.WindowMixed = Math.Max(_playback.WindowMixed, playbackRms);
    }

    internal MeetingCaptureHealth Snapshot(double elapsedSeconds, string? diagnosticsError,
        int outputDevices = 0, int skippedOutputDevices = 0, long outputDevicesLost = 0) =>
        new(elapsedSeconds, outputDevices, skippedOutputDevices, request.ProcessId,
            _microphone.Snapshot(elapsedSeconds), _playback.Snapshot(elapsedSeconds), diagnosticsError)
            { OutputDevicesLost = outputDevicesLost };

    private sealed class Source
    {
        internal long Packets, Samples, SilentSamples, MissingSamples;
        internal double Peak, WindowInput, WindowMixed;
        internal double? LastPacket, LastSignal;
        internal MeetingPacketTiming? LastTiming;

        internal MeetingAudioSourceHealth Snapshot(double elapsed)
        {
            var result = new MeetingAudioSourceHealth(Packets, Samples, SilentSamples, MissingSamples,
                Peak, WindowInput, WindowMixed,
                LastPacket is { } packet ? Math.Max(0, elapsed - packet) : null,
                LastSignal is { } signal ? Math.Max(0, elapsed - signal) : null) { LastPacketTiming = LastTiming };
            WindowInput = WindowMixed = 0;
            return result;
        }
    }
}
