using System.Diagnostics;

namespace Ansible.Core.Windows;

internal sealed record MeetingCapturePacket(byte[] Pcm, ulong QpcPosition100Nanoseconds, ulong DevicePosition, uint Flags = 0)
{
    internal const uint Discontinuity = 1, Silent = 2, TimestampError = 4;
}

internal sealed class MeetingPacketClock
{
    private long _nextSample = -1;
    private ulong _lastDevicePosition;
    private int _lastSamples;
    private bool _hasPacket;
    internal long? ExpectedSample => _hasPacket ? _nextSample : null;
    internal ulong? PreviousDevicePosition => _hasPacket ? _lastDevicePosition : null;
    internal long Map(long timestampSample, ulong devicePosition, int samples)
    {
        var start = timestampSample;
        if (_hasPacket)
        {
            if (devicePosition != 0 && _lastDevicePosition != 0 && devicePosition < _lastDevicePosition)
                throw new IOException("Meeting capture returned a regressing device position.");
            // Some devices report native-rate positions. Use the counter only when it advances by the PCM packet length.
            if (devicePosition > _lastDevicePosition && devicePosition - _lastDevicePosition == (ulong)_lastSamples)
                start = _nextSample;
            else if (Math.Abs(timestampSample - _nextSample) <= 80)
                start = _nextSample;
        }
        _nextSample = checked(start + samples);
        _lastDevicePosition = devicePosition;
        _lastSamples = samples;
        _hasPacket = true;
        return start;
    }
}

internal static class MeetingCaptureClock
{
    internal const int SampleRate = 16000;
    internal static long Now100Nanoseconds => checked((long)((UInt128)(ulong)Stopwatch.GetTimestamp()
        * 10000000 / (ulong)Stopwatch.Frequency));
    internal static long Samples(long clock, long origin) => checked((long)Math.Round(
        checked(clock - origin) * (SampleRate / 10000000.0), MidpointRounding.AwayFromZero));
}

internal sealed class MeetingPacketTimeline
{
    internal const int CapacitySamples = 32000;
    private readonly short[] _samples = new short[CapacitySamples];
    private readonly long[] _positions = new long[CapacitySamples];
    private long _cursor;
    private long _lastEnd = -1;

    internal MeetingPacketTimeline(long cursor = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);
        _cursor = cursor;
        Array.Fill(_positions, -1);
    }
    internal long Cursor => _cursor;

    internal void Add(long start, ReadOnlySpan<byte> pcm, long clockPosition)
    {
        if (pcm.Length == 0 || pcm.Length % 2 != 0 || pcm.Length / 2 > CapacitySamples)
            throw new InvalidDataException("Meeting capture returned an invalid PCM16 packet size.");
        var count = pcm.Length / 2;
        if (start < -1600 || start > clockPosition + 320)
            throw new IOException("Meeting capture returned an invalid packet timestamp.");
        if (_lastEnd >= 0)
        {
            if (start < _lastEnd - 1) throw new IOException($"Meeting capture returned overlapping packet timestamps (start {start}, previous end {_lastEnd}, packet samples {count}, clock {clockPosition}).");
            // A one-sample difference is rounding at the 16 kHz timestamp boundary.
            if (Math.Abs(start - _lastEnd) <= 1) start = _lastEnd;
        }
        var end = checked(start + count);
        // A source that joins mid-meeting may deliver its first packet slightly before audio already emitted.
        var floor = _lastEnd < 0 ? _cursor : 0;
        if (end <= floor) return;
        if (start < floor)
        {
            pcm = pcm[checked((int)(floor - start) * 2)..];
            start = floor;
            count = pcm.Length / 2;
        }
        if (start < _cursor) throw new IOException("Meeting capture delivered audio after its timestamp had already been emitted.");
        if (end > _cursor + CapacitySamples) throw new IOException("Meeting capture exceeded its two-second timestamp buffer.");
        for (var index = 0; index < count; index++)
        {
            var position = start + index;
            var slot = (int)(position % CapacitySamples);
            if (_positions[slot] >= _cursor) throw new IOException("Meeting capture attempted to overwrite pending audio.");
            _positions[slot] = position;
            _samples[slot] = (short)(pcm[index * 2] | pcm[index * 2 + 1] << 8);
        }
        _lastEnd = end;
    }

    internal byte[] Read(int count) => Read(count, out _);

    internal byte[] Read(int count, out int capturedSamples)
    {
        if (count is < 1 or > CapacitySamples) throw new ArgumentOutOfRangeException(nameof(count));
        capturedSamples = 0;
        var pcm = new byte[count * 2];
        for (var index = 0; index < count; index++)
        {
            var position = _cursor + index;
            var slot = (int)(position % CapacitySamples);
            if (_positions[slot] != position) continue;
            capturedSamples++;
            var sample = _samples[slot];
            pcm[index * 2] = (byte)sample;
            pcm[index * 2 + 1] = (byte)(sample >> 8);
            _positions[slot] = -1;
        }
        _cursor += count;
        return pcm;
    }
}
