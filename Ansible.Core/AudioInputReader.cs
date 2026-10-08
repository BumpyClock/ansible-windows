using System.Buffers.Binary;
using System.Numerics;

namespace Ansible.Core;

/// <summary>
/// Reads complete RIFF WAV files, retaining interleaved channels and their sample rate.
/// Input is limited to five minutes, 64 MiB of decoded floats, and 100 MiB of file bytes.
/// Replay additionally requires 16 kHz mono PCM16. No resampling is performed.
/// </summary>
public sealed class AudioInputReader : IAudioInputReader
{
    public const int MaximumDurationSeconds = 300;
    public const int MaximumDecodedBytes = 64 * 1024 * 1024;
    public const int MaximumFileBytes = 100 * 1024 * 1024;
    private const int DecodeBufferBytes = 64 * 1024;

    public Task<WaveAudio> ReadRecordingAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            await using var stream = Open(path, cancellationToken);
            return await ReadRecordingAsync(stream, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<byte[]> ReadReplayAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            await using var stream = Open(path, cancellationToken);
            return await ReadReplayAsync(stream, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private static FileStream Open(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, DecodeBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    internal static async Task<WaveAudio> ReadRecordingAsync(Stream stream, CancellationToken cancellationToken)
    {
        var wave = await ReadLayoutAsync(stream, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var samples = new float[wave.DataBytes / wave.BytesPerSample];
        var buffer = new byte[DecodeBufferBytes - DecodeBufferBytes % wave.BytesPerSample];
        stream.Position = wave.DataOffset;
        var sampleOffset = 0;
        while (sampleOffset < samples.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sampleCount = Math.Min(buffer.Length / wave.BytesPerSample, samples.Length - sampleOffset);
            var byteCount = sampleCount * wave.BytesPerSample;
            await ReadExactlyAsync(stream, buffer.AsMemory(0, byteCount), cancellationToken).ConfigureAwait(false);
            Decode(buffer.AsSpan(0, byteCount), samples.AsSpan(sampleOffset, sampleCount), wave);
            sampleOffset += sampleCount;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new WaveAudio(samples, wave.SampleRate, wave.Channels);
    }

    internal static async Task<byte[]> ReadReplayAsync(Stream stream, CancellationToken cancellationToken)
    {
        var wave = await ReadLayoutAsync(stream, cancellationToken).ConfigureAwait(false);
        if (wave.Format != 1 || wave.SampleRate != 16000 || wave.Channels != 1 || wave.Bits != 16)
        {
            throw new InvalidDataException("Replay requires 16 kHz mono PCM16 WAV audio.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var data = new byte[wave.DataBytes];
        stream.Position = wave.DataOffset;
        await ReadExactlyAsync(stream, data, cancellationToken).ConfigureAwait(false);
        return data;
    }

    private static async Task<WaveLayout> ReadLayoutAsync(Stream stream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var length = stream.Length;
        if (length < 12 || length > MaximumFileBytes)
        {
            throw new InvalidDataException("Choose a RIFF WAV file up to 100 MiB.");
        }
        stream.Position = 0;
        var header = new byte[12];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !header.AsSpan(8, 4).SequenceEqual("WAVE"u8) ||
            (long)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) + 8 != length)
        {
            throw new InvalidDataException("The WAV file has invalid RIFF boundaries.");
        }

        WaveLayout? format = null;
        long dataOffset = 0;
        uint dataBytes = 0;
        var hasData = false;
        var chunkHeader = new byte[8];
        var formatBytes = new byte[40];
        while (stream.Position < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (length - stream.Position < 8)
            {
                throw new InvalidDataException("The WAV file contains a truncated chunk header.");
            }
            await ReadExactlyAsync(stream, chunkHeader, cancellationToken).ConfigureAwait(false);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4));
            var next = stream.Position + size + (size & 1);
            if (next > length)
            {
                throw new InvalidDataException("The WAV file contains a truncated chunk or padding.");
            }
            if (chunkHeader.AsSpan(0, 4).SequenceEqual("fmt "u8))
            {
                if (format is not null)
                {
                    throw new InvalidDataException("The WAV file contains duplicate fmt chunks.");
                }
                if (size < 16 || size == 17)
                {
                    throw new InvalidDataException("The WAV file has an invalid format size.");
                }
                await ReadExactlyAsync(stream, formatBytes.AsMemory(0, (int)Math.Min(size, 40)),
                    cancellationToken).ConfigureAwait(false);
                format = ParseFormat(formatBytes, size);
            }
            else if (chunkHeader.AsSpan(0, 4).SequenceEqual("data"u8))
            {
                if (hasData)
                {
                    throw new InvalidDataException("The WAV file contains duplicate data chunks.");
                }
                hasData = true;
                dataOffset = stream.Position;
                dataBytes = size;
            }
            stream.Position = next;
        }
        if (format is null || !hasData || dataBytes == 0 || dataBytes % format.BlockAlign != 0)
        {
            throw new InvalidDataException("The WAV file requires one fmt chunk and nonempty, complete audio frames.");
        }
        if ((long)dataBytes / format.BlockAlign > (long)format.SampleRate * MaximumDurationSeconds)
        {
            throw new InvalidDataException("The WAV recording exceeds the five-minute duration limit.");
        }
        if ((long)dataBytes / format.BytesPerSample * sizeof(float) > MaximumDecodedBytes)
        {
            throw new InvalidDataException("The WAV recording exceeds the 64 MiB decoded float payload limit.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return format with { DataOffset = dataOffset, DataBytes = checked((int)dataBytes) };
    }

    private static WaveLayout ParseFormat(ReadOnlySpan<byte> bytes, uint size)
    {
        var format = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        var rate = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var byteRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        var alignment = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
        if (size >= 18 && BinaryPrimitives.ReadUInt16LittleEndian(bytes[16..]) != size - 18)
        {
            throw new InvalidDataException("The WAV format extension size is inconsistent.");
        }
        if (format == 65534)
        {
            if (size < 40)
            {
                throw new InvalidDataException("The extensible WAV format is truncated.");
            }
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]) != bits)
            {
                throw new NotSupportedException("WAV valid-bit padding is not supported.");
            }
            var mask = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]);
            if (mask != 0 && BitOperations.PopCount(mask) != channels)
            {
                throw new InvalidDataException("The WAV channel mask does not match its channel count.");
            }
            var subformat = new Guid(bytes.Slice(24, 16));
            format = subformat == new Guid("00000001-0000-0010-8000-00aa00389b71") ? (ushort)1 :
                subformat == new Guid("00000003-0000-0010-8000-00aa00389b71") ? (ushort)3 : (ushort)0;
        }
        if (channels is < 1 or > 16 || rate is < 1000 or > 384000 ||
            !(format == 1 && bits is 8 or 16 or 24 or 32 || format == 3 && bits is 32 or 64))
        {
            throw new NotSupportedException("Use PCM8/16/24/32 or float32/64 WAV audio with 1-16 channels at 1-384 kHz.");
        }
        if (alignment != channels * (bits / 8) || byteRate != (long)rate * alignment)
        {
            throw new InvalidDataException("The WAV block alignment or byte rate is inconsistent.");
        }
        return new WaveLayout(format, bits, channels, (int)rate, alignment, 0, 0);
    }

    private static void Decode(ReadOnlySpan<byte> bytes, Span<float> samples, WaveLayout wave)
    {
        for (var index = 0; index < samples.Length; index++)
        {
            var value = bytes.Slice(index * wave.BytesPerSample, wave.BytesPerSample);
            samples[index] = (wave.Format, wave.Bits) switch
            {
                (1, 8) => (value[0] - 128) / 128f,
                (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(value) / 32768f,
                (1, 24) => ((value[0] | value[1] << 8 | value[2] << 16) << 8 >> 8) / 8388608f,
                (1, 32) => BinaryPrimitives.ReadInt32LittleEndian(value) / 2147483648f,
                (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(value),
                (3, 64) => (float)BinaryPrimitives.ReadDoubleLittleEndian(value),
                _ => throw new InvalidOperationException("Unvalidated WAV sample format.")
            };
            if (!float.IsFinite(samples[index]))
            {
                throw new InvalidDataException("The WAV recording contains non-finite samples.");
            }
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> bytes, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException("The WAV file is truncated.", error);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed record WaveLayout(ushort Format, int Bits, int Channels, int SampleRate,
        int BlockAlign, long DataOffset, int DataBytes)
    {
        public int BytesPerSample => Bits / 8;
    }
}
