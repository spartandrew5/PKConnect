using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PKConnect.Core.graphics;

/// <summary>
/// Encodes the FRLG Oak-speech intro portrait: an 8bpp (one byte per pixel)
/// image of 8x8 row-major tiles plus a 32-colour BGR555 palette that the game
/// loads at colour index <see cref="Gen3IntroSlot.PaletteBaseColorIndex"/>.
///
/// Fully-transparent pixels become byte 0 (the scene's global backdrop colour,
/// same as vanilla); opaque pixels become <c>64 + index</c> into the 32-colour
/// palette built from the image.
/// </summary>
public static class Gen3IntroImage
{
    public static (byte[] Tiles, byte[] Palette)? Build(Image<Rgba32> image)
    {
        if (image.Width != Gen3IntroSlot.WidthPx || image.Height != Gen3IntroSlot.HeightPx)
        {
            return null;
        }

        var palette = BuildPalette(image);
        if (palette is null)
        {
            return null;
        }

        var w = image.Width;
        var h = image.Height;
        var indices = new byte[w * h];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < w; x++)
                {
                    var p = row[x];
                    indices[y * w + x] = p.A == 0
                        ? (byte)0
                        : (byte)(Gen3IntroSlot.PaletteBaseColorIndex
                                 + Nearest(palette, (p.R << 16) | (p.G << 8) | p.B));
                }
            }
        });

        var tilesWide = w / GbaTileCodec.TileSize;
        var tilesTall = h / GbaTileCodec.TileSize;
        var tiles = new byte[tilesWide * tilesTall * 64];
        for (var t = 0; t < tilesWide * tilesTall; t++)
        {
            var tx = (t % tilesWide) * GbaTileCodec.TileSize;
            var ty = (t / tilesWide) * GbaTileCodec.TileSize;
            for (var row = 0; row < GbaTileCodec.TileSize; row++)
            {
                for (var col = 0; col < GbaTileCodec.TileSize; col++)
                {
                    tiles[t * 64 + row * 8 + col] = indices[(ty + row) * w + tx + col];
                }
            }
        }

        var palBytes = new byte[Gen3IntroSlot.PaletteColors * 2];
        for (var i = 0; i < Gen3IntroSlot.PaletteColors; i++)
        {
            var c = palette[i];
            var v = (((c >> 16) & 0xFF) >> 3) | ((((c >> 8) & 0xFF) >> 3) << 5) | (((c & 0xFF) >> 3) << 10);
            palBytes[i * 2] = (byte)(v & 0xFF);
            palBytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return (tiles, palBytes);
    }

    /// <summary>
    /// The 32 most-used opaque colours, snapped to GBA precision (so a "reduce
    /// to 32 colours" export's channel noise collapses). Transparency is carried
    /// by pixel alpha, not a palette slot, so no slot is reserved. Returns null
    /// only if the image has no opaque pixels.
    /// </summary>
    private static int[]? BuildPalette(Image<Rgba32> image)
    {
        var counts = new Dictionary<int, int>();
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                foreach (var p in accessor.GetRowSpan(y))
                {
                    if (p.A == 0)
                    {
                        continue;
                    }
                    var rgb = GbaPalette.SnapToGba((p.R << 16) | (p.G << 8) | p.B);
                    counts[rgb] = counts.GetValueOrDefault(rgb) + 1;
                }
            }
        });
        if (counts.Count == 0)
        {
            return null;
        }

        var colors = new int[Gen3IntroSlot.PaletteColors];
        var i = 0;
        foreach (var (rgb, _) in counts.OrderByDescending(kv => kv.Value).Take(Gen3IntroSlot.PaletteColors))
        {
            colors[i++] = rgb;
        }
        // pad the tail with the last real colour so Nearest never picks black-0
        for (; i < colors.Length; i++)
        {
            colors[i] = colors[i - 1];
        }
        return colors;
    }

    private static int Nearest(int[] palette, int rgb)
    {
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        var best = 0;
        var bestDist = int.MaxValue;
        for (var i = 0; i < palette.Length; i++)
        {
            var c = palette[i];
            var dr = ((c >> 16) & 0xFF) - r;
            var dg = ((c >> 8) & 0xFF) - g;
            var db = (c & 0xFF) - b;
            var dist = dr * dr + dg * dg + db * db;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }
        return best;
    }
}
