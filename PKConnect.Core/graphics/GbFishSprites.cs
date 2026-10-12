using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PKConnect.Core.graphics;

/// <summary>
/// Game Boy fishing tiles. In both Yellow and Crystal the fishing pose is the standing frame with its bottom
/// row (two 8x8 tiles) swapped, once per facing (down, up, side). A pack ships them either as three separate
/// 8x16 files - the row's left tile stacked over its right tile - or as one combined 16x24 sheet: the three
/// 16x8 rows stacked (down, up, side), the layout of the games' own fishing sheets.
/// </summary>
public static class GbFishSprites
{
    /// <summary>
    /// Splits a combined 16x24 fishing sheet into the three separate-style 8x16 parts (each row's left tile
    /// over its right tile). Returns null unless the sheet is exactly 16x24.
    /// </summary>
    public static List<Image<Rgba32>>? SplitCombined(Image<Rgba32> sheet)
    {
        const int t = GbTileCodec.TileSize;
        if (sheet.Width != 2 * t || sheet.Height != 3 * t)
        {
            return null;
        }
        var parts = new List<Image<Rgba32>>();
        for (var row = 0; row < 3; row++)
        {
            var part = new Image<Rgba32>(t, 2 * t);
            for (var half = 0; half < 2; half++)
            {
                using var tile = sheet.Clone(ctx => ctx.Crop(new Rectangle(half * t, row * t, t, t)));
                var dest = new Point(0, half * t);
                part.Mutate(ctx => ctx.DrawImage(tile, dest, 1f));
            }
            parts.Add(part);
        }
        return parts;
    }
}
