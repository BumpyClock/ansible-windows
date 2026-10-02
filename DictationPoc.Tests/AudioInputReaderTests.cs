using System.Buffers.Binary;
using System.Text;
using DictationPoc.Core;

namespace DictationPoc.Tests;

public sealed class AudioInputReaderTests
{
    [Theory]
    [InlineData(1, 8)]
    [InlineData(1, 16)]
    [InlineData(1, 24)]
    [InlineData(1, 32)]
    [InlineData(3, 32)]
    [InlineData(3, 64)]
    public async Task DecodesAsymmetricInterleavedChannels(int format, int bits)
    {
        var payload = (format, bits) switch
        {
            (1, 8) => new byte[] { 0, 192, 128, 96 },
            (1, 16) => new byte[] { 0, 128, 0, 64, 0, 0, 0, 224 },
            (1, 24) => new byte[] { 0, 0, 128, 0, 0, 64, 0, 0, 0, 0, 0, 224 },
            (1, 32) => new byte[] { 0, 0, 0, 128, 0, 0, 0, 64, 0, 0, 0, 0, 0, 0, 0, 224 },
            (3, 32) => new[] { -1f, 0.5f, 0f, -0.25f }.SelectMany(BitConverter.GetBytes).ToArray(),
            (3, 64) => new[] { -1d, 0.5d, 0d, -0.25d }.SelectMany(BitConverter.GetBytes).ToArray(),
            _ => throw new InvalidOperationException()
        };
        using var stream = new MemoryStream(WaveFixture.Create((ushort)format, 2, 48000, (ushort)bits, payload));
        var audio = await AudioInputReader.ReadRecordingAsync(stream, CancellationToken.None);
        Assert.Equal(48000, audio.SampleRate);
        Assert.Equal(2, audio.Channels);
        Assert.Equal([-1f, 0.5f, 0f, -0.25f], audio.Samples);
    }

    [Theory]
    [InlineData(1, 16)]
    [InlineData(1, 24)]
    [InlineData(3, 32)]
    [InlineData(3, 64)]
    public async Task AcceptsSupportedExtensibleSubformats(int format, int bits)
    {
        var payload = new byte[bits / 8 * 2];
        using var stream = new MemoryStream(WaveFixture.Create((ushort)format, 2, 44100, (ushort)bits, payload, true));
        var audio = await AudioInputReader.ReadRecordingAsync(stream, CancellationToken.None);
        Assert.Equal(44100, audio.SampleRate);
        Assert.Equal(2, audio.Channels);
        Assert.Equal([0f, 0f], audio.Samples);
    }

    [Fact]
    public async Task ChunkedPcm24DecodePreservesFramesAcrossReadBoundaries()
    {
        var data = Enumerable.Range(0, 20000).SelectMany(_ => new byte[] { 0, 0, 128, 0, 0, 64 }).ToArray();
        using var stream = new MemoryStream(WaveFixture.Create(1, 2, 48000, 24, data));
        var audio = await AudioInputReader.ReadRecordingAsync(stream, CancellationToken.None);
        Assert.Equal(40000, audio.Samples.Length);
        Assert.Equal([-1f, 0.5f], audio.Samples[..2]);
        Assert.Equal([-1f, 0.5f, -1f, 0.5f], audio.Samples[21844..21848]);
        Assert.Equal([-1f, 0.5f], audio.Samples[^2..]);
        Assert.Equal(2, audio.Channels);
    }

    [Fact]
    public async Task ReplayAndRecordingShareLayoutEvenWhenDataPrecedesFormat()
    {
        byte[] pcm = [0, 128, 0, 32, 0, 224];
        var wave = WaveFixture.Chunks(("data", pcm), ("JUNK", [1, 2, 3]),
            ("fmt ", WaveFixture.Format(1, 1, 16000, 16)));
        using var stream = new MemoryStream(wave);
        var recording = await AudioInputReader.ReadRecordingAsync(stream, CancellationToken.None);
        var replay = await AudioInputReader.ReadReplayAsync(stream, CancellationToken.None);
        Assert.Equal([-1f, 0.25f, -0.25f], recording.Samples);
        Assert.Equal(pcm, replay);
    }

