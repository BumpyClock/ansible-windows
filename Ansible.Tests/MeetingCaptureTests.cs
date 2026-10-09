using Ansible.Core;
using Ansible.Core.Windows;
using System.Runtime.InteropServices;

namespace Ansible.Tests;

public sealed class MeetingCaptureTests
{
    [Fact]
    public void MixedAudioSaturatesAndPreservesSingleSource()
    {
        var microphone = new Queue<byte>(new byte[] { 0x30, 0x75, 0x00, 0x80, 0xD2, 0x04 });
        var playback = new Queue<byte>(new byte[] { 0x30, 0x75, 0x00, 0x80 });
        Assert.Equal(new byte[] { 0xFF, 0x7F, 0x00, 0x80, 0xD2, 0x04 }, MeetingPcmMixer.Mix(microphone, playback, 3));
    }

    [Fact]
    public void MissingAudioProducesSilence()
    {
        Assert.Equal(new byte[8], MeetingPcmMixer.Mix(new(), new(), 4));
    }

    [Fact]
    public void MeetingApplicationRequiresSelectedProcess()
    {
        Assert.Throws<ArgumentException>(() => new MeetingCaptureRequest(MeetingPlaybackSource.MeetingApp).Validate());
        new MeetingCaptureRequest(MeetingPlaybackSource.SystemAudio).Validate();
    }

    [Theory, InlineData(false), InlineData(true), Trait("Category", "NativeIntegration")]
    public async Task AudioAndMicrophoneStartProducePcmAndRelease(bool processOnly)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var capture = await new MeetingCaptureFactory().StartAsync(
            new(processOnly ? MeetingPlaybackSource.MeetingApp : MeetingPlaybackSource.SystemAudio,
                processOnly ? Environment.ProcessId : null), timeout.Token);
        using var tone = new MeetingTestTone();
        var maximumToneAmplitude = 0.0;
        for (var index = 0; index < 50; index++)
        {
            var bytes = await capture.Audio.ReadAsync(timeout.Token);
            Assert.Equal(640, bytes.Length);
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
        await capture.StopAsync();
        Assert.True(capture.IsReleased);
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

