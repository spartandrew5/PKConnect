using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PKConnect.Core.graphics;

/// <summary>
/// Game Boy Advance 4bpp tile codec.
///
/// Format (verified byte-exact against retail FireRed (U) 1.0 by decoding the
/// player's overworld walk sprite):
///   - 8x8 tiles, 32 bytes each: for each of 8 rows, 4 bytes hold 8 pixels, two
///     per byte, low nibble = left pixel, high nibble = right pixel.
///   - Tiles are laid out row-major across the image width (tile 0 top-left,
///     then left-to-right, then the next tile row down).
///   - Each pixel is a 0-15 index into a <see cref="GbaPalette"/> (index 0 = transparent).
/// </summary>
public static class GbaTileCodec
{
    public const int TileSize = 8;
    public const int BytesPerTile = 32;

    /// <summary>
    /// Encodes <paramref name="image"/> to 4bpp tile bytes, mapping every pixel
    /// to the nearest colour in <paramref name="palette"/> (fully transparent
    /// pixels → index 0). Width and height must be multiples of 8.
    /// </summary>
    public static byte[] Encode(Image<Rgba32> image, GbaPalette palette)
    {
        if (image.Width % TileSize != 0 || image.Height % TileSize != 0)
        {
            throw new ArgumentException(
                $"Image {image.Width}x{image.Height} is not a whole number of 8x8 tiles.");
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
                        : (byte)palette.NearestIndex((p.R << 16) | (p.G << 8) | p.B);
                }
            }
        });

        var tilesWide = w / TileSize;
        var tilesTall = h / TileSize;
        var numTiles = tilesWide * tilesTall;
        var outBytes = new byte[numTiles * BytesPerTile];

        for (var t = 0; t < numTiles; t++)
        {
            var tx = (t % tilesWide) * TileSize;
            var ty = (t / tilesWide) * TileSize;
            for (var row = 0; row < TileSize; row++)
            {
                for (var col = 0; col < TileSize; col += 2)
                {
                    var left = indices[(ty + row) * w + tx + col];
                    var right = indices[(ty + row) * w + tx + col + 1];
                    outBytes[t * BytesPerTile + row * 4 + col / 2] = (byte)((right << 4) | left);
                }
            }
        }
        return outBytes;
    }

    /// <summary>Decodes 4bpp tile bytes into an image (for tests / diagnostics).</summary>
    public static Image<Rgba32> Decode(ReadOnlySpan<byte> data, int tilesWide, int tilesTall, GbaPalette palette)
    {
        var w = tilesWide * TileSize;
        var h = tilesTall * TileSize;
        var img = new Image<Rgba32>(w, h);
        var numTiles = tilesWide * tilesTall;

        for (var t = 0; t < numTiles; t++)
        {
            var tx = (t % tilesWide) * TileSize;
            var ty = (t / tilesWide) * TileSize;
            for (var row = 0; row < TileSize; row++)
            {
                for (var col = 0; col < TileSize; col += 2)
                {
                    var b = data[t * BytesPerTile + row * 4 + col / 2];
                    foreach (var (dx, idx) in new[] { (0, b & 0xF), (1, b >> 4) })
                    {
                        var rgb = palette[idx];
                        img[tx + col + dx, ty + row] = idx == 0
                            ? new Rgba32(0, 0, 0, 0)
                            : new Rgba32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
                    }
                }
            }
        }
        return img;
    }
}