    [Fact]
    public async Task ReplayAcceptsExtensiblePcm16AndPreservesTheExactBytes()
    {
        byte[] pcm = [0, 128, 255, 127, 0, 64];
        using var stream = new MemoryStream(WaveFixture.Create(1, 1, 16000, 16, pcm, true));
        Assert.Equal(pcm, await AudioInputReader.ReadReplayAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(1, 2, 16000, 16)]
    [InlineData(1, 1, 44100, 16)]
    [InlineData(1, 1, 16000, 8)]
    [InlineData(3, 1, 16000, 32)]
    public async Task ReplayRejectsValidNonReplayFormats(int format, int channels, int rate, int bits)
    {
        using var stream = new MemoryStream(WaveFixture.Create((ushort)format, (ushort)channels,
            (uint)rate, (ushort)bits, new byte[channels * bits / 8]));
        var audio = await AudioInputReader.ReadRecordingAsync(stream, CancellationToken.None);
        Assert.Equal(channels, audio.Channels);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AudioInputReader.ReadReplayAsync(stream, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(MalformedWaves))]
    public async Task RecordingAndReplayRejectTheSameMalformedLayout(string name, byte[] wave)
    {
        using var stream = new MemoryStream(wave);
        var recordingError = await Record.ExceptionAsync(() =>
            AudioInputReader.ReadRecordingAsync(stream, CancellationToken.None));
        var replayError = await Record.ExceptionAsync(() =>
            AudioInputReader.ReadReplayAsync(stream, CancellationToken.None));
        Assert.True(recordingError is InvalidDataException or NotSupportedException, name);
        Assert.NotNull(replayError);
        Assert.Equal(recordingError.GetType(), replayError.GetType());
        Assert.Equal(recordingError.Message, replayError.Message);
    }

    public static IEnumerable<object[]> MalformedWaves()
    {
        var format = WaveFixture.Format(1, 1, 16000, 16);
        var valid = WaveFixture.Chunks(("fmt ", format), ("data", [0, 0]));
        yield return ["missing format", WaveFixture.Chunks(("data", [0, 0]))];
        yield return ["missing data", WaveFixture.Chunks(("fmt ", format))];
        yield return ["empty data", WaveFixture.Chunks(("fmt ", format), ("data", []))];
        yield return ["duplicate format", WaveFixture.Chunks(("fmt ", format), ("data", [0, 0]), ("fmt ", format))];
        yield return ["duplicate data", WaveFixture.Chunks(("fmt ", format), ("data", [0, 0]), ("data", [1, 2]))];
        yield return ["incomplete frame", WaveFixture.Chunks(("fmt ", format), ("data", [0]))];
        yield return ["short format", WaveFixture.Chunks(("fmt ", new byte[15]), ("data", [0, 0]))];
        yield return ["17-byte format", WaveFixture.Chunks(("fmt ", new byte[17]), ("data", [0, 0]))];
        yield return ["bad byte rate", Mutate(valid, 28, 1)];
        yield return ["zero sample rate", Mutate(valid, 24, 0)];
        yield return ["excessive sample rate", Mutate(valid, 24, 384001)];
        yield return ["bad alignment", Mutate(valid, 32, 0x00100001)];
        yield return ["zero channels", Mutate(valid, 20, 1)];
        yield return ["RIFF smaller than file", Mutate(valid, 4, (uint)valid.Length - 10)];
        yield return ["wrong RIFF signature", Mutate(valid, 0, 0)];
        yield return ["wrong WAVE signature", Mutate(valid, 8, 0)];
        yield return ["RIFF larger than file", Mutate(valid, 4, (uint)valid.Length)];
        yield return ["chunk past boundary", Mutate(valid, 40, 100)];
        var partialHeader = WaveFixture.Chunks(("fmt ", format), ("data", [0, 0])).Concat(new byte[] { 1, 2, 3 }).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(partialHeader.AsSpan(4), (uint)partialHeader.Length - 8);
        yield return ["trailing partial header", partialHeader];
        var truncated = valid[..^1];
        BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(4), (uint)truncated.Length - 8);
        yield return ["truncated data", truncated];
        var missingPad = WaveFixture.Chunks(("fmt ", format), ("JUNK", [1]))[..^1];
        BinaryPrimitives.WriteUInt32LittleEndian(missingPad.AsSpan(4), (uint)missingPad.Length - 8);
        yield return ["missing odd chunk padding", missingPad];
        var extended = WaveFixture.Create(1, 1, 16000, 16, [0, 0], true);
        yield return ["wrong GUID suffix", Mutate(extended, 56, 0)];
        yield return ["wrong valid bits", Mutate(extended, 36, 0x000f0016)];
        yield return ["bad extension length", Mutate(extended, 36, 0x00100015)];
        yield return ["wrong channel mask", Mutate(extended, 40, 3)];
        yield return ["short extensible format", WaveFixture.Chunks(("fmt ", WaveFixture.Format(65534, 1, 16000, 16)), ("data", [0, 0]))];
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MaxValue)]
    public async Task RejectsNonFiniteAndFloatOverflowSamples(double value)
    {
        using var stream = new MemoryStream(WaveFixture.Create(3, 1, 16000, 64, BitConverter.GetBytes(value)));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AudioInputReader.ReadRecordingAsync(stream, CancellationToken.None));
        Assert.Contains("non-finite", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringDataIoStopsBothPaths(bool replay)
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new SparseWaveStream(1, 16000, 16, 128000) { CancelOnDataRead = cancellation };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay
            ? AudioInputReader.ReadReplayAsync(stream, cancellation.Token)
            : (Task)AudioInputReader.ReadRecordingAsync(stream, cancellation.Token));
        Assert.True(stream.DataBytesRead > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyCancelledInputDoesNotOpenAFile(bool replay)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        IAudioInputReader reader = new AudioInputReader();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay
            ? reader.ReadReplayAsync("this-file-must-not-be-opened.wav", cancellation.Token)
            : (Task)reader.ReadRecordingAsync("this-file-must-not-be-opened.wav", cancellation.Token));
    }

    [Fact]
    public async Task DecodedExpansionAccepts64MiBAndRejectsTheNextSampleBeforeReadingData()
    {
        using var accepted = new SparseWaveStream(1, 384000, 8, 16 * 1024 * 1024);
        var audio = await AudioInputReader.ReadRecordingAsync(accepted, CancellationToken.None);
        Assert.Equal(16 * 1024 * 1024, audio.Samples.Length);
        Assert.Equal(-1f, audio.Samples[0]);
        Assert.Equal(-1f, audio.Samples[^1]);
        Assert.InRange(accepted.MaximumReadBytes, 1, 64 * 1024);
        using var rejected = new SparseWaveStream(1, 384000, 8, 16 * 1024 * 1024 + 1);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AudioInputReader.ReadRecordingAsync(rejected, CancellationToken.None));
        Assert.Contains("64 MiB", error.Message);
        Assert.Equal(0, rejected.DataBytesRead);
        var replayError = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AudioInputReader.ReadReplayAsync(rejected, CancellationToken.None));
        Assert.Equal(error.Message, replayError.Message);
    }

    [Fact]
    public async Task DurationAcceptsFiveMinutesAndRejectsOneMoreFrameBeforeDataIo()
    {
        using var accepted = new SparseWaveStream(1, 1000, 16, 600000);
        var audio = await AudioInputReader.ReadRecordingAsync(accepted, CancellationToken.None);
        Assert.Equal(300000, audio.Samples.Length);
        using var rejected = new SparseWaveStream(1, 1000, 16, 600002);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AudioInputReader.ReadRecordingAsync(rejected, CancellationToken.None));
        Assert.Contains("five-minute", error.Message);
        Assert.Equal(0, rejected.DataBytesRead);
        var replayError = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AudioInputReader.ReadReplayAsync(rejected, CancellationToken.None));
        Assert.Equal(error.Message, replayError.Message);
    }

    [Fact]
    public async Task FileBytesAccept100MiBAndRejectTheNextByteBeforeIo()
    {
        using var accepted = new SparseWaveStream(1, 16000, 16, 2, AudioInputReader.MaximumFileBytes);
        var audio = await AudioInputReader.ReadRecordingAsync(accepted, CancellationToken.None);
        Assert.Equal([0f], audio.Samples);
        using var rejected = new SparseWaveStream(1, 16000, 16, 2, AudioInputReader.MaximumFileBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AudioInputReader.ReadRecordingAsync(rejected, CancellationToken.None));
        Assert.Equal(0, rejected.ReadCalls);
    }

    private static byte[] Mutate(byte[] source, int offset, uint value)
    {
        var copy = source.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(offset), value);
        return copy;
    }
}

