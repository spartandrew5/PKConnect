using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PKConnect.Core.nds;

/// <summary>
/// 16-colour palette builder shared by every Nitro sprite codec (BTX0's
/// Color16 format and NCGR/NCLR's 4bpp format use the exact same indexed
/// pixel + BGR555 palette scheme).
/// </summary>
public static class PaletteQuantizer
{
    /// <summary>
    /// Slot 0 is always <paramref name="background"/> (the game's transparent-
    /// background convention). Slots 1.. are chosen by weighted farthest-point
    /// sampling over the image's other distinct colours: each pick maximises
    /// (min RGB distance to already-picked colours) x frequency. Plain
    /// "N most-frequent colours" looks fine on the source PNG but silently
    /// erases small, high-contrast accent regions (a red cap on an otherwise
    /// white/tan/green outfit) once they lose the popularity contest to the
    /// large fill colours - farthest-point sampling keeps them because
    /// they're maximally different from what's already covered, while the
    /// frequency weighting still keeps single-pixel anti-aliasing noise from
    /// winning a slot over a real, if smaller, coloured region.
    /// </summary>
    public static List<Rgba32> BuildPalette(Image<Rgba32> image, Rgba32 background, int colorCount)
    {
        var counts = new Dictionary<Rgba32, int>();
        for (var yy = 0; yy < image.Height; yy++)
        {
            for (var xx = 0; xx < image.Width; xx++)
            {
                var pixel = image[xx, yy];
                if (pixel == background) continue;
                counts[pixel] = counts.GetValueOrDefault(pixel) + 1;
            }
        }

        var palette = new List<Rgba32> { background };
        var candidates = counts.ToList();
        while (palette.Count < colorCount && candidates.Count > 0)
        {
            var bestIndex = 0;
            var bestScore = -1L;
            for (var i = 0; i < candidates.Count; i++)
            {
                var (color, frequency) = candidates[i];
                var minDist = long.MaxValue;
                foreach (var picked in palette)
                {
                    var d = Dist2(picked, color);
                    if (d < minDist) minDist = d;
                }
                var score = minDist * frequency;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }
            palette.Add(candidates[bestIndex].Key);
            candidates.RemoveAt(bestIndex);
        }
        return palette;
    }

    /// <summary>
    /// Nearest palette colour by squared RGB distance, slot 0 (the background)
    /// excluded so a dark-but-distinct pixel never collapses onto it.
    /// </summary>
    public static int NearestIndex(List<Rgba32> palette, Rgba32 color)
    {
        var best = 1;
        var bestDist = long.MaxValue;
        for (var i = 1; i < palette.Count; i++)
        {
            var dist = Dist2(palette[i], color);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }
        return best;
    }

    private static long Dist2(Rgba32 a, Rgba32 b)
    {
        var dr = a.R - b.R;
        var dg = a.G - b.G;
        var db = a.B - b.B;
        return (long)dr * dr + (long)dg * dg + (long)db * db;
    }
}
