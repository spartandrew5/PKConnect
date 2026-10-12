using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PKConnect.Core.nds;

/// <summary>
/// Gen 5 (Black/White, B2W2) trainer battle sprites. Every trainer is an
/// 8-member NARC group (same layout as the Pokémon sprites in a/0/0/4):
///   0 LZ11 RGCN - 80x80 still (the front's cell source)
///   1 LZ11 RGCN - 256x128 "parts" sheet, 2D-mapped (32 tiles wide)
///   2 RECN cells, 3 LZ11 RNAN cell anims, 4 RCMN multi-cells,
///   5 RAMN multi-cell anim, 6 unknown, 7 RLCN 16-colour palette
/// A front's single cell bank is exactly the still's 6 OAM chunks; a back's
/// banks cut its throw poses and arm pieces out of the parts sheet, which the
/// multi-cell animation then positions/rotates at runtime - so the parts
/// sheet is the back's real (lossless, writable) art.
/// </summary>
public static class Gen5TrainerSpriteCodec
{
    public const int GroupSize = 8;
    public const int StillMember = 0;
    public const int SheetMember = 1;
    public const int PaletteMember = 7;

    public const int StillSize = 80;
    public const int SheetWidth = 256;
    public const int SheetHeight = 128;

    private const int TileSize = 8;
    private const int BytesPerTile = 32;

    /// <summary>
    /// The 80x80 still's tile order: OAM-sized chunks laid end to end
    /// (64x64, then the right 16px column as two 16x32, then the bottom 16px
    /// row as two 32x16, then the 16x16 corner) - matches the front RECN's
    /// cell list. Confirmed against all 95 BW trainer fronts.
    /// </summary>
    private static readonly (int X, int Y, int W, int H)[] StillChunks =
        [(0, 0, 64, 64), (64, 0, 16, 32), (64, 32, 16, 32), (0, 64, 32, 16), (32, 64, 32, 16), (64, 64, 16, 16)];

    public static Rgba32[] ReadPalette(byte[] rlcn)
    {
        var h = NcgrSpriteCodec.ReadNclrHeader(rlcn);
        var palette = new Rgba32[16];
        for (var i = 0; i < 16; i++)
        {
            palette[i] = i < h.ColorCount
                ? NitroColor.FromBgr555(BitConverter.ToUInt16(rlcn, h.ColorsOffset + i * 2))
                : new Rgba32(0, 0, 0, 0);
        }
        return palette;
    }

    /// <summary>LZ11-decompresses a member when it's compressed, else returns it as is.</summary>
    public static byte[] Unpack(byte[] member) => Lz11Codec.IsLz11(member) ? Lz11Codec.Decompress(member) : member;

    /// <summary>The 80x80 still (member 0) - a front's battle image.</summary>
    public static Image<Rgba32> DecodeStill(byte[] stillMember, Rgba32[] palette)
    {
        var rgcn = Unpack(stillMember);
        var dataOffset = NcgrSpriteCodec.ReadNcgrHeader(rgcn).DataOffset;
        var image = new Image<Rgba32>(StillSize, StillSize);
        var tile = 0;
        foreach (var (cx, cy, cw, ch) in StillChunks)
        {
            for (var ty = 0; ty < ch / TileSize; ty++)
                for (var tx = 0; tx < cw / TileSize; tx++, tile++)
                    DrawTile(rgcn, dataOffset + tile * BytesPerTile, image, cx + tx * TileSize, cy + ty * TileSize, palette);
        }
        return image;
    }

    /// <summary>
    /// The 256x128 parts sheet (member 1) - a back's throw art. Unlike the still,
    /// it's a "scanned" RGCN (CHAR +0x14 = 1): a plain linear 4bpp bitmap, two
    /// pixels per byte, low nibble first. Cells address it as a 32-tile-wide 2D grid.
    /// </summary>
    public static Image<Rgba32> DecodeSheet(byte[] sheetMember, Rgba32[] palette)
    {
        var rgcn = Unpack(sheetMember);
        var dataOffset = NcgrSpriteCodec.ReadNcgrHeader(rgcn).DataOffset;
        var image = new Image<Rgba32>(SheetWidth, SheetHeight);
        for (var i = 0; i < SheetWidth * SheetHeight && dataOffset + i / 2 < rgcn.Length; i++)
        {
            var b = rgcn[dataOffset + i / 2];
            image[i % SheetWidth, i / SheetWidth] = palette[i % 2 == 0 ? b & 0xF : b >> 4];
        }
        return image;
    }

