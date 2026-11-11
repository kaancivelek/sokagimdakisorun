using System.Buffers.Binary;
using System.Text;

namespace SokagimdakiSorun.Media.Application;

/// <summary>
/// EXIF/GPS ve benzeri gömülü metadata'yı <b>saklamadan önce</b> ayıklar (ADR-0004 §6 kararı, ARCHITECTURE §8).
/// Görüntü yeniden kodlanmaz, piksel verisine dokunulmaz: yalnızca kapsayıcı yapısındaki segment/chunk'lar
/// <b>izin listesine</b> göre süzülür. İzin listesinde olmayan her yardımcı veri atılır; yeni bir metadata türü
/// varsayılan olarak saklanmaz.
/// <list type="bullet">
///   <item>JPEG: APP0 yalnızca JFIF, APP2 yalnızca ICC profili, APP14 yalnızca Adobe korunur; diğer APPn (EXIF, XMP,
///         IPTC, MPF…) ve yorumlar (COM) atılır. EOI'den sonraki veri (ör. gömülü ek görüntüler) atılır.</item>
///   <item>PNG: kritik chunk'lar ve görüntülemeyi etkileyen yardımcı chunk'lar korunur; <c>eXIf</c>, <c>tEXt</c>,
///         <c>zTXt</c>, <c>iTXt</c>, <c>tIME</c> vb. atılır. IEND sonrası atılır.</item>
///   <item>WebP: <c>EXIF</c> ve <c>XMP </c> chunk'ları atılır; VP8X bayrakları ve RIFF boyutu düzeltilir.</item>
/// </list>
/// Yapı bozuksa <see langword="false"/> döner ve dosya reddedilir. Bilinen sınır: EXIF yön (orientation) etiketi de
/// atıldığı için yönü yalnızca EXIF'te tutan fotoğraflar yan görünebilir.
/// </summary>
internal static class ImageMetadataStripper
{
    // Görüntülemeyi etkileyen PNG yardımcı chunk'ları (renk, saydamlık, ölçek, APNG). Metin/zaman/EXIF yok.
    private static readonly HashSet<string> PngAncillaryAllowList =
        ["tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT", "bKGD", "pHYs", "hIST", "acTL", "fcTL", "fdAT"];

    public static bool TryStrip(ImageFormat format, ReadOnlySpan<byte> input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);

