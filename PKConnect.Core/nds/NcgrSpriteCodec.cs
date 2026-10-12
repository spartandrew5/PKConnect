using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PKConnect.Core.nds;

/// <summary>
/// Codec for the classic Nitro character/palette pair - NCGR ("RGCN" on
/// disk, reversed-magic like every Nitro sub-chunk) + NCLR ("RLCN") - used by
/// HGSS's front/back battle sprites (a/0/5/8, a/0/0/6), unlike the overworld
/// sprites' BTX0 texture format. Confirmed against real HG bytes: depth=3
/// (4bpp/16-colour, same as BTX0's Color16), tiledFlag low byte 0 (row-major
/// 8x8 tile blocks - the same "Lineal" layout GBA sprites use, see
/// GbaTileCodec), and nTilesX/Y = 0xFFFF (the file doesn't record its own
/// pixel width - only the paired NCER cell data does, so callers must supply
/// <paramref name="tilesWide"/> from a hardcoded, verified value per slot
/// rather than anything read from the file itself).
///
/// Layout, ported from Tinke's Plugins/Images/Images/NCGR.cs + NCLR.cs:
///   NCGR: [0x00 outer header, 16B][0x10 RAHC: id,size,nTilesY,nTilesX,depth,
///     unk1,unk2,tiledFlag,size_tiledata,unk3][0x30 tile data]
///   NCLR: [0x00 outer header, 16B][0x10 PLTT: id,length,depth,unk1,unk2,
///     pal_length,colours_start_offset][colours at PLTT_start+8+colours_start_offset]
/// </summary>
public static class NcgrSpriteCodec
{
    private const int TileSize = 8;
    private const int BytesPerTile = 32; // 8x8 4bpp

    public readonly record struct NcgrHeader(int DataOffset, int DataLength, int Depth);
    public readonly record struct NclrHeader(int ColorsOffset, int ColorCount);

    public static NcgrHeader ReadNcgrHeader(byte[] ncgr)
    {
        if (Tag(ncgr, 0) != "RGCN") throw new InvalidDataException("Not an NCGR file.");
        var rahcOffset = ReadU16(ncgr, 0x0C);
        if (Tag(ncgr, rahcOffset) != "RAHC") throw new InvalidDataException("Expected a RAHC (CHAR) section.");

        var depth = (int)ReadU32(ncgr, rahcOffset + 12);
        var sizeTiledata = (int)ReadU32(ncgr, rahcOffset + 24);
        var dataOffset = rahcOffset + 0x20;
        return new NcgrHeader(dataOffset, sizeTiledata, depth);
    }

    public static NclrHeader ReadNclrHeader(byte[] nclr)
    {
        if (Tag(nclr, 0) != "RLCN") throw new InvalidDataException("Not an NCLR file.");
        var plttOffset = ReadU16(nclr, 0x0C);
        if (Tag(nclr, plttOffset) != "TTLP") throw new InvalidDataException("Expected a TTLP (PLTT) section.");

        var depth = ReadU16(nclr, plttOffset + 8);
        var palLength = (int)ReadU32(nclr, plttOffset + 0x10);
        var colorsStartOffset = (int)ReadU32(nclr, plttOffset + 0x14);
        var colorCount = depth == 4 ? 256 : 16;
        var colorsOffset = plttOffset + 8 + colorsStartOffset;
        return new NclrHeader(colorsOffset, Math.Min(colorCount, palLength / 2));
    }