    // ---- layout (cells + animation) ----

    /// <summary>
    /// Members that say how the parts sheet is cut up and animated: RECN cells, RNAN
    /// cell anims, RCMN multi-cells, RAMN multi-cell anim, and the unknown member 6.
    /// A sheet is only correct together with the layout it was drawn for, so a set
    /// carries its own (a ".layout" file) and the writer copies it in with the sheet.
    /// </summary>
    public static readonly int[] LayoutMembers = [2, 3, 4, 5, 6];

    private static readonly byte[] LayoutMagic = "G5LY"u8.ToArray();

    /// <summary>A group's layout members packed into one blob: "G5LY", u32 count, then (u32 length, bytes) per member.</summary>
    public static byte[] PackLayout(byte[] narc, int group)
    {
        using var output = new MemoryStream();
        output.Write(LayoutMagic);
        output.Write(BitConverter.GetBytes(LayoutMembers.Length));
        foreach (var member in LayoutMembers)
        {
            var (offset, length) = NarcArchive.GetMemberRange(narc, group * GroupSize + member);
            output.Write(BitConverter.GetBytes(length));
            output.Write(narc, offset, length);
        }
        return output.ToArray();
    }

    /// <summary>Member offset within the group (2..6) -> bytes, from a <see cref="PackLayout"/> blob.</summary>
    public static Dictionary<int, byte[]> UnpackLayout(byte[] blob)
    {
        if (blob.Length < 8 || !blob.AsSpan(0, 4).SequenceEqual(LayoutMagic))
            throw new InvalidDataException("Not a Gen 5 sprite layout file.");
        var count = BitConverter.ToInt32(blob, 4);
        if (count != LayoutMembers.Length) throw new InvalidDataException($"Layout has {count} members, expected {LayoutMembers.Length}.");
        var members = new Dictionary<int, byte[]>();
        var pos = 8;
        foreach (var member in LayoutMembers)
        {
            var length = BitConverter.ToInt32(blob, pos);
            members[member] = blob.AsSpan(pos + 4, length).ToArray();
            pos += 4 + length;
        }
        return members;
    }

    // ---- encode (writer side) ----

    /// <summary>
    /// A 16-colour palette (slot 0 = backdrop) shared by every image of one sprite
    /// group, plus a pixel -> index resolver. The backdrop is the first image's
    /// top-left pixel (the sets' convention); fully transparent pixels map to it too.
    /// </summary>
    public sealed class GroupPalette
    {
        public List<Rgba32> Colors { get; }
        private readonly Rgba32 background;
        private readonly Dictionary<Rgba32, int> exact = [];

        public GroupPalette(IReadOnlyList<Image<Rgba32>> images)
        {
            background = images[0][0, 0];
            using var combined = new Image<Rgba32>(images.Max(i => i.Width), images.Sum(i => i.Height));
            var y0 = 0;
            foreach (var image in images)
            {
                for (var y = 0; y < image.Height; y++)
                    for (var x = 0; x < combined.Width; x++)
                        combined[x, y0 + y] = x < image.Width ? Opaque(image[x, y]) : background;
                y0 += image.Height;
            }
            Colors = PaletteQuantizer.BuildPalette(combined, background, 16);
            for (var i = 0; i < Colors.Count; i++) exact.TryAdd(Colors[i], i);
        }

        private Rgba32 Opaque(Rgba32 pixel) => pixel.A < 128 ? background : pixel with { A = 255 };

        public int IndexOf(Rgba32 pixel)
        {
            var p = Opaque(pixel);
            if (p == background) return 0;
            return exact.TryGetValue(p, out var i) ? i : PaletteQuantizer.NearestIndex(Colors, p);
        }
    }

