using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PKConnect.Core.graphics;

/// <summary>
/// A GBA 16-colour palette. Colours are 15-bit BGR555 packed little-endian
/// (2 bytes each, 32 bytes total). Index 0 is the transparent colour.
/// </summary>
public sealed class GbaPalette
{
    public const int Size = 16;
    public const int ByteLength = Size * 2;

    /// <summary>Packed RGB888 (0xRRGGBB) for each of the 16 slots.</summary>
    private readonly int[] _colors;

    /// <summary>
    /// Number of meaningful slots (0.._count-1). Slots past this are unused
    /// zero-padding and must never win a <see cref="NearestIndex"/> match.
    /// </summary>
    private readonly int _count;

    private GbaPalette(int[] colors, int count = Size)
    {
        _colors = colors;
        _count = Math.Clamp(count, 1, Size);
    }

    public int this[int index] => _colors[index];

    /// <summary>Reads 16 BGR555-LE colours from ROM at <paramref name="offset"/>.</summary>
    public static GbaPalette FromRom(byte[] rom, int offset)
    {
        var colors = new int[Size];
        for (var i = 0; i < Size; i++)
        {
            var v = rom[offset + i * 2] | (rom[offset + i * 2 + 1] << 8);
            var r = (v & 0x1F) * 255 / 31;
            var g = ((v >> 5) & 0x1F) * 255 / 31;
            var b = ((v >> 10) & 0x1F) * 255 / 31;
            colors[i] = (r << 16) | (g << 8) | b;
        }
        return new GbaPalette(colors);
    }

    /// <summary>
    /// The nearest colour the GBA can actually display (15-bit BGR555). Snapping
    /// to this before counting merges the +-1..7 channel noise a "reduce to 16
    /// colours" export leaves behind.
    /// </summary>
    public static int SnapToGba(int rgb)
    {
        var r = (rgb >> 16) & 0xF8;
        var g = (rgb >> 8) & 0xF8;
        var b = rgb & 0xF8;
        return (r << 16) | (g << 8) | b;
    }