internal static class WaveFixture
{
    public static byte[] Create(ushort format, ushort channels, uint rate, ushort bits, byte[] data, bool extensible = false) =>
        Chunks(("fmt ", Format(format, channels, rate, bits, extensible)), ("data", data));

    public static byte[] Format(ushort format, ushort channels, uint rate, ushort bits, bool extensible = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write(extensible ? (ushort)65534 : format);
        writer.Write(channels);
        writer.Write(rate);
        writer.Write((uint)(rate * channels * (bits / 8)));
        writer.Write((ushort)(channels * (bits / 8)));
        writer.Write(bits);
        if (extensible)
        {
            writer.Write((ushort)22);
            writer.Write(bits);
            writer.Write(channels == 2 ? 3u : 0u);
            writer.Write(new Guid(format == 1 ? "00000001-0000-0010-8000-00aa00389b71" :
                "00000003-0000-0010-8000-00aa00389b71").ToByteArray());
        }
        return stream.ToArray();
    }

    public static byte[] Chunks(params (string Id, byte[] Bytes)[] chunks)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write("RIFF"u8);
        writer.Write(0u);
        writer.Write("WAVE"u8);
        foreach (var chunk in chunks)
        {
            writer.Write(Encoding.ASCII.GetBytes(chunk.Id));
            writer.Write(chunk.Bytes.Length);
            writer.Write(chunk.Bytes);
            if (chunk.Bytes.Length % 2 != 0) { writer.Write((byte)0); }
        }
        stream.Position = 4;
        writer.Write((uint)stream.Length - 8);
        return stream.ToArray();
    }
}

