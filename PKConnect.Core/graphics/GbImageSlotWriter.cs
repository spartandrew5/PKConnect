using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PKConnect.Core.graphics;

/// <summary>
/// Shared helper for GB/GBC "uncompressed, fixed-size, written in place" sprite
/// slots (walk / bike / fish). Encodes a PNG to 2bpp with <see cref="GbTileCodec"/>
/// and emits an edit only when the bytes actually differ, refusing to write over
/// a region that looks empty (wrong ROM revision).
/// </summary>
public static class GbImageSlotWriter
{
    public static void TryWriteFile(
        byte[] rom,
        string packDir,
        string? fileName,
        Gen1SpriteSlot slot,
        List<(int Offset, byte[] Data)> edits,
        List<PlayerGraphicsSlotResult> results,
        bool columnMajor = false)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            results.Add(new(slot.Name, false, "not provided by pack"));
            return;
        }

        var path = Path.Combine(packDir, fileName);
        if (!File.Exists(path))
        {
            results.Add(new(slot.Name, false, $"file not found: {fileName}"));
            return;
        }

        try
        {
            using var image = Image.Load<Rgba32>(path);
            TryWriteImage(rom, image, slot, edits, results, columnMajor);
        }
        catch (Exception ex)
        {
            results.Add(new(slot.Name, false, $"load/encode failed: {ex.Message}"));
        }
    }

    public static void TryWriteImage(
        byte[] rom,
        Image<Rgba32> image,
        Gen1SpriteSlot slot,
        List<(int Offset, byte[] Data)> edits,
        List<PlayerGraphicsSlotResult> results,
        bool columnMajor = false)
    {
        if (slot.Offset < 0 || slot.Offset + slot.ByteLength > rom.Length)
        {
            results.Add(new(slot.Name, false, "slot offset out of range for this ROM"));
            return;
        }

        var expectedW = slot.TilesWide * GbTileCodec.TileSize;
        var expectedH = slot.TilesTall * GbTileCodec.TileSize;
        if (image.Width != expectedW || image.Height != expectedH)
        {
            results.Add(new(slot.Name, false,
                $"expected {expectedW}x{expectedH}, got {image.Width}x{image.Height}"));
            return;
        }

        byte[] encoded;
        try
        {
            encoded = GbTileCodec.Encode(image, columnMajor);
        }
        catch (Exception ex)
        {
            results.Add(new(slot.Name, false, $"encode failed: {ex.Message}"));
            return;
        }

        if (encoded.Length != slot.ByteLength)
        {
            results.Add(new(slot.Name, false,
                $"encoded {encoded.Length} bytes, slot is {slot.ByteLength}"));
            return;
        }

        var current = rom.AsSpan(slot.Offset, slot.ByteLength);
        if (IsUniform(current, 0x00) || IsUniform(current, 0xFF))
        {
            results.Add(new(slot.Name, false,
                "ROM bytes at this offset look empty — wrong revision/romhack?"));
            return;
        }

        if (current.SequenceEqual(encoded))
        {
            results.Add(new(slot.Name, true, "already matches (no-op)"));
            return;
        }

        edits.Add((slot.Offset, encoded));
        results.Add(new(slot.Name, true, null));
    }

    /// <summary>Stitches images left-to-right into one row (heights must match).</summary>
    public static Image<Rgba32> StitchHorizontal(IReadOnlyList<Image<Rgba32>> parts)
    {
        var width = parts.Sum(p => p.Width);
        var height = parts.Max(p => p.Height);
        var canvas = new Image<Rgba32>(width, height);
        var x = 0;
        foreach (var part in parts)
        {
            var offsetX = x;
            canvas.Mutate(ctx => ctx.DrawImage(part, new Point(offsetX, 0), 1f));
            x += part.Width;
        }
        return canvas;
    }

    private static bool IsUniform(ReadOnlySpan<byte> span, byte value)
    {
        foreach (var b in span)
        {
            if (b != value)
            {
                return false;
            }
        }
        return true;
    }
}
