namespace PKConnect.Core.nds;

/// <summary>
/// NDS "LZ11" (LZ77 with extended lengths) codec - what Gen 5 uses for its
/// trainer battle sprites (a/0/7/2, a/0/7/3 ...), unlike Gen 3/4's LZ10 (see
/// Lz10Codec). Header: tag byte 0x11, then a 3-byte little-endian length (or
/// 0 followed by a 4-byte length when it doesn't fit). Then flag-byte groups,
/// MSB first: bit clear = literal byte; bit set = a back-reference whose first
/// nibble picks the encoding:
///   1..F : 2 bytes, length = nibble + 1 (2..16)
///   0    : 3 bytes, length = next 8 bits + 0x11 (17..272)
///   1    : 4 bytes, length = next 16 bits + 0x111 (273..65808)
/// each followed by a 12-bit displacement - 1.
/// </summary>
public static class Lz11Codec
{
    private const int Tag = 0x11;
    private const int MinMatch = 3;
    private const int MaxMatch = 0x10110; // 65808
    private const int MaxDisplacement = 0x1000;

    /// <summary>
    /// Tag byte plus a plausible length - some raw members just happen to start with
    /// 0x11. Callers that know a member is compressed should still call Decompress directly.
    /// </summary>
    public static bool IsLz11(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5 || data[0] != Tag) return false;
        var length = data[1] | data[2] << 8 | data[3] << 16;
        if (length == 0) return data.Length >= 9 && BitConverter.ToInt32(data[4..8]) > 0xFFFFFF;
        // LZ11 expands at most ~0x10110x per back-reference; a tiny stream can't claim a huge output.
        return length <= (data.Length - 4) * 0x10110L;
    }

    public static byte[] Decompress(ReadOnlySpan<byte> src)
    {
        if (!IsLz11(src)) throw new InvalidDataException($"Not an LZ11 stream (tag 0x{(src.Length > 0 ? src[0] : 0):X2}).");
        var length = src[1] | src[2] << 8 | src[3] << 16;
        var pos = 4;
        if (length == 0)
        {
            length = BitConverter.ToInt32(src[4..8]);
            pos = 8;
        }

        var output = new byte[length];
        var outPos = 0;
        while (outPos < length)
        {
            var flags = src[pos++];
            for (var bit = 7; bit >= 0 && outPos < length; bit--)
            {
                if ((flags & (1 << bit)) == 0)
                {
                    output[outPos++] = src[pos++];
                    continue;
                }

                int matchLength, disp;
                var indicator = src[pos] >> 4;
                if (indicator == 0)
                {
                    matchLength = ((src[pos] & 0xF) << 4 | src[pos + 1] >> 4) + 0x11;
                    disp = ((src[pos + 1] & 0xF) << 8 | src[pos + 2]) + 1;
                    pos += 3;
                }
                else if (indicator == 1)
                {
                    matchLength = ((src[pos] & 0xF) << 12 | src[pos + 1] << 4 | src[pos + 2] >> 4) + 0x111;
                    disp = ((src[pos + 2] & 0xF) << 8 | src[pos + 3]) + 1;
                    pos += 4;
                }
                else
                {
                    matchLength = indicator + 1;
                    disp = ((src[pos] & 0xF) << 8 | src[pos + 1]) + 1;
                    pos += 2;
                }

                if (disp > outPos) throw new InvalidDataException("LZ11 back-reference before the start of the output.");
                for (var k = 0; k < matchLength && outPos < length; k++, outPos++)
                {
                    output[outPos] = output[outPos - disp];
                }
            }
        }
        return output;
    }

    /// <summary>Greedy LZ11 compressor. Sprite payloads are small (&lt;= 16KB), so a plain window scan is fine.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        if (data.Length <= 0xFFFFFF)
        {
            output.WriteByte(Tag);
            output.WriteByte((byte)data.Length);
            output.WriteByte((byte)(data.Length >> 8));
            output.WriteByte((byte)(data.Length >> 16));
        }
        else
        {
            output.WriteByte(Tag);
            output.WriteByte(0); output.WriteByte(0); output.WriteByte(0);
            output.Write(BitConverter.GetBytes(data.Length));
        }

        var pos = 0;
        var block = new List<byte>(1 + 8 * 4);
        while (pos < data.Length)
        {
            block.Clear();
            block.Add(0);
            for (var bit = 7; bit >= 0 && pos < data.Length; bit--)
            {
                var (len, disp) = FindMatch(data, pos);
                if (len < MinMatch)
                {
                    block.Add(data[pos++]);
                    continue;
                }

                block[0] |= (byte)(1 << bit);
                var d = disp - 1;
                if (len <= 0x10)
                {
                    block.Add((byte)((len - 1) << 4 | d >> 8));
                    block.Add((byte)d);
                }
                else if (len <= 0x110)
                {
                    var l = len - 0x11;
                    block.Add((byte)(l >> 4));
                    block.Add((byte)((l & 0xF) << 4 | d >> 8));
                    block.Add((byte)d);
                }
                else
                {
                    var l = len - 0x111;
                    block.Add((byte)(0x10 | l >> 12));
                    block.Add((byte)(l >> 4));
                    block.Add((byte)((l & 0xF) << 4 | d >> 8));
                    block.Add((byte)d);
                }
                pos += len;
            }
            output.Write(block.ToArray());
        }

        // Keep the stream 4-byte aligned, as the retail files are.
        while (output.Length % 4 != 0) output.WriteByte(0);
        return output.ToArray();
    }

    private static (int Length, int Displacement) FindMatch(ReadOnlySpan<byte> data, int pos)
    {
        var bestLen = 0;
        var bestDisp = 0;
        var maxLen = Math.Min(MaxMatch, data.Length - pos);
        if (maxLen < MinMatch) return (0, 0);

        var start = Math.Max(0, pos - MaxDisplacement);
        for (var candidate = pos - 1; candidate >= start; candidate--)
        {
            if (data[candidate] != data[pos]) continue;
            var len = 1;
            // Overlapping matches (candidate + len past pos) are valid: the decoder copies byte by byte.
            while (len < maxLen && data[candidate + len] == data[pos + len]) len++;
            if (len > bestLen)
            {
                bestLen = len;
                bestDisp = pos - candidate;
                if (len == maxLen) break;
            }
        }
        return (bestLen, bestDisp);
    }
}
