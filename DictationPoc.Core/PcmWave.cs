using System.Text;

namespace DictationPoc.Core;

public static class PcmWave
{
    public static byte[] Read16kMono(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path), Encoding.ASCII);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
        {
            throw new InvalidDataException("Live verification requires a RIFF WAV recording.");
        }
        reader.ReadUInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
        {
            throw new InvalidDataException("The selected recording is not WAV audio.");
        }
        var validFormat = false;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadUInt32();
            var next = reader.BaseStream.Position + size + (size & 1);
            if (next > reader.BaseStream.Length)
            {
                throw new InvalidDataException("The WAV recording contains a truncated chunk.");
            }
            if (id == "fmt ")
            {
                if (size < 16)
                {
                    throw new InvalidDataException("The WAV recording has an invalid format.");
                }
                var format = reader.ReadUInt16();
                var channels = reader.ReadUInt16();
                var sampleRate = reader.ReadUInt32();
                reader.ReadUInt32();
                var alignment = reader.ReadUInt16();
                var bits = reader.ReadUInt16();
                validFormat = format == 1 && channels == 1 && sampleRate == 16000 && alignment == 2 && bits == 16;
            }
            else if (id == "data")
            {
                if (!validFormat || size == 0 || size % 2 != 0 || size > 100 * 1024 * 1024)
                {
                    throw new InvalidDataException("Live verification requires 16 kHz mono PCM16 WAV, up to 100 MB.");
                }
                return reader.ReadBytes(checked((int)size));
            }
            reader.BaseStream.Position = next;
        }
        throw new InvalidDataException("The WAV recording contains no audio data.");
    }
}