        return format switch
        {
            ImageFormat.Jpeg => TryStripJpeg(input, output),
            ImageFormat.Png => TryStripPng(input, output),
            ImageFormat.WebP => TryStripWebP(input, output),
            _ => false,
        };
    }

    private static bool TryStripJpeg(ReadOnlySpan<byte> data, Stream output)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return false;
        }

        output.Write(data[..2]);
        var pos = 2;
        while (pos < data.Length)
        {
            if (data[pos] != 0xFF)
            {
                return false;
            }

            // Marker öncesi dolgu baytları (FF FF …).
            while (pos < data.Length && data[pos] == 0xFF)
            {
                pos++;
            }

            if (pos >= data.Length)
            {
                return false;
            }

            var marker = data[pos++];
            if (marker == 0xD9)
            {
                output.Write([0xFF, 0xD9]);
                return true; // EOI: sonrasındaki her şey atılır.
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                output.Write([0xFF, marker]); // uzunluksuz bağımsız marker
                continue;
            }

            if (marker is 0xD8 or 0x00 || pos + 2 > data.Length)
            {
                return false;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
            if (length < 2 || pos + length > data.Length)
            {
                return false;
            }

            var segment = data.Slice(pos, length);
            if (KeepJpegSegment(marker, segment[2..]))
            {
                output.Write([0xFF, marker]);
                output.Write(segment);
            }

            pos += length;

            if (marker == 0xDA && !TryCopyEntropyCodedData(data, ref pos, output))
            {
                return false;
            }
        }

        return false; // EOI yok
    }

    // SOS sonrası entropi kodlu veri, bir sonraki gerçek marker'a kadar aynen kopyalanır.
    private static bool TryCopyEntropyCodedData(ReadOnlySpan<byte> data, ref int pos, Stream output)
    {
        var start = pos;
        while (true)
        {
            if (pos + 1 >= data.Length)
            {
                return false;
            }

            if (data[pos] != 0xFF)
            {
                pos++;
                continue;
            }

            var next = data[pos + 1];
            if (next == 0x00 || next is >= 0xD0 and <= 0xD7)
            {
                pos += 2; // bayt doldurma (FF 00) veya RSTn
                continue;
            }

            if (next == 0xFF)
            {
                pos++; // dolgu
                continue;
            }

            output.Write(data[start..pos]);
            return true; // gerçek marker: çağıran döngü işler
        }
    }

    private static bool KeepJpegSegment(byte marker, ReadOnlySpan<byte> payload) => marker switch
    {
        0xE0 => payload.StartsWith("JFIF\0"u8),
        0xE2 => payload.StartsWith("ICC_PROFILE\0"u8),
        0xEE => payload.StartsWith("Adobe"u8),
        >= 0xE1 and <= 0xEF => false, // EXIF/XMP (APP1), MPF (APP2), IPTC (APP13) ve diğer uygulama segmentleri
        0xFE => false, // COM
        _ => true, // DQT, DHT, SOFn, SOS, DRI… görüntü verisi için gerekli
    };

    private static bool TryStripPng(ReadOnlySpan<byte> data, Stream output)
    {
        const int signatureLength = 8;
        if (data.Length < signatureLength)
        {
            return false;
        }

        output.Write(data[..signatureLength]);
        var pos = signatureLength;
        var first = true;
        while (pos + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4));
            if (length > int.MaxValue - 12 || pos + 12L + length > data.Length)
            {
                return false;
            }

            var typeBytes = data.Slice(pos + 4, 4);
            foreach (var b in typeBytes)
            {
                if (!char.IsAsciiLetter((char)b))
                {
                    return false;
                }
            }

            var type = Encoding.ASCII.GetString(typeBytes);
            if (first && type != "IHDR")
            {
                return false;
            }

            first = false;
            var total = 12 + (int)length;
            if (char.IsAsciiLetterUpper(type[0]) || PngAncillaryAllowList.Contains(type))
            {
                output.Write(data.Slice(pos, total));
            }

            pos += total;
            if (type == "IEND")
            {
                return true; // IEND sonrası atılır.
            }
        }

        return false; // IEND yok veya kesik chunk
    }

    private static bool TryStripWebP(ReadOnlySpan<byte> data, Stream output)
    {
        const byte exifFlag = 0x08;
        const byte xmpFlag = 0x04;

        if (data.Length < 12)
        {
            return false;
        }

        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
        if (riffSize < 4 || 8L + riffSize > data.Length)
        {
            return false;
        }

        var end = 8 + (int)riffSize; // RIFF dışındaki sondaki veri atılır.
        var kept = new List<(int Offset, int Length)>();
        var keptSize = 4; // "WEBP"
        var pos = 12;
        while (pos < end)
        {
            if (pos + 8 > end)
            {
                return false;
            }

            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 4, 4));
            var padded = size + (size & 1);
            if (pos + 8L + padded > end)
            {
                return false;
            }

            var fourCc = data.Slice(pos, 4);
            var chunkLength = 8 + (int)padded;
            if (!fourCc.SequenceEqual("EXIF"u8) && !fourCc.SequenceEqual("XMP "u8))
            {
                kept.Add((pos, chunkLength));
                keptSize += chunkLength;
            }

            pos += chunkLength;
        }

        if (kept.Count == 0)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[12];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)keptSize);
        "WEBP"u8.CopyTo(header[8..]);
        output.Write(header);

        foreach (var (offset, length) in kept)
        {
            var chunk = data.Slice(offset, length);
            if (chunk[..4].SequenceEqual("VP8X"u8) && length > 8)
            {
                output.Write(chunk[..8]);
                output.WriteByte((byte)(chunk[8] & ~(exifFlag | xmpFlag)));
                output.Write(chunk[9..]);
            }
            else
            {
                output.Write(chunk);
            }
        }

        return true;
    }
}
