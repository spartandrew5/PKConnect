namespace PKConnect.Core.graphics;

/// <summary>
/// GBA BIOS LZ77 ("LZ10") codec — the format behind SWI 0x11/0x12
/// (LZ77UnCompVram / Wram). Used by Gen 3 for compressed trainer sprites,
/// palettes and map icons.
///
/// The compressor is a port of UPR-FVX's <c>DSCmp.compressLZ10</c> (itself from
/// AlmiaE / DSDecmp). Header: tag byte 0x10, then a 3-byte little-endian
/// decompressed length. Then flag-byte groups: bit set = a 2-byte
/// back-reference (length 3..18, displacement 1..4096), bit clear = a literal.
/// </summary>
public static class Lz10Codec
{
    private const int Tag = 0x10;
    private const int MinMatch = 3;
    private const int MaxMatch = 0x12;      // 18
    private const int MaxDisplacement = 0x1000; // 4096

    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var outStream = new MemoryStream();
        outStream.WriteByte(Tag);
        outStream.WriteByte((byte)(data.Length & 0xFF));
        outStream.WriteByte((byte)((data.Length >> 8) & 0xFF));
        outStream.WriteByte((byte)((data.Length >> 16) & 0xFF));

        var cur = 0;
        var block = new byte[17];
        var blockLen = 1;
        var count = 0;

        while (cur < data.Length)
        {
            if (count == 8)
            {
                outStream.Write(block, 0, blockLen);
                block[0] = 0;
                blockLen = 1;
                count = 0;
            }

            var (matchLen, matchDisp) = FindMatch(data, cur);
            if (matchLen < MinMatch)
            {
                block[blockLen++] = data[cur++];
            }
            else
            {
                block[0] |= (byte)(1 << (7 - count));
                block[blockLen] = (byte)(((matchLen - 3) & 0xF) << 4);
                block[blockLen++] |= (byte)(((matchDisp - 1) >> 8) & 0xF);
                block[blockLen++] = (byte)((matchDisp - 1) & 0xFF);
                cur += matchLen;
            }

            count++;
        }

        if (count > 0)
        {
            outStream.Write(block, 0, blockLen);
        }

        return outStream.ToArray();
    }

    private static (int Length, int Displacement) FindMatch(ReadOnlySpan<byte> data, int cur)
    {
        var pending = Math.Min(data.Length - cur, MaxMatch);
        var finished = Math.Min(cur, MaxDisplacement);
        if (pending == 0)
        {
            return (0, 0);
        }

        var bestLen = 0;
        var bestDisp = 0;
        for (var i = 0; i < finished - 1; i++)
        {
            var candidate = cur - finished + i;
            var len = 0;
            while (len < pending && data[candidate + len] == data[cur + len])
            {
                len++;
            }
            if (len > bestLen)
            {
                bestLen = len;
                bestDisp = finished - i;
                if (len == pending)
                {
                    break;
                }
            }
        }
        return (bestLen, bestDisp);
    }

    /// <summary>Decompresses an LZ10 stream at <paramref name="offset"/>. Also returns the compressed byte length.</summary>
    public static (byte[] Data, int CompressedLength) Decompress(byte[] rom, int offset)
    {
        if (rom[offset] != Tag)
        {
            throw new InvalidDataException($"Not an LZ10 stream (tag 0x{rom[offset]:X2}) at 0x{offset:X}");
        }
        var decompressedLength = rom[offset + 1] | (rom[offset + 2] << 8) | (rom[offset + 3] << 16);
        var outBytes = new byte[decompressedLength];
        var outPos = 0;
        var pos = offset + 4;

        while (outPos < decompressedLength)
        {
            var flags = rom[pos++];
            for (var bit = 7; bit >= 0 && outPos < decompressedLength; bit--)
            {
                if ((flags & (1 << bit)) == 0)
                {
                    outBytes[outPos++] = rom[pos++];
                }
                else
                {
                    var b0 = rom[pos++];
                    var b1 = rom[pos++];
                    var length = ((b0 >> 4) & 0xF) + 3;
                    var disp = (((b0 & 0xF) << 8) | b1) + 1;
                    for (var k = 0; k < length && outPos < decompressedLength; k++)
                    {
                        outBytes[outPos] = outBytes[outPos - disp];
                        outPos++;
                    }
                }
            }
        }

        return (outBytes, pos - offset);
    }
}