    /// <summary>The palette member with its first 16 colours replaced (BGR555).</summary>
    public static byte[] EncodePalette(byte[] rlcn, GroupPalette palette)
    {
        var output = (byte[])rlcn.Clone();
        var h = NcgrSpriteCodec.ReadNclrHeader(output);
        for (var i = 0; i < Math.Min(16, h.ColorCount); i++)
        {
            var color = i < palette.Colors.Count ? palette.Colors[i] : new Rgba32(0, 0, 0);
            BitConverter.TryWriteBytes(output.AsSpan(h.ColorsOffset + i * 2), (ushort)NitroColor.ToBgr555(color));
        }
        return output;
    }

    /// <summary>The still member (LZ11) re-encoded from an 80x80 image.</summary>
    public static byte[] EncodeStill(byte[] stillMember, Image<Rgba32> image, GroupPalette palette)
    {
        RequireSize(image, StillSize, StillSize, "front");
        var rgcn = Unpack(stillMember);
        var dataOffset = NcgrSpriteCodec.ReadNcgrHeader(rgcn).DataOffset;
        var tile = 0;
        foreach (var (cx, cy, cw, ch) in StillChunks)
        {
            for (var ty = 0; ty < ch / TileSize; ty++)
                for (var tx = 0; tx < cw / TileSize; tx++, tile++)
                    WriteTile(rgcn, dataOffset + tile * BytesPerTile, image, cx + tx * TileSize, cy + ty * TileSize, palette);
        }
        return Lz11Codec.Compress(rgcn);
    }

    /// <summary>The parts-sheet member (LZ11, scanned 4bpp) re-encoded from a 256x128 image.</summary>
    public static byte[] EncodeSheet(byte[] sheetMember, Image<Rgba32> image, GroupPalette palette)
    {
        RequireSize(image, SheetWidth, SheetHeight, "sheet");
        var rgcn = Unpack(sheetMember);
        var dataOffset = NcgrSpriteCodec.ReadNcgrHeader(rgcn).DataOffset;
        for (var i = 0; i < SheetWidth * SheetHeight; i += 2)
        {
            var lo = palette.IndexOf(image[i % SheetWidth, i / SheetWidth]);
            var hi = palette.IndexOf(image[(i + 1) % SheetWidth, (i + 1) / SheetWidth]);
            rgcn[dataOffset + i / 2] = (byte)(lo | hi << 4);
        }
        return Lz11Codec.Compress(rgcn);
    }

    /// <summary>A static (BW) front's sheet: the still at (0,0) on an otherwise empty sheet - what the retail files hold.</summary>
    public static Image<Rgba32> StillOnSheet(Image<Rgba32> still)
    {
        var background = still[0, 0];
        var sheet = new Image<Rgba32>(SheetWidth, SheetHeight, background);
        for (var y = 0; y < Math.Min(StillSize, still.Height); y++)
            for (var x = 0; x < Math.Min(StillSize, still.Width); x++)
                sheet[x, y] = still[x, y];
        return sheet;
    }

    private static void RequireSize(Image<Rgba32> image, int width, int height, string what)
    {
        if (image.Width != width || image.Height != height)
            throw new ArgumentException($"{what} image is {image.Width}x{image.Height}, expected {width}x{height}.");
    }

    private static void WriteTile(byte[] rgcn, int tileStart, Image<Rgba32> image, int x0, int y0, GroupPalette palette)
    {
        for (var i = 0; i < 64; i += 2)
        {
            var lo = palette.IndexOf(image[x0 + i % 8, y0 + i / 8]);
            var hi = palette.IndexOf(image[x0 + (i + 1) % 8, y0 + (i + 1) / 8]);
            rgcn[tileStart + i / 2] = (byte)(lo | hi << 4);
        }
    }

    private static void DrawTile(byte[] rgcn, int tileStart, Image<Rgba32> image, int x0, int y0, Rgba32[] palette)
    {
        if (tileStart + BytesPerTile > rgcn.Length) return;
        for (var i = 0; i < 64; i++)
        {
            var b = rgcn[tileStart + i / 2];
            image[x0 + i % 8, y0 + i / 8] = palette[i % 2 == 0 ? b & 0xF : b >> 4];
        }
    }
}