internal sealed class SparseWaveStream : Stream
{
    private readonly byte[] _prefix;
    private readonly long _length;
    private readonly long _dataBytes;
    private readonly byte[] _suffix;
    public long DataBytesRead { get; private set; }
    public int ReadCalls { get; private set; }
    public int MaximumReadBytes { get; private set; }
    public CancellationTokenSource? CancelOnDataRead { get; init; }

    public SparseWaveStream(ushort channels, uint rate, ushort bits, int dataBytes, long? fileBytes = null)
    {
        _prefix = WaveFixture.Create(1, channels, rate, bits, []);
        _dataBytes = dataBytes;
        var waveLength = _prefix.Length + dataBytes + (dataBytes & 1);
        _length = fileBytes ?? waveLength;
        BinaryPrimitives.WriteUInt32LittleEndian(_prefix.AsSpan(4), (uint)_length - 8);
        BinaryPrimitives.WriteUInt32LittleEndian(_prefix.AsSpan(40), (uint)dataBytes);
        _suffix = new byte[8];
        "JUNK"u8.CopyTo(_suffix);
        BinaryPrimitives.WriteUInt32LittleEndian(_suffix.AsSpan(4), (uint)Math.Max(0, _length - waveLength - 8));
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCalls++;
        MaximumReadBytes = Math.Max(MaximumReadBytes, buffer.Length);
        var count = (int)Math.Min(buffer.Length, _length - Position);
        var destination = buffer.Span[..count];
        destination.Clear();
        for (var index = 0; index < count; index++)
        {
            var offset = Position + index;
            if (offset < _prefix.Length) { destination[index] = _prefix[(int)offset]; }
            var suffixOffset = offset - (_prefix.Length + _dataBytes + (_dataBytes & 1));
            if (suffixOffset >= 0 && suffixOffset < _suffix.Length)
            {
                destination[index] = _suffix[(int)suffixOffset];
            }
        }
        if (Position >= _prefix.Length && Position < _prefix.Length + _dataBytes)
        {
            DataBytesRead += count;
            CancelOnDataRead?.Cancel();
        }
        Position += count;
        return ValueTask.FromResult(count);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get; set; }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
