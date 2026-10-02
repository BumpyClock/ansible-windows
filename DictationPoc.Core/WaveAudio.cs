using System.Buffers.Binary;
using System.Text;

namespace DictationPoc.Core;

public sealed record WaveAudio(float[] Samples, int SampleRate, int Channels)
{
    public static WaveAudio Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path), Encoding.ASCII);
        if (reader.BaseStream.Length > 100 * 1024 * 1024 ||
            Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
        {
            throw new InvalidDataException("Choose a RIFF WAV recording up to 100 MB.");
        }
        reader.ReadUInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
        {
            throw new InvalidDataException("The selected file is not WAV audio.");
        }
        ushort format = 0, channels = 0, bits = 0, alignment = 0;
        uint rate = 0;
        byte[]? data = null;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadUInt32();
            var next = reader.BaseStream.Position + size + (size & 1);
            if (next > reader.BaseStream.Length)
            {
                throw new InvalidDataException("The WAV recording is truncated.");
            }
            if (id == "fmt ")
            {
                if (size < 16) { throw new InvalidDataException("Invalid WAV format."); }
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = reader.ReadUInt32();
                reader.ReadUInt32();
                alignment = reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (format == 65534 && size >= 40)
                {
                    reader.ReadUInt16();
                    var validBits = reader.ReadUInt16();
                    reader.ReadUInt32();
                    var subformat = new Guid(reader.ReadBytes(16));
                    if (validBits != bits)
                    {
                        throw new NotSupportedException("WAV valid-bit padding is not supported by this POC.");
                    }
                    format = subformat == new Guid("00000001-0000-0010-8000-00aa00389b71") ? (ushort)1 :
                        subformat == new Guid("00000003-0000-0010-8000-00aa00389b71") ? (ushort)3 : (ushort)0;
                }
            }
            else if (id == "data")
            {
                data = reader.ReadBytes(checked((int)size));
            }
            reader.BaseStream.Position = next;
        }
        if (data is null || channels is < 1 or > 16 || rate is < 1000 or > 384000 ||
            !(format == 1 && bits is 8 or 16 or 24 or 32 || format == 3 && bits is 32 or 64) ||
            alignment != channels * (bits / 8) || data.Length % alignment != 0)
        {
            throw new NotSupportedException("The recording must contain PCM or floating-point WAV audio with complete frames.");
        }
        var bytesPerSample = bits / 8;
        var samples = new float[data.Length / bytesPerSample];
        for (var index = 0; index < samples.Length; index++)
        {
            var bytes = data.AsSpan(index * bytesPerSample, bytesPerSample);
            samples[index] = (format, bits) switch
            {
                (1, 8) => (bytes[0] - 128) / 128f,
                (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
                (1, 24) => ((bytes[0] | bytes[1] << 8 | bytes[2] << 16) << 8 >> 8) / 8388608f,
                (1, 32) => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f,
                (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(bytes),
                (3, 64) => (float)BinaryPrimitives.ReadDoubleLittleEndian(bytes),
                _ => throw new NotSupportedException("Unsupported WAV sample format.")
            };
            if (!float.IsFinite(samples[index]))
            {
                throw new InvalidDataException("The WAV recording contains non-finite samples.");
            }
        }
        return new WaveAudio(samples, checked((int)rate), channels);
    }
}
