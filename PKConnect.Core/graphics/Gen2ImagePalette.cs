using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PKConnect.Core.graphics;

/// <summary>
/// Derives the 2-colour Gen 2 trainer-class palette from a pack's front image,
/// mirroring UPR-FVX's <c>GBCImage.Builder.preparePalette</c> +
/// <c>Gen2PlayerCharacterGraphics.initImagePalette</c>: the image's distinct
/// colours are ranked bright→dark, pure white is pinned to slot 0 and pure black
/// to slot 3, and the two middle slots become the trainer palette.
///
/// GSC trainer palettes store only those two colours, 2 bytes each as GBC
/// BGR555 little-endian (white/black are implicit in the hardware palette).
/// </summary>
public static class Gen2ImagePalette
{
    /// <summary>Returns the 4 BGR555-LE bytes to write, or null if the image is unusable.</summary>
    public static byte[]? DeriveTrainerPaletteBytes(Image<Rgba32> frontImage)
    {
        var distinct = new HashSet<int>();
        frontImage.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    if (p.A != 0)
                    {
                        distinct.Add((p.R << 16) | (p.G << 8) | p.B);
                    }
                }
            }
        });

        if (distinct.Count == 0 || distinct.Count > 4)
        {
            return null;
        }

        var sorted = distinct
            .OrderByDescending(Luminance)
            .ToList();

        // Slots: [white, light-grey, dark-grey, black]; sorted colours drop into
        // their brightness-rank slot unless they are pure white/black.
        var slots = new[] { 0xFFFFFF, 0xAAAAAA, 0x666666, 0x000000 };
        for (var i = 0; i < sorted.Count && i < 4; i++)
        {
            var c = sorted[i];
            if (c != 0xFFFFFF && c != 0x000000)
            {
                slots[i] = c;
            }
        }

        return
        [
            .. ToBgr555(slots[1]),
            .. ToBgr555(slots[2]),
        ];
    }

    private static double Luminance(int rgb)
    {
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        return 0.299 * r + 0.587 * g + 0.114 * b;
    }

    private static byte[] ToBgr555(int rgb)
    {
        var r = ((rgb >> 16) & 0xFF) >> 3;
        var g = ((rgb >> 8) & 0xFF) >> 3;
        var b = (rgb & 0xFF) >> 3;
        var v = r | (g << 5) | (b << 10);
        return [(byte)(v & 0xFF), (byte)((v >> 8) & 0xFF)];
    }
}
