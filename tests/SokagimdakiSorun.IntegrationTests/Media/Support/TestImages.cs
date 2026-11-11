using System.Buffers.Binary;
using System.Text;

namespace SokagimdakiSorun.Media.TestSupport;

/// <summary>
/// Testler için elle kurulmuş, yapısal olarak geçerli küçük görüntüler (görüntü kütüphanesi yok). Metadata'lı
/// sürümler, konum verisini temsil eden <see cref="Marker"/> metnini EXIF/XMP/metin chunk'larında ve dosya sonunda taşır.
/// Ayıklanmış çıktı, metadata'sız sürümle bayt bayt aynı olmalıdır.
/// Bu dosya hem SokagimdakiSorun.IntegrationTests hem SokagimdakiSorun.Media.Tests tarafından derlenir (bağlantılı dosya).
/// </summary>
internal static class TestImages
{
    /// <summary>Gömülü konum verisinin yerine geçen işaret.</summary>
    public const string Marker = "GPS-41.0082N-28.9784E";

    public static byte[] Jpeg(bool withMetadata)
    {
        var b = new List<byte> { 0xFF, 0xD8 };
        Segment(b, 0xE0, [.. "JFIF\0"u8, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);
        if (withMetadata)
        {
            Segment(b, 0xE1, [.. "Exif\0\0"u8, .. Ascii(Marker)]);
            Segment(b, 0xE1, [.. "http://ns.adobe.com/xap/1.0/\0"u8, .. Ascii($"<x:xmpmeta>{Marker}</x:xmpmeta>")]);
            Segment(b, 0xED, [.. "Photoshop 3.0\0"u8, .. Ascii(Marker)]); // IPTC
            Segment(b, 0xFE, Ascii(Marker)); // COM
        }

        Segment(b, 0xDB, [0x00, .. Enumerable.Repeat((byte)1, 64)]); // DQT
        Segment(b, 0xC0, [0x08, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x11, 0x00]); // SOF0 1x1, 1 bileşen
        Segment(b, 0xC4, [0x00, 0x01, .. new byte[15], 0x00]); // DHT
        Segment(b, 0xDA, [0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]); // SOS
        b.AddRange([0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56]); // entropi verisi: bayt doldurma + RST0
        b.AddRange([0xFF, 0xD9]); // EOI
        if (withMetadata)
        {
            b.AddRange(Ascii(Marker)); // EOI sonrası (ör. gömülü ek görüntü)
        }

        return [.. b];
    }

    public static byte[] Png(bool withMetadata)
    {
        var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Chunk(b, "IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 0, 0, 0, 0]);
        if (withMetadata)
        {
            Chunk(b, "tEXt", [.. "Comment\0"u8, .. Ascii(Marker)]);
            Chunk(b, "eXIf", [.. "MM\0*"u8, .. Ascii(Marker)]);
            Chunk(b, "iTXt", [.. "XML:com.adobe.xmp\0\0\0\0\0"u8, .. Ascii(Marker)]);
        }

        Chunk(b, "gAMA", [0, 0, 0xB1, 0x8F]);
        Chunk(b, "IDAT", [0x78, 0x9C, 0x63, 0x60, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01]);
        Chunk(b, "IEND", []);
        if (withMetadata)
        {
            b.AddRange(Ascii(Marker));
        }

        return [.. b];
    }

    public static byte[] WebP(bool withMetadata)
    {
        var chunks = new List<byte>();
        byte flags = withMetadata ? (byte)(0x08 | 0x04) : (byte)0;
        RiffChunk(chunks, "VP8X", [flags, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        RiffChunk(chunks, "VP8 ", [0x30, 0x01, 0x00, 0x9D, 0x01]); // tek uzunluk → dolgu baytı
        if (withMetadata)
        {
            RiffChunk(chunks, "EXIF", [.. "MM\0*"u8, .. Ascii(Marker)]);
            RiffChunk(chunks, "XMP ", Ascii($"<x:xmpmeta>{Marker}</x:xmpmeta>"));
        }

        var b = new List<byte>();
        b.AddRange("RIFF"u8.ToArray());
        b.AddRange(LittleEndian((uint)(4 + chunks.Count)));
        b.AddRange("WEBP"u8.ToArray());
        b.AddRange(chunks);
        if (withMetadata)
        {
            b.AddRange(Ascii(Marker)); // RIFF dışı sondaki veri
        }

        return [.. b];
    }

    public static bool ContainsMarker(ReadOnlySpan<byte> data) => data.IndexOf(Ascii(Marker)) >= 0;

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

    private static void Segment(List<byte> b, byte marker, byte[] payload)
    {
        b.AddRange([0xFF, marker]);
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(payload.Length + 2));
        b.AddRange(length);
        b.AddRange(payload);
    }

    private static void Chunk(List<byte> b, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        b.AddRange(length);
        b.AddRange(Encoding.ASCII.GetBytes(type));
        b.AddRange(data);
        b.AddRange(new byte[4]); // CRC (ayıklayıcı doğrulamaz, aynen kopyalar)
    }

    private static void RiffChunk(List<byte> b, string fourCc, byte[] data)
    {
        b.AddRange(Encoding.ASCII.GetBytes(fourCc));
        b.AddRange(LittleEndian((uint)data.Length));
        b.AddRange(data);
        if ((data.Length & 1) == 1)
        {
            b.Add(0);
        }
    }

    private static byte[] LittleEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }
}
