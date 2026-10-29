using Ansible.Core;
using Ansible.Core.Windows;
using System.Runtime.InteropServices;

namespace Ansible.Tests;

public sealed class MeetingCaptureTests
{
    [Fact]
    public void MixedAudioSaturatesAndPreservesSingleSource()
    {
        byte[] microphone = [0x30, 0x75, 0x00, 0x80, 0xD2, 0x04];
        byte[] playback = [0x30, 0x75, 0x00, 0x80, 0, 0];
        Assert.Equal(new byte[] { 0xFF, 0x7F, 0x00, 0x80, 0xD2, 0x04 }, MeetingPcmMixer.Mix(microphone, playback, out _, out _));
    }

    [Fact]
    public void MissingAudioProducesSilence()
    {
        Assert.Equal(new byte[8], MeetingPcmMixer.Mix(new byte[8], new byte[8], out _, out _));
    }

    [Fact]
    public void OutputDevicesAreSummedWithSaturation()
    {
        var total = new byte[6];
        MeetingPcmMixer.Accumulate(total, [0x30, 0x75, 0x00, 0x80, 0x10, 0x00]);
        MeetingPcmMixer.Accumulate(total, [0x30, 0x75, 0x00, 0x80, 0x05, 0x00]);
        Assert.Equal(new byte[] { 0xFF, 0x7F, 0x00, 0x80, 0x15, 0x00 }, total);
        Assert.Throws<ArgumentException>(() => MeetingPcmMixer.Accumulate(total, new byte[4]));
    }

    [Fact]
    public void LateOutputTimelineStartsAtTheSharedCursor()
    {
        var timeline = new MeetingPacketTimeline(1000);
        timeline.Add(996, [1, 0, 2, 0, 3, 0, 4, 0, 5, 0, 6, 0], 1100);
        Assert.Equal(1000, timeline.Cursor);
        Assert.Equal(new byte[] { 5, 0, 6, 0, 0, 0 }, timeline.Read(3, out var captured));
        Assert.Equal(2, captured);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeetingPacketTimeline(-1));
    }

    [Fact]
    public void SpeakerNamingProcessIsOptional()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeetingCaptureRequest(0).Validate());
        new MeetingCaptureRequest().Validate();
        new MeetingCaptureRequest(42).Validate();
    }

    [Fact, Trait("Category", "NativeIntegration")]
    public async Task AudioAndMicrophoneStartProducePcmAndRelease()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var capture = await new MeetingCaptureFactory().StartAsync(new(), timeout.Token);
        using var tone = new MeetingTestTone();
        var playbackMixedRms = 0.0;
        capture.Activity += activity => playbackMixedRms = Math.Max(playbackMixedRms, activity.PlaybackRms);
        var maximumToneAmplitude = 0.0;
        for (var index = 0; index < 50; index++)
        {
            byte[] bytes;
            try { bytes = await capture.Audio.ReadAsync(timeout.Token); }
            catch (System.Threading.Channels.ChannelClosedException error)
            {
                throw new Xunit.Sdk.XunitException($"{error.InnerException?.Message}\n" +
                    System.Text.Json.JsonSerializer.Serialize(capture.Health, LocalDiagnosticJson.Default.MeetingCaptureHealth));
            }
            Assert.Equal(1280, bytes.Length);
            double sine = 0, cosine = 0;
            for (var sampleIndex = 0; sampleIndex < bytes.Length / 2; sampleIndex++)
            {
                var sample = (short)(bytes[sampleIndex * 2] | bytes[sampleIndex * 2 + 1] << 8) / 32768.0;
                var phase = 2 * Math.PI * 440 * sampleIndex / 16000;
                sine += sample * Math.Sin(phase);
                cosine += sample * Math.Cos(phase);
            }
            maximumToneAmplitude = Math.Max(maximumToneAmplitude,
                2 * Math.Sqrt(sine * sine + cosine * cosine) / (bytes.Length / 2));
        }
        Assert.True(maximumToneAmplitude > 0.01, "The generated 440 Hz tone must reach the meeting capture stream.");
        Assert.True(capture.ClockSamplePosition >= capture.CapturedSeconds * 16000);
        await capture.StopAsync();
        var stoppedPosition = capture.ClockSamplePosition;
        long flushedSamples = 0;
        await foreach (var packet in capture.Audio.ReadAllAsync(timeout.Token)) flushedSamples += packet.Length / 2;
        Assert.True(flushedSamples > 0, "Stop must emit pending holdback audio.");
        Assert.Equal(stoppedPosition, (long)Math.Round(capture.CapturedSeconds * 16000));
        await Task.Delay(20, timeout.Token);
        Assert.Equal(stoppedPosition, capture.ClockSamplePosition);
        Assert.True(capture.IsReleased);
        var health = Assert.IsType<MeetingCaptureHealth>(capture.Health);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(health, LocalDiagnosticJson.Default.MeetingCaptureHealth));
        Assert.True(health.Playback.Packets > 0, "Playback must supply real packets, not only synthetic mixer silence.");
        Assert.True(health.Playback.PeakInputRms > 0.001, "Playback itself must contain a signal; microphone pickup is not sufficient.");
        Assert.True(playbackMixedRms > 0.001, "Playback signal must survive the timestamp mixer.");
    }
}

