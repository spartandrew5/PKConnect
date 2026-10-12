using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PKConnect.Core.graphics;

/// <summary>
/// Game Boy / Game Boy Color 2bpp planar tile codec.
///
/// Format (verified byte-exact against retail Pokémon Red (U) and Yellow (U)
/// for the player walk / bike / fishing sprites):
///   - The image is cut into 8x8 tiles in row-major order across its full width
///     (tile 0 = top-left, then left-to-right, then next tile row down).
///   - Each tile is 16 bytes: 8 rows x 2 bytes. For row r, byte[r*2] is the low
///     bitplane and byte[r*2+1] the high bitplane; bit 7 is the leftmost pixel.
///     colour index = (high &lt;&lt; 1) | low, in 0..3.
///   - Palette: the distinct pixel luminances are ranked brightest-first, so the
///     lightest colour maps to index 0 and the darkest to index 3. Fully
///     transparent pixels count as "brightest" (index 0), matching how the
///     vanilla overworld sprites treat their background.
/// </summary>
public static class GbTileCodec
{
    public const int TileSize = 8;
    public const int BytesPerTile = 16;

    /// <summary>
    /// Encodes <paramref name="image"/> to 2bpp tile bytes. Width and height
    /// must both be multiples of 8.
    /// </summary>
    /// <param name="columnMajor">
    /// When true, tiles are serialized column-by-column (tile 0 = top-left, tile
    /// 1 = the one below it, ... down the first column, then the next column).
    /// Gen 2 front / trainer-card / back images use this order; Gen 1 sprites and
    /// all overworld sprites are row-major (the default).
    /// </param>
    public static byte[] Encode(Image<Rgba32> image, bool columnMajor = false)
    {
        if (image.Width % TileSize != 0 || image.Height % TileSize != 0)
        {
            throw new ArgumentException(
                $"Image {image.Width}x{image.Height} is not a whole number of 8x8 tiles.");
        }

        var tilesWide = image.Width / TileSize;
        var tilesTall = image.Height / TileSize;
        var indices = QuantizeToIndices(image);
        var numTiles = tilesWide * tilesTall;

        var outBytes = new byte[numTiles * BytesPerTile];
        for (var t = 0; t < numTiles; t++)
        {
            var tx = columnMajor ? t / tilesTall : t % tilesWide;
            var ty = columnMajor ? t % tilesTall : t / tilesWide;
            for (var row = 0; row < TileSize; row++)
            {
                byte lo = 0, hi = 0;
                for (var col = 0; col < TileSize; col++)
                {
                    var v = indices[(ty * TileSize + row) * image.Width + tx * TileSize + col];
                    var bit = 7 - col;
                    lo |= (byte)((v & 1) << bit);
                    hi |= (byte)(((v >> 1) & 1) << bit);
                }
                outBytes[t * BytesPerTile + row * 2] = lo;
                outBytes[t * BytesPerTile + row * 2 + 1] = hi;
            }
        }
        return outBytes;
    }

    /// <summary>
    /// Decodes 2bpp tile bytes back into a greyscale image of the given tile
    /// dimensions. Index 0..3 → white / light grey / dark grey / black. Mainly
    /// for tests and diagnostics.
    /// </summary>
    public static Image<Rgba32> Decode(ReadOnlySpan<byte> data, int tilesWide, int tilesTall)
    {
        var w = tilesWide * TileSize;
        var h = tilesTall * TileSize;
        var img = new Image<Rgba32>(w, h);
        Span<byte> shades = [255, 170, 85, 0];

        var t = 0;
        for (var ty = 0; ty < tilesTall; ty++)
        {
            for (var tx = 0; tx < tilesWide; tx++, t++)
            {
                for (var row = 0; row < TileSize; row++)
                {
                    var lo = data[t * BytesPerTile + row * 2];
                    var hi = data[t * BytesPerTile + row * 2 + 1];
                    for (var col = 0; col < TileSize; col++)
                    {
                        var bit = 7 - col;
                        var v = ((hi >> bit) & 1) << 1 | ((lo >> bit) & 1);
                        var s = shades[v];
                        img[tx * TileSize + col, ty * TileSize + row] = new Rgba32(s, s, s, 255);
                    }
                }
            }
        }
        return img;
    }

    /// <summary>
    /// Maps every pixel to a 0..3 colour index by luminance rank (brightest → 0).
    /// Transparent pixels are treated as pure white. If the image somehow has
    /// more than four distinct shades, extras collapse onto the nearest of four
    /// evenly-spaced buckets.
    /// </summary>
    private static int[] QuantizeToIndices(Image<Rgba32> image)
    {
        var w = image.Width;
        var h = image.Height;
        var luma = new int[w * h];
        var distinct = new SortedSet<int>();

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < h; y++)
            {
                var rowSpan = accessor.GetRowSpan(y);
                for (var x = 0; x < w; x++)
                {
                    var p = rowSpan[x];
                    int l = p.A == 0
                        ? 255
                        : (int)Math.Round(0.299 * p.R + 0.587 * p.G + 0.114 * p.B);
                    luma[y * w + x] = l;
                    distinct.Add(l);
                }
            }
        });

        var indices = new int[w * h];

        if (distinct.Count <= 4)
        {
            // brightest distinct luma → 0, next → 1, ...
            var rank = new Dictionary<int, int>();
            var r = 0;
            foreach (var l in distinct.Reverse())
            {
                rank[l] = r++;
            }
            for (var i = 0; i < luma.Length; i++)
            {
                indices[i] = rank[luma[i]];
            }
        }
        else
        {
            for (var i = 0; i < luma.Length; i++)
            {
                indices[i] = Math.Clamp((int)Math.Round((255 - luma[i]) / 255.0 * 3), 0, 3);
            }
        }

        return indices;
    }
}
