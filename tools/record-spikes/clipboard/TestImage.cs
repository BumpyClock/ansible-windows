using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

// Synthetic, labeled fixtures. No screenshots or speech are collected by this harness.
internal sealed record TestImage(int Width, int Height, byte[] Rgba)
{
    public static TestImage Frame(int number, int width, bool noise)
    {
        int height = width * 9 / 16;
        var pixels = new byte[width * height * 4];
        var random = new Random(101 + number);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int p = (y * width + x) * 4;
            pixels[p] = noise ? (byte)random.Next(256) : (byte)(35 + number * 17 % 180);
            pixels[p + 1] = noise ? (byte)random.Next(256) : (byte)(55 + x * 100 / width);
            pixels[p + 2] = noise ? (byte)random.Next(256) : (byte)(70 + y * 100 / height);
            pixels[p + 3] = 255;
        }
        var result = new TestImage(width, height, pixels);
        result.Label($"SYNTHETIC FRAME {number:00}", Math.Max(2, width / 160));
        return result;
    }

    public static TestImage Composite(IReadOnlyList<TestImage> images)
    {
        int columns = Math.Min(4, (int)Math.Ceiling(Math.Sqrt(images.Count)));
        int rows = (images.Count + columns - 1) / columns;
        int tileWidth = Math.Min(1920 / columns, 1080 / rows * 16 / 9);
        int tileHeight = tileWidth * 9 / 16;
        int width = columns * tileWidth, height = rows * tileHeight;
        var rgba = new byte[width * height * 4];
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
        for (int i = 0; i < images.Count; i++)
        {
            var image = images[i];
            for (int y = 0; y < tileHeight; y++)
            for (int x = 0; x < tileWidth; x++)
            {
                int source = (y * image.Height / tileHeight * image.Width + x * image.Width / tileWidth) * 4;
                int dest = ((i / columns * tileHeight + y) * width + i % columns * tileWidth + x) * 4;
                image.Rgba.AsSpan(source, 4).CopyTo(rgba.AsSpan(dest, 4));
            }
        }
        return new(width, height, rgba);
    }

    private void Label(string text, int scale)
    {
        int panelWidth = Math.Min(Width, (text.Length * 4 + 2) * scale);
        for (int y = 0; y < Math.Min(Height, 9 * scale); y++)
        for (int x = 0; x < panelWidth; x++)
        {
            int p = (y * Width + x) * 4;
            Rgba[p] = Rgba[p + 1] = Rgba[p + 2] = 255;
        }
        for (int c = 0; c < text.Length; c++)
        {
            string glyph = text[c] switch
            {
                '0' => "111101101101111", '1' => "010110010010111", '2' => "111001111100111",
                '3' => "111001111001111", '4' => "101101111001001", '5' => "111100111001111",
                '6' => "111100111101111", '7' => "111001010010010", '8' => "111101111101111",
                '9' => "111101111001111", 'S' => "111100111001111", 'Y' => "101101010010010",
                'N' => "101111111111101", 'T' => "111010010010010", 'H' => "101101111101101",
                'E' => "111100110100111", 'I' => "111010010010111", 'C' => "111100100100111",
                'F' => "111100110100100", 'R' => "110101110101101", 'A' => "010101111101101",
                'M' => "101111111101101", _ => "000000000000000"
            };
            for (int y = 0; y < 5 * scale; y++)
            for (int x = 0; x < 3 * scale; x++)
            {
                int px = (1 + c * 4) * scale + x, py = 2 * scale + y;
                if (px >= Width || py >= Height || glyph[y / scale * 3 + x / scale] != '1') continue;
                int p = (py * Width + px) * 4;
                Rgba[p] = Rgba[p + 1] = Rgba[p + 2] = 0;
            }
        }
    }

    public byte[] Png()
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), Height);
        header[8] = 8; header[9] = 6;
        Chunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            for (int y = 0; y < Height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(Rgba.AsSpan(y * Width * 4, Width * 4));
            }
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream stream, string kind, byte[] data)
    {
        var bytes = Encoding.ASCII.GetBytes(kind).Concat(data).ToArray();
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length); stream.Write(word); stream.Write(bytes);
        uint crc = uint.MaxValue;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc); stream.Write(word);
    }

    public byte[] DibV5()
    {
        var dib = new byte[124 + Rgba.Length];
        void Word(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(offset), value);
        Word(0, 124); Word(4, Width); Word(8, Height);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14), 32);
        Word(16, 3); Word(20, Rgba.Length);
        Word(40, 0x00ff0000); Word(44, 0x0000ff00); Word(48, 0x000000ff); Word(52, unchecked((int)0xff000000));
        Word(56, 0x73524742); Word(108, 4);
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int source = (y * Width + x) * 4, dest = 124 + ((Height - 1 - y) * Width + x) * 4;
            dib[dest] = Rgba[source + 2]; dib[dest + 1] = Rgba[source + 1]; dib[dest + 2] = Rgba[source]; dib[dest + 3] = 255;
        }
        return dib;
    }
}
