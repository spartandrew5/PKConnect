using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PKConnect.Core.nds;

/// <summary>
/// Codec for the "Color16" (format 3: 4bpp / 16-colour indexed) variant of
/// the NDS BTX0 texture format - what HGSS's overworld character sprites
/// turned out to use (confirmed against a real extracted "1_69.BTX0": its
/// palette section is exactly 32 bytes = 16 BGR555 colours). Ported from
/// DSPRE's <c>LibNDSFormats/BTX0.cs</c> (DS_Map/LibNDSFormats/BTX0.cs in
/// github.com/DS-Pokemon-Rom-Editor/DSPRE), which itself only supports this
/// one format - the same format every overworld/trainer sprite reskin needs,
/// since they're flat pixel-art, not photographic 3D-model textures (those
/// use the block-compressed format this codec doesn't handle).
///
/// Both directions only touch the pixel-data and palette-data byte ranges in
/// place; the BTX0 stays exactly the same length, so no NARC/ROM restructuring
/// is ever needed for a same-size reskin.
/// </summary>
public static class Btx0SpriteCodec
{
    private const int Color16Format = 3;

    public readonly record struct Header(int ImageOffset, int PaletteOffset, int Width, int Height, int ColorCount, int PaletteCount, int Format);

    public static Header ReadHeader(byte[] btx)
    {
        if (Tag(btx, 0) != "BTX0") throw new InvalidDataException("Not a BTX0 file.");

        var tex0Offset = (int)ReadU32(btx, 0x10);
        if (Tag(btx, tex0Offset) != "TEX0") throw new InvalidDataException("Expected a TEX0 section.");

        var textInfoOffset = tex0Offset + ReadU16(btx, tex0Offset + 14);
        var imageOffset = tex0Offset + (int)ReadU32(btx, tex0Offset + 20);
        var paletteDataSize = (int)ReadU32(btx, tex0Offset + 48) << 3;
        var paletteInfoOffset = tex0Offset + (int)ReadU32(btx, tex0Offset + 52);
        var paletteOffset = tex0Offset + (int)ReadU32(btx, tex0Offset + 56);

        var numTexObjs = btx[textInfoOffset + 1];
        // First texture object's "parameters" field: skip the 4-byte texInfo
        // header, the 8-byte unknown-block header, the num_objs*4-byte unknown
        // array, then the 4-byte info-block header, landing on tex_offset (u16)
        // + parameters (u16) for object 0.
        var parameters = ReadU16(btx, textInfoOffset + 12 + numTexObjs * 4 + 6);
        var width = 8 << ((parameters >> 4) & 7);
        var format = (parameters >> 10) & 7;

        var paletteCount = btx[paletteInfoOffset + 1];
        var colorCount = paletteCount > 0 ? paletteDataSize / paletteCount / 2 : 0;
        var height = width > 0 ? (paletteOffset - imageOffset) * 2 / width : 0;

        return new Header(imageOffset, paletteOffset, width, height, colorCount, paletteCount, format);
    }

    public static Image<Rgba32> Decode(byte[] btx, int paletteIndex = 0)
    {
        var h = ReadHeader(btx);
        if (h.Format != Color16Format)
        {
            throw new NotSupportedException($"BTX0 texture format {h.Format} is not supported (only Color16/4bpp).");
        }

        var palette = ReadPalette(btx, h, paletteIndex);
        var image = new Image<Rgba32>(h.Width, h.Height);

        var x = 0;
        var y = 0;
        for (var i = h.ImageOffset; i < h.PaletteOffset && y < h.Height; i++)
        {
            var packed = btx[i];
            SetPixel(image, ref x, ref y, h.Width, palette[packed & 0xF]);
            SetPixel(image, ref x, ref y, h.Width, palette[packed >> 4]);
        }

        return image;
    }

    /// <summary>
    /// Re-encodes <paramref name="replacement"/> (same dimensions as the
    /// original, at most 16 distinct colours) into <paramref name="btx"/> in
    /// place and returns it. Unlike DSPRE's version, the colour at (0,0) is
    /// always forced into palette slot 0 - the convention these sprites rely
    /// on for their transparent background - rather than whichever slot a
    /// hash-set happens to assign it.
    /// </summary>
    public static byte[] Encode(byte[] btx, Image<Rgba32> replacement, int paletteIndex = 0)
    {
        var h = ReadHeader(btx);
        if (h.Format != Color16Format)
        {
            throw new NotSupportedException($"BTX0 texture format {h.Format} is not supported (only Color16/4bpp).");
        }
        if (replacement.Width != h.Width || replacement.Height != h.Height)
        {
            throw new ArgumentException(
                $"Replacement image is {replacement.Width}x{replacement.Height}, expected {h.Width}x{h.Height}.");
        }

        var background = replacement[0, 0];
        var palette = PaletteQuantizer.BuildPalette(replacement, background, h.ColorCount);
        var exactIndex = new Dictionary<Rgba32, int>();
        for (var i = 0; i < palette.Count; i++) exactIndex[palette[i]] = i;

        int ResolveIndex(Rgba32 color) =>
            exactIndex.TryGetValue(color, out var exact) ? exact : PaletteQuantizer.NearestIndex(palette, color);

        var x = 0;
        var y = 0;
        for (var i = h.ImageOffset; i < h.PaletteOffset && y < h.Height; i++)
        {
            var lo = ResolveIndex(GetPixel(replacement, ref x, ref y, h.Width));
            var hi = ResolveIndex(GetPixel(replacement, ref x, ref y, h.Width));
            btx[i] = (byte)(lo | (hi << 4));
        }

        var paletteBase = h.PaletteOffset + paletteIndex * h.ColorCount * 2;
        for (var c = 0; c < palette.Count; c++)
        {
            var bgr555 = NitroColor.ToBgr555(palette[c]);
            btx[paletteBase + c * 2] = (byte)bgr555;
            btx[paletteBase + c * 2 + 1] = (byte)(bgr555 >> 8);
        }

        return btx;
    }

    private static Rgba32 GetPixel(Image<Rgba32> image, ref int x, ref int y, int width)
    {
        var pixel = image[x, y];
        Advance(ref x, ref y, width);
        return pixel;
    }

    private static void SetPixel(Image<Rgba32> image, ref int x, ref int y, int width, Rgba32 color)
    {
        if (y < image.Height) image[x, y] = color;
        Advance(ref x, ref y, width);
    }

    private static void Advance(ref int x, ref int y, int width)
    {
        x++;
        if (x >= width)
        {
            x = 0;
            y++;
        }
    }

    private static Rgba32[] ReadPalette(byte[] btx, Header h, int paletteIndex)
    {
        var colors = new Rgba32[h.ColorCount];
        var baseOffset = h.PaletteOffset + paletteIndex * h.ColorCount * 2;
        for (var i = 0; i < h.ColorCount; i++)
        {
            var bgr555 = ReadU16(btx, baseOffset + i * 2);
            colors[i] = NitroColor.FromBgr555(bgr555);
        }
        return colors;
    }

    private static string Tag(byte[] data, int offset) => System.Text.Encoding.ASCII.GetString(data, offset, 4);
    private static uint ReadU32(byte[] data, int offset) => BitConverter.ToUInt32(data, offset);
    private static ushort ReadU16(byte[] data, int offset) => BitConverter.ToUInt16(data, offset);
}