internal sealed unsafe partial class MeetingTestTone : IDisposable
{
    private nint _device, _samples, _header;
    internal MeetingTestTone()
    {
        var format = new Format { Tag = 1, Channels = 1, Rate = 16000, Bytes = 32000, Align = 2, Bits = 16 };
        Check(Open(out _device, uint.MaxValue, in format, 0, 0, 0));
        _samples = Marshal.AllocHGlobal(32000);
        for (var index = 0; index < 16000; index++)
            ((short*)_samples)[index] = (short)(Math.Sin(2 * Math.PI * 440 * index / 16000) * 1000);
        _header = Marshal.AllocHGlobal(sizeof(Header));
        *(Header*)_header = new Header { Data = _samples, Length = 32000 };
        Check(Prepare(_device, _header, (uint)sizeof(Header)));
        Check(Write(_device, _header, (uint)sizeof(Header)));
    }
    public void Dispose()
    {
        Check(Reset(_device));
        Check(Unprepare(_device, _header, (uint)sizeof(Header)));
        Check(Close(_device));
        Marshal.FreeHGlobal(_header);
        Marshal.FreeHGlobal(_samples);
    }
    private static void Check(uint result) => Assert.Equal(0u, result);
    [StructLayout(LayoutKind.Sequential, Pack = 2)] private struct Format
    {
        internal ushort Tag, Channels;
        internal uint Rate, Bytes;
        internal ushort Align, Bits, Extra;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Header
    {
        internal nint Data;
        internal uint Length, Recorded;
        internal nuint User;
        internal uint Flags, Loops;
        internal nint Next;
        internal nuint Reserved;
    }
    [LibraryImport("winmm.dll", EntryPoint = "waveOutOpen")] private static partial uint Open(out nint device, uint id, in Format format, nint callback, nuint instance, uint flags);
    [LibraryImport("winmm.dll", EntryPoint = "waveOutPrepareHeader")] private static partial uint Prepare(nint device, nint header, uint size);
    [LibraryImport("winmm.dll", EntryPoint = "waveOutWrite")] private static partial uint Write(nint device, nint header, uint size);
    [LibraryImport("winmm.dll", EntryPoint = "waveOutReset")] private static partial uint Reset(nint device);
    [LibraryImport("winmm.dll", EntryPoint = "waveOutUnprepareHeader")] private static partial uint Unprepare(nint device, nint header, uint size);
    [LibraryImport("winmm.dll", EntryPoint = "waveOutClose")] private static partial uint Close(nint device);
}