    /// <summary>
    /// Builds a 16-colour palette from an image: slot 0 keeps
    /// <paramref name="slot0Rgb"/> (transparent on the GBA regardless), slots
    /// 1.. are the image's distinct opaque colours (snapped to GBA precision)
    /// ordered brightest-first. Returns null if there are more than 15.
    /// </summary>
    public static GbaPalette? FromImage(Image<Rgba32> image, int slot0Rgb)
    {
        var distinct = new HashSet<int>();
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    if (p.A != 0)
                    {
                        distinct.Add(SnapToGba((p.R << 16) | (p.G << 8) | p.B));
                    }
                }
            }
        });

        if (distinct.Count > Size - 1)
        {
            return null;
        }

        var colors = new int[Size];
        colors[0] = slot0Rgb;
        var i = 1;
        foreach (var c in distinct.OrderByDescending(Luminance))
        {
            colors[i++] = c;
        }
        return new GbaPalette(colors, i);
    }

    /// <summary>
    /// The palette from an indexed (colour-type-3) PNG's PLTE chunk, in file
    /// order, padded to 16. This is the authoritative GBA sprite palette when a
    /// pack ships proper 16-colour indexed art. Returns null if the PNG isn't
    /// indexed or has more than 16 entries.
    /// </summary>
    public static GbaPalette? FromIndexedPng(byte[] png)
    {
        if (png.Length < 8 || png[0] != 0x89 || png[1] != 'P')
        {
            return null;
        }
        var i = 8;
        var colorType = -1;
        byte[]? plte = null;
        while (i + 8 <= png.Length)
        {
            var len = (png[i] << 24) | (png[i + 1] << 16) | (png[i + 2] << 8) | png[i + 3];
            var tag = System.Text.Encoding.ASCII.GetString(png, i + 4, 4);
            var dataStart = i + 8;
            if (tag == "IHDR" && len >= 10)
            {
                colorType = png[dataStart + 9];
            }
            else if (tag == "PLTE")
            {
                plte = png[dataStart..(dataStart + len)];
            }
            else if (tag == "IEND")
            {
                break;
            }
            i = dataStart + len + 4;
        }

        if (colorType != 3 || plte is null || plte.Length % 3 != 0 || plte.Length / 3 > Size)
        {
            return null;
        }

        var count = plte.Length / 3;
        var colors = new int[Size];
        for (var c = 0; c < count; c++)
        {
            colors[c] = (plte[c * 3] << 16) | (plte[c * 3 + 1] << 8) | plte[c * 3 + 2];
        }
        return new GbaPalette(colors, count);
    }

    /// <summary>
    /// Reduces an image with too many colours to 15 opaque colours + a
    /// transparent slot 0, so non-indexed pack art still works (GBA sprites are
    /// 4bpp / 16 colours). Picks the 15 most-used colours - for pixel art with
    /// anti-aliased edges this keeps the real sprite colours and every stray
    /// blended edge pixel snaps to its nearest via <see cref="NearestIndex"/>.
    /// </summary>
    public static GbaPalette FromImageQuantized(Image<Rgba32> image, int slot0Rgb)
    {
        var counts = new Dictionary<int, int>();
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    if (p.A == 0)
                    {
                        continue;
                    }
                    var rgb = SnapToGba((p.R << 16) | (p.G << 8) | p.B);
                    counts[rgb] = counts.GetValueOrDefault(rgb) + 1;
                }
            }
        });

        var slots = new int[Size];
        slots[0] = slot0Rgb;
        var i = 1;
        foreach (var (rgb, _) in counts.OrderByDescending(kv => kv.Value).Take(Size - 1))
        {
            slots[i++] = rgb;
        }
        return new GbaPalette(slots, i);
    }

    /// <summary>Parses a JASC-PAL (16 colours, "R G B" 0-255 lines).</summary>
    public static GbaPalette? FromJascPal(IReadOnlyList<string> lines)
    {
        if (lines.Count < 3 || !lines[0].Trim().Equals("JASC-PAL", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var colors = new int[Size];
        var read = 0;
        for (var li = 3; li < lines.Count && read < Size; li++)
        {
            var parts = lines[li].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3
                || !int.TryParse(parts[0], out var r)
                || !int.TryParse(parts[1], out var g)
                || !int.TryParse(parts[2], out var b))
            {
                continue;
            }
            colors[read++] = (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255);
        }
        return read == Size ? new GbaPalette(colors) : null;
    }

    /// <summary>
    /// A watery reflection palette derived from this one: every colour lerped
    /// toward a pale blue-white, approximating FRLG's hand-tuned bridge-
    /// reflection tint (lighter, blue-shifted). Used when a pack ships no
    /// explicit <c>SpriteReflectionPalette</c> .pal - far better than reusing
    /// the body palette verbatim, which reads as a hard mirror.
    /// </summary>
    public GbaPalette DeriveReflection()
    {
        const int target = 0xC8E1F0; // pale cyan-white
        const double t = 0.42;
        var outc = new int[Size];
        for (var i = 0; i < Size; i++)
        {
            var c = _colors[i];
            var r = (int)(((c >> 16) & 0xFF) + (((target >> 16) & 0xFF) - ((c >> 16) & 0xFF)) * t);
            var g = (int)(((c >> 8) & 0xFF) + (((target >> 8) & 0xFF) - ((c >> 8) & 0xFF)) * t);
            var b = (int)((c & 0xFF) + ((target & 0xFF) - (c & 0xFF)) * t);
            outc[i] = (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255);
        }
        return new GbaPalette(outc, _count);
    }

    private static double Luminance(int rgb) =>
        0.299 * ((rgb >> 16) & 0xFF) + 0.587 * ((rgb >> 8) & 0xFF) + 0.114 * (rgb & 0xFF);

    public byte[] ToBytes()
    {
        var bytes = new byte[ByteLength];
        for (var i = 0; i < Size; i++)
        {
            var c = _colors[i];
            var r = ((c >> 16) & 0xFF) >> 3;
            var g = ((c >> 8) & 0xFF) >> 3;
            var b = (c & 0xFF) >> 3;
            var v = r | (g << 5) | (b << 10);
            bytes[i * 2] = (byte)(v & 0xFF);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return bytes;
    }

    /// <summary>
    /// Nearest opaque palette index (by squared RGB distance) for a colour.
    /// Slot 0 is transparent-only on the GBA and unused zero-padding past
    /// <see cref="_count"/> must not win, so the search is 1.._count-1. This is
    /// what keeps a pure-black outline pixel from collapsing onto the (often
    /// also black) transparent slot 0.
    /// </summary>
    public int NearestIndex(int rgb888)
    {
        var r = (rgb888 >> 16) & 0xFF;
        var g = (rgb888 >> 8) & 0xFF;
        var b = rgb888 & 0xFF;

        var best = 1;
        var bestDist = int.MaxValue;
        for (var i = 1; i < _count; i++)
        {
            var c = _colors[i];
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
