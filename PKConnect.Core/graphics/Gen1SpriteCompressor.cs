namespace PKConnect.Core.graphics;

/// <summary>
/// Pokémon Generation 1 sprite compressor / decompressor.
///
/// Direct port of pokered's canonical <c>tools/pkmncompress.c</c> (public
/// domain, from the pret disassembly) — the same algorithm the games use, so
/// anything this produces the in-game decompressor will accept. Verified in
/// Python against retail Red/Yellow: decompressing a ROM sprite renders it
/// correctly, and compress→decompress round-trips byte-exact.
///
/// Input to <see cref="Compress"/> is raw row-major 2bpp tile data
/// (<see cref="GbTileCodec.Encode"/> output): a square sprite of
/// <c>width*width</c> 8x8 tiles, 16 bytes each, up to 15x15 tiles.
/// </summary>
public static class Gen1SpriteCompressor
{
    private static readonly int[][] GrayCodes =
    [
        [0x0, 0x1, 0x3, 0x2, 0x6, 0x7, 0x5, 0x4, 0xC, 0xD, 0xF, 0xE, 0xA, 0xB, 0x9, 0x8],
        [0x8, 0x9, 0xB, 0xA, 0xE, 0xF, 0xD, 0xC, 0x4, 0x5, 0x7, 0x6, 0x2, 0x3, 0x1, 0x0],
    ];

    private static readonly int[][] UngrayCodes =
    [
        [0x0, 0x1, 0x3, 0x2, 0x7, 0x6, 0x4, 0x5, 0xF, 0xE, 0xC, 0xD, 0x8, 0x9, 0xB, 0xA],
        [0xF, 0xE, 0xC, 0xD, 0x8, 0x9, 0xB, 0xA, 0x0, 0x1, 0x3, 0x2, 0x7, 0x6, 0x4, 0x5],
    ];

    private static readonly int[] FillTable =
    [
        0x0001, 0x0003, 0x0007, 0x000F, 0x001F, 0x003F, 0x007F, 0x00FF,
        0x01FF, 0x03FF, 0x07FF, 0x0FFF, 0x1FFF, 0x3FFF, 0x7FFF, 0xFFFF,
    ];

    // ---- bit stream state (module-static, mirrors pkmncompress.c) ----
    private const int OutputCapacity = 15 * 15 * 0x10;

    private sealed class BitWriter
    {
        public readonly byte[] Output = new byte[OutputCapacity];
        private int _curBit;
        private int _curByte;

        public void Reset()
        {
            Array.Clear(Output);
            _curBit = 7;
            _curByte = 0;
        }

        public void WriteBit(int bit)
        {
            if (++_curBit == 8)
            {
                _curByte++;
                _curBit = 0;
            }
            Output[_curByte] |= (byte)(bit << (7 - _curBit));
        }

        /// <summary>Bit length so far (matches pkmncompress's return value).</summary>
        public int BitLength => (_curByte + 1) * 8 + _curBit;
    }

    private sealed class BitReader(byte[] data)
    {
        private int _curBit = 7;
        private int _curByte;

        public int ReadBit()
        {
            if (_curBit == -1)
            {
                _curByte++;
                _curBit = 7;
            }
            return (data[_curByte] >> _curBit--) & 1;
        }

        public int ReadInt(int count)
        {
            var n = 0;
            while (count-- > 0)
            {
                n = (n << 1) | ReadBit();
            }
            return n;
        }

        /// <summary>Bytes consumed (rounded up), = compressed length.</summary>
        public int BytesConsumed => _curByte + 1;
    }

    // ================= COMPRESS =================

    /// <param name="tileData">Row-major 2bpp tile bytes (16 bytes/tile), square.</param>
    /// <param name="width">Sprite width in tiles (== height in tiles).</param>
    public static byte[] Compress(ReadOnlySpan<byte> tileData, int width)
    {
        if (width is < 1 or > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "must be 1..15 tiles");
        }
        if (tileData.Length != width * width * 0x10)
        {
            throw new ArgumentException(
                $"tileData is {tileData.Length} bytes, expected {width * width * 0x10} for {width}x{width} tiles.");
        }

        var data = tileData.ToArray();
        TransposeTiles(data, width);

        var ramSize = width * width * 8;
        var planes = new byte[2][];
        planes[0] = new byte[ramSize];
        planes[1] = new byte[ramSize];
        for (var i = 0; i < ramSize; i++)
        {
            planes[0][i] = data[i * 2];
            planes[1][i] = data[i * 2 + 1];
        }

