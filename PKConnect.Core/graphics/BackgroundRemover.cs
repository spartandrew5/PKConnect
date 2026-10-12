using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PKConnect.Core.graphics;

/// <summary>
/// Turns a solid, opaque edge background transparent by flood-filling inward
/// from the borders (same idea as the frontend's TrainerSprite preview). Only
/// acts when both top corners are opaque and share a colour - so a pack PNG
/// that already has a real transparent background is left untouched.
/// </summary>
public static class BackgroundRemover
{
    private const int Tolerance = 42; // sum of |dr|+|dg|+|db|
    private const int PocketTolerance = 12; // enclosed pockets: stricter match

    public static void MakeEdgeBackgroundTransparent(Image<Rgba32> image)
    {
        var w = image.Width;
        var h = image.Height;
        if (w < 2 || h < 2)
        {
            return;
        }

        var px = new Rgba32[w * h];
        image.CopyPixelDataTo(px);

        // The two TOP corners decide the backdrop. The bottom ones can't be trusted: back sprites are cut
        // off at the bottom edge, so the body fills those corners on at least one frame of the strip.
        // Border pixels that don't match (that body) are simply skipped by the flood fill below.
        var c0 = px[0];
        var c1 = px[w - 1];
        if (c0.A <= 200 || c1.A <= 200 || Dist(c1, c0) > Tolerance)
        {
            return;
        }

        var seen = new bool[w * h];
        var stack = new Stack<int>();
        for (var x = 0; x < w; x++)
        {
            stack.Push(x);
            stack.Push((h - 1) * w + x);
        }
        for (var y = 0; y < h; y++)
        {
            stack.Push(y * w);
            stack.Push(y * w + (w - 1));
        }

        while (stack.Count > 0)
        {
            var p = stack.Pop();
            if (seen[p])
            {
                continue;
            }
            seen[p] = true;
            if (px[p].A == 0 || Dist(px[p], c0) > Tolerance)
            {
                continue;
            }
            px[p] = new Rgba32(0, 0, 0, 0);

            var x = p % w;
            var y = p / w;
            if (x + 1 < w) stack.Push(p + 1);
            if (x - 1 >= 0) stack.Push(p - 1);
            if (y + 1 < h) stack.Push(p + w);
            if (y - 1 >= 0) stack.Push(p - w);
        }

        // Pockets of background the sprite fully encloses (e.g. between an arm
        // and the body) aren't reachable from the edges. Clear those too, with a
        // tighter tolerance so near-bg sprite colours survive. Only for saturated
        // key colours: a white/black/grey bg is also a real body colour in GB sprites.
        var isKeyColour = Math.Max(c0.R, Math.Max(c0.G, c0.B)) - Math.Min(c0.R, Math.Min(c0.G, c0.B)) >= 64;
        for (var p = 0; isKeyColour && p < w * h; p++)
        {
            if (!seen[p] && px[p].A > 0 && Dist(px[p], c0) <= PocketTolerance)
            {
                px[p] = new Rgba32(0, 0, 0, 0);
            }
        }

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < w; x++)
                {
                    row[x] = px[y * w + x];
                }
            }
        });
    }

    private static int Dist(Rgba32 a, Rgba32 b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
}