    /// <summary><paramref name="tilesWide"/>: the sprite's width in 8px tiles - see the class doc for why this can't be read from the file.</summary>
    public static Image<Rgba32> Decode(byte[] ncgr, byte[] nclr, int tilesWide)
    {
        var ch = ReadNcgrHeader(ncgr);
        if (ch.Depth != 3) throw new NotSupportedException($"NCGR depth {ch.Depth} is not supported (only 4bpp/16-colour).");

        var lh = ReadNclrHeader(nclr);
        var palette = new Rgba32[lh.ColorCount];
        for (var i = 0; i < lh.ColorCount; i++)
        {
            palette[i] = NitroColor.FromBgr555(ReadU16(nclr, lh.ColorsOffset + i * 2));
        }

        var numTiles = ch.DataLength / BytesPerTile;
        var tilesTall = (numTiles + tilesWide - 1) / tilesWide;
        var width = tilesWide * TileSize;
        var height = tilesTall * TileSize;
        var image = new Image<Rgba32>(width, height);

        for (var t = 0; t < numTiles; t++)
        {
            var tx = (t % tilesWide) * TileSize;
            var ty = (t / tilesWide) * TileSize;
            var tileStart = ch.DataOffset + t * BytesPerTile;
            for (var row = 0; row < TileSize; row++)
            {
                for (var col = 0; col < TileSize; col += 2)
                {
                    var b = ncgr[tileStart + row * 4 + col / 2];
                    image[tx + col, ty + row] = palette[b & 0xF];
                    image[tx + col + 1, ty + row] = palette[b >> 4];
                }
            }
        }
        return image;
    }

    /// <summary>
    /// Re-encodes <paramref name="replacement"/> into <paramref name="ncgr"/> and
    /// <paramref name="nclr"/> in place (both stay exactly the same length -
    /// same-size overwrite, same reasoning as Btx0SpriteCodec). Dimensions
    /// must be a whole number of 8x8 tiles matching the original tile count.
    /// </summary>
    public static void Encode(byte[] ncgr, byte[] nclr, Image<Rgba32> replacement, int tilesWide)
    {
        var ch = ReadNcgrHeader(ncgr);
        if (ch.Depth != 3) throw new NotSupportedException($"NCGR depth {ch.Depth} is not supported (only 4bpp/16-colour).");

        var numTiles = ch.DataLength / BytesPerTile;
        var tilesTall = (numTiles + tilesWide - 1) / tilesWide;
        var expectedWidth = tilesWide * TileSize;
        var expectedHeight = tilesTall * TileSize;
        if (replacement.Width != expectedWidth || replacement.Height != expectedHeight)
        {
            throw new ArgumentException(
                $"Replacement image is {replacement.Width}x{replacement.Height}, expected {expectedWidth}x{expectedHeight}.");
        }

        var lh = ReadNclrHeader(nclr);
        var background = replacement[0, 0];
        var palette = PaletteQuantizer.BuildPalette(replacement, background, lh.ColorCount);
        var exactIndex = new Dictionary<Rgba32, int>();
        for (var i = 0; i < palette.Count; i++) exactIndex[palette[i]] = i;

        int ResolveIndex(Rgba32 color) =>
            exactIndex.TryGetValue(color, out var exact) ? exact : PaletteQuantizer.NearestIndex(palette, color);

        for (var t = 0; t < numTiles; t++)
        {
            var tx = (t % tilesWide) * TileSize;
            var ty = (t / tilesWide) * TileSize;
            var tileStart = ch.DataOffset + t * BytesPerTile;
            for (var row = 0; row < TileSize; row++)
            {
                for (var col = 0; col < TileSize; col += 2)
                {
                    var lo = ResolveIndex(replacement[tx + col, ty + row]);
                    var hi = ResolveIndex(replacement[tx + col + 1, ty + row]);
                    ncgr[tileStart + row * 4 + col / 2] = (byte)(lo | (hi << 4));
                }
            }
        }

        for (var c = 0; c < palette.Count; c++)
        {
            var bgr555 = NitroColor.ToBgr555(palette[c]);
            nclr[lh.ColorsOffset + c * 2] = (byte)bgr555;
            nclr[lh.ColorsOffset + c * 2 + 1] = (byte)(bgr555 >> 8);
        }
    }

    private static string Tag(byte[] data, int offset) => System.Text.Encoding.ASCII.GetString(data, offset, 4);
    private static uint ReadU32(byte[] data, int offset) => BitConverter.ToUInt32(data, offset);
    private static ushort ReadU16(byte[] data, int offset) => BitConverter.ToUInt16(data, offset);
}