        var writer = new BitWriter();
        byte[]? best = null;

        for (var mode = 0; mode < 3; mode++)
        {
            for (var order = 0; order < 2; order++)
            {
                if (mode == 0 && order == 0)
                {
                    continue;
                }

                var bitLen = InterpretCompress(writer, planes, mode, order, width);
                // Matches pkmncompress.c: compressed_size / 8 (bitLen is
                // (curByte+1)*8 + curBit with curBit < 8, so this is curByte+1).
                var byteLen = bitLen / 8;
                if (best is null || byteLen < best.Length)
                {
                    best = writer.Output[..byteLen];
                }
            }
        }

        return best!;
    }

    private static int InterpretCompress(BitWriter w, byte[][] planes, int mode, int order, int width)
    {
        var ramSize = width * width * 8;
        var rams = new byte[2][];
        rams[0] = (byte[])planes[order].Clone();
        rams[1] = (byte[])planes[order ^ 1].Clone();

        if (mode != 0)
        {
            for (var i = 0; i < ramSize; i++)
            {
                rams[1][i] ^= rams[0][i];
            }
        }

        CompressPlane(rams[0], width);
        if (mode != 1)
        {
            CompressPlane(rams[1], width);
        }

        w.Reset();
        w.Output[0] = (byte)((width << 4) | width);
        w.WriteBit(order);

        var bitGroups = new byte[15 * 4 * 15 * 8];
        var index = 0;

        for (var plane = 0; plane < 2; plane++)
        {
            var type = 0;
            var nums = 0;
            Array.Clear(bitGroups);
            index = 0;

            for (var x = 0; x < width; x++)
            {
                for (var bit = 0; bit < 8; bit += 2)
                {
                    for (int y = 0, b = x * width * 8; y < width * 8; y++, b++)
                    {
                        var bitGroup = (rams[plane][b] >> (6 - bit)) & 3;
                        if (bitGroup != 0)
                        {
                            if (type == 0)
                            {
                                w.WriteBit(1);
                            }
                            else if (type == 1)
                            {
                                RleEncodeNumber(w, nums);
                            }
                            type = 2;
                            bitGroups[index++] = (byte)bitGroup;
                            nums = 0;
                        }
                        else
                        {
                            if (type == 0)
                            {
                                w.WriteBit(0);
                            }
                            else if (type == 1)
                            {
                                nums++;
                            }
                            else
                            {
                                WriteDataPacket(w, bitGroups, index);
                                w.WriteBit(0);
                                w.WriteBit(0);
                            }
                            type = 1;
                            Array.Clear(bitGroups);
                            index = 0;
                        }
                    }
                }
            }

            if (type == 1)
            {
                RleEncodeNumber(w, nums);
            }
            else
            {
                WriteDataPacket(w, bitGroups, index);
            }

            if (plane == 0)
            {
                if (mode == 0)
                {
                    w.WriteBit(0);
                }
                else
                {
                    w.WriteBit(1);
                    w.WriteBit(mode - 1);
                }
            }
        }

        return w.BitLength;
    }

    private static void CompressPlane(byte[] plane, int width)
    {
        var ramSize = width * width * 8;
        var nybbleLo = 0;
        for (var i = 0; i < ramSize; i++)
        {
            var m = i % width;
            if (m == 0)
            {
                nybbleLo = 0;
            }
            var j = i / width + m * width * 8;
            var nybbleHi = (plane[j] >> 4) & 0xF;
            var codeHi = GrayCodes[nybbleLo & 1][nybbleHi];
            nybbleLo = plane[j] & 0xF;
            var codeLo = GrayCodes[nybbleHi & 1][nybbleLo];
            plane[j] = (byte)((codeHi << 4) | codeLo);
        }
    }

    private static void RleEncodeNumber(BitWriter w, int n)
    {
        n++;
        var v = n + 1;
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        v -= v >> 1;
        v--;
        var number = n - v;

        var bitCount = -1;
        var vv = v;
        while (vv != 0)
        {
            vv >>= 1;
            bitCount++;
        }

        for (var j = 0; j < bitCount; j++)
        {
            w.WriteBit(1);
        }
        w.WriteBit(0);
        for (var j = bitCount; j >= 0; j--)
        {
            w.WriteBit((number >> j) & 1);
        }
    }

    private static void WriteDataPacket(BitWriter w, byte[] bitGroups, int n)
    {
        for (var i = 0; i < n; i++)
        {
            w.WriteBit((bitGroups[i] >> 1) & 1);
            w.WriteBit(bitGroups[i] & 1);
        }
    }

    // ================= DECOMPRESS =================

    /// <summary>Decompresses a Gen 1 sprite, returning row-major 2bpp tile data.</summary>
    public static byte[] Uncompress(byte[] rom, int offset)
    {
        return UncompressInner(rom, offset, out _);
    }

    /// <summary>
    /// Length in bytes of the compressed Gen 1 sprite starting at <paramref name="offset"/>.
    /// </summary>
    public static int GetCompressedLength(byte[] rom, int offset)
    {
        UncompressInner(rom, offset, out var consumed);
        return consumed;
    }

    private static byte[] UncompressInner(byte[] rom, int offset, out int consumed)
    {
        var data = new byte[rom.Length - offset];
        Array.Copy(rom, offset, data, 0, data.Length);
        var r = new BitReader(data);

        var width = r.ReadInt(4);
        if (r.ReadInt(4) != width)
        {
            throw new InvalidDataException("Gen1 sprite is not square");
        }

        var size = width * width * 8;
        var rams = new byte[2][];
        var order = r.ReadBit();
        rams[order] = FillPlane(r, width);
        var mode = r.ReadBit();
        if (mode != 0)
        {
            mode += r.ReadBit();
        }
        rams[order ^ 1] = FillPlane(r, width);

        UncompressPlane(rams[order], width);
        if (mode != 1)
        {
            UncompressPlane(rams[order ^ 1], width);
        }
        if (mode != 0)
        {
            for (var i = 0; i < size; i++)
            {
                rams[order ^ 1][i] ^= rams[order][i];
            }
        }

        var output = new byte[size * 2];
        for (var i = 0; i < size; i++)
        {
            output[i * 2] = rams[0][i];
            output[i * 2 + 1] = rams[1][i];
        }
        TransposeTiles(output, width);

        consumed = r.BytesConsumed;
        return output;
    }

    private static byte[] FillPlane(BitReader r, int width)
    {
        var mode = r.ReadBit();
        var size = width * width * 0x20;
        var plane = new byte[size];
        var len = 0;
        while (len < size)
        {
            if (mode != 0)
            {
                while (len < size)
                {
                    var bitGroup = r.ReadInt(2);
                    if (bitGroup == 0)
                    {
                        break;
                    }
                    plane[len++] = (byte)bitGroup;
                }
            }
            else
            {
                var w = 0;
                while (r.ReadBit() != 0)
                {
                    w++;
                }
                if (w >= FillTable.Length)
                {
                    throw new InvalidDataException("Invalid Gen1 compressed data");
                }
                var n = FillTable[w] + r.ReadInt(w + 1);
                while (len < size && n-- > 0)
                {
                    plane[len++] = 0;
                }
            }
            mode ^= 1;
        }

        var ram = new byte[size];
        len = 0;
        for (var y = 0; y < width; y++)
        {
            for (var x = 0; x < width * 8; x++)
            {
                for (var i = 0; i < 4; i++)
                {
                    ram[len++] = plane[(y * 4 + i) * width * 8 + x];
                }
            }
        }
        for (var i = 0; i < size - 3; i += 4)
        {
            ram[i / 4] = (byte)((ram[i] << 6) | (ram[i + 1] << 4) | (ram[i + 2] << 2) | ram[i + 3]);
        }
        return ram;
    }

    private static void UncompressPlane(byte[] plane, int width)
    {
        for (var x = 0; x < width * 8; x++)
        {
            var bit = 0;
            for (var y = 0; y < width; y++)
            {
                var i = y * width * 8 + x;
                var nybbleHi = (plane[i] >> 4) & 0xF;
                var codeHi = UngrayCodes[bit][nybbleHi];
                bit = codeHi & 1;
                var nybbleLo = plane[i] & 0xF;
                var codeLo = UngrayCodes[bit][nybbleLo];
                bit = codeLo & 1;
                plane[i] = (byte)((codeHi << 4) | codeLo);
            }
        }
    }

    // ================= shared =================

    private static void TransposeTiles(byte[] data, int width)
    {
        var size = width * width;
        for (var i = 0; i < size; i++)
        {
            var j = (i * width + i / width) % size;
            if (i < j)
            {
                Span<byte> tmp = stackalloc byte[0x10];
                var p = data.AsSpan(i * 0x10, 0x10);
                var q = data.AsSpan(j * 0x10, 0x10);
                p.CopyTo(tmp);
                q.CopyTo(p);
                tmp.CopyTo(q);
            }
        }
    }
}
