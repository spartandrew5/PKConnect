using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PKConnect.Core.nds;

/// <summary>
/// The player figure on Gen 5's trainer card (BW a/0/4/0, B2W2 a/0/3/9). Same
/// idea as Gen 4's (see <see cref="Gen4TrainerCardCodec"/>): one 8bpp tile pool
/// (88x80 = 110 tiles) holds both genders' figures, and a 32x32 BG tilemap per
/// gender picks its tiles - the hero's with entry 0 for "empty", the heroine's
/// padded with a blank filler tile. Vanilla uses 108 of the 110 tiles (hero 49,
/// heroine 59), in a 256-colour palette shared with the card's own UI.
///
/// Since the Gen 5 writer shows the chosen sprite for either gender, both
/// tilemaps are pointed at ONE figure: that frees enough tiles for a whole
/// 80x80 front sprite, so nothing is downscaled into the vanilla 40px window.
/// </summary>
public static class Gen5TrainerCardCodec
{
    private const int TileSize = 8;
    private const int BytesPerTile = 64; // 8bpp
    private const int BackgroundTolerance = 6;

    public readonly record struct InjectResult(int TilesUsed, int ColoursUsed, int ColoursWanted, IReadOnlyList<int> WrittenPalettes);

    private sealed class Map
    {
        public required byte[] Bytes;
        public required int DataOffset;
        public required int TilesWide;
        public required int Entries;
        public ushort this[int entry]
        {
            get => BitConverter.ToUInt16(Bytes, DataOffset + entry * 2);
            set => BitConverter.TryWriteBytes(Bytes.AsSpan(DataOffset + entry * 2), value);
        }

        /// <summary>The raw entry the map is padded with (0 for the hero's, the blank filler tile for the heroine's).</summary>
        public ushort Filler => Enumerable.Range(0, Entries).GroupBy(e => this[e]).OrderByDescending(g => g.Count()).First().Key;
    }

    private static Map ReadMap(byte[] nscr)
    {
        if (System.Text.Encoding.ASCII.GetString(nscr, 0, 4) != "RCSN") throw new InvalidDataException("Not an NSCR file.");
        var scrn = BitConverter.ToUInt16(nscr, 0x0C);
        var width = BitConverter.ToUInt16(nscr, scrn + 8);
        var dataSize = (int)BitConverter.ToUInt32(nscr, scrn + 16);
        return new Map { Bytes = nscr, DataOffset = scrn + 0x14, TilesWide = width / TileSize, Entries = dataSize / 2 };
    }

    private static bool IsBlankTile(byte[] sheet, int dataOffset, int tile)
    {
        var start = dataOffset + tile * BytesPerTile;
        if (start + BytesPerTile > sheet.Length) return true;
        for (var b = 0; b < BytesPerTile; b++) if (sheet[start + b] != 0) return false;
        return true;
    }

    /// <summary>Map entries that draw part of the figure: every cell whose tile has any pixels.</summary>
    private static List<int> FigureEntries(Map map, byte[] sheet, int dataOffset) =>
        Enumerable.Range(0, map.Entries).Where(e => !IsBlankTile(sheet, dataOffset, map[e] & 0x3FF)).ToList();

    /// <summary>The figure a tilemap assembles, cropped to its tiles' bounding box.</summary>
    public static Image<Rgba32> DecodeFigure(byte[] sheet, byte[] palette, byte[] nscr)
    {
        var dataOffset = NcgrSpriteCodec.ReadNcgrHeader(sheet).DataOffset;
        var map = ReadMap(nscr);
        var cells = FigureEntries(map, sheet, dataOffset);
        if (cells.Count == 0) throw new InvalidDataException("The tilemap draws no figure.");
        var colours = ReadColours(palette);

        int c0 = cells.Min(e => e % map.TilesWide), c1 = cells.Max(e => e % map.TilesWide);
        int r0 = cells.Min(e => e / map.TilesWide), r1 = cells.Max(e => e / map.TilesWide);
        var image = new Image<Rgba32>((c1 - c0 + 1) * TileSize, (r1 - r0 + 1) * TileSize, colours[0]);
        foreach (var e in cells)
        {
            var entry = map[e];
            var start = dataOffset + (entry & 0x3FF) * BytesPerTile;
            bool flipH = (entry & 0x400) != 0, flipV = (entry & 0x800) != 0;
            for (var py = 0; py < TileSize; py++)
                for (var px = 0; px < TileSize; px++)
                {
                    var index = sheet[start + (flipV ? 7 - py : py) * TileSize + (flipH ? 7 - px : px)];
                    image[(e % map.TilesWide - c0) * TileSize + px, (e / map.TilesWide - r0) * TileSize + py] = colours[index];
                }
        }
        return image;
    }

    /// <summary>
    /// Replaces both genders' figures with <paramref name="source"/>'s content (its backdrop -
    /// the top-left pixel's colour, or transparency - cropped away), standing where the
    /// hero's did: same horizontal centre, feet on the same row. Mutates the sheet, both
    /// tilemaps and <paramref name="palettes"/> in place.
    /// <paramref name="palettes"/>: the 256-colour members the card may use; the first is the
    /// reference, and only those whose figure colours currently match it are recoloured (the
    /// others are different screens' themes). Only indices the vanilla figures used are ever
    /// written - the bank is shared with the card's own UI.
    /// </summary>
    public static InjectResult Inject(
        byte[] sheet, IReadOnlyList<byte[]> palettes, byte[] heroNscr, byte[] heroineNscr, Image<Rgba32> source)
    {
        var header = NcgrSpriteCodec.ReadNcgrHeader(sheet);
        if (header.Depth != 4) throw new NotSupportedException("Trainer-card sheet is not 8bpp.");
        if (palettes.Count == 0) throw new ArgumentException("At least one palette member is required.");
        var dataOffset = header.DataOffset;

        var maps = new[] { ReadMap(heroNscr), ReadMap(heroineNscr) };
        var fillers = maps.Select(m => m.Filler).ToArray();
        var figures = maps.Select(m => FigureEntries(m, sheet, dataOffset)).ToArray();
        if (figures[0].Count == 0) throw new InvalidDataException("The hero tilemap draws no figure.");

        // Tiles and palette indices the two vanilla figures own - the only ones this may reuse.
        var tiles = figures.SelectMany((cells, i) => cells.Select(e => maps[i][e] & 0x3FF)).Distinct().OrderBy(t => t).ToList();
        var budget = new SortedSet<int>();
        foreach (var tile in tiles)
            for (var b = 0; b < BytesPerTile; b++)
            {
                var index = sheet[dataOffset + tile * BytesPerTile + b];
                if (index != 0) budget.Add(index);
            }

        // Where the hero stood.
        var wide = maps[0].TilesWide;
        int heroLeft = figures[0].Min(e => e % wide), heroRight = figures[0].Max(e => e % wide);
        var bottomRow = figures[0].Max(e => e / wide);
        var centreX = (heroLeft + heroRight + 1) * TileSize / 2;

        using var content = CropToContent(source);
        var tilesW = (content.Width + TileSize - 1) / TileSize;
        var tilesH = (content.Height + TileSize - 1) / TileSize;
        if (tilesW > wide || tilesH > bottomRow + 1)
            throw new ArgumentException($"A {content.Width}x{content.Height} figure doesn't fit on the card.");
        var leftCol = Math.Clamp((int)Math.Round((centreX - tilesW * TileSize / 2.0) / TileSize), 0, wide - tilesW);
        var topRow = bottomRow - tilesH + 1;
        // Bottom-aligned, centred within its tile area.
        var offsetX = (tilesW * TileSize - content.Width) / 2;
        var offsetY = tilesH * TileSize - content.Height;

        var (resolve, assigned, wanted) = BuildColours(content, budget.ToList());

        // Tile pixels, one fresh tile per non-empty grid cell.
        var free = new Queue<int>(tiles);
        var placed = new Dictionary<(int Col, int Row), int>();
        var tile64 = new byte[BytesPerTile];
        for (var ty = 0; ty < tilesH; ty++)
        {
            for (var tx = 0; tx < tilesW; tx++)
            {
                var any = false;
                for (var i = 0; i < BytesPerTile; i++)
                {
                    int x = tx * TileSize + i % TileSize - offsetX, y = ty * TileSize + i / TileSize - offsetY;
                    var index = x < 0 || y < 0 || x >= content.Width || y >= content.Height ? 0 : resolve(content[x, y]);
                    tile64[i] = (byte)index;
                    any |= index != 0;
                }
                if (!any) continue;
                if (free.Count == 0) throw new InvalidOperationException("The figure needs more tiles than the card sheet has.");
                var tile = free.Dequeue();
                tile64.CopyTo(sheet, dataOffset + tile * BytesPerTile);
                placed[(leftCol + tx, topRow + ty)] = tile;
            }
        }
        var used = tiles.Count - free.Count;
        foreach (var tile in free) Array.Clear(sheet, dataOffset + tile * BytesPerTile, BytesPerTile);

        // Both tilemaps: drop the old figure, draw the new one.
        for (var m = 0; m < maps.Length; m++)
        {
            foreach (var e in figures[m]) maps[m][e] = fillers[m];
            foreach (var ((col, row), tile) in placed) maps[m][row * maps[m].TilesWide + col] = (ushort)tile;
        }

        // Recolour every palette that currently shows the vanilla figure the way the reference does.
        var reference = palettes[0];
        var (refOffset, _) = PaletteData(reference);
        var referenceColours = budget.Select(i => BitConverter.ToUInt16(reference, refOffset + i * 2)).ToArray();
        var written = new List<int>();
        for (var p = 0; p < palettes.Count; p++)
        {
            var (offset, count) = PaletteData(palettes[p]);
            if (count < 256) continue;
            var current = budget.Select(i => BitConverter.ToUInt16(palettes[p], offset + i * 2));
            if (!current.SequenceEqual(referenceColours)) continue;
            written.Add(p);
        }
        foreach (var p in written)
        {
            var (offset, _) = PaletteData(palettes[p]);
            foreach (var (colour, index) in assigned)
                BitConverter.TryWriteBytes(palettes[p].AsSpan(offset + index * 2), (ushort)NitroColor.ToBgr555(colour));
        }
        return new InjectResult(used, assigned.Count, wanted, written);
    }

    /// <summary>
    /// Where a palette member's colours start and how many it really holds. Counted from
    /// the data length, not the header's bit-depth flag: some of B2W2's card palettes are
    /// flagged 16-colour while holding all 256 (NcgrSpriteCodec.ReadNclrHeader would say 16).
    /// </summary>
    private static (int Offset, int Count) PaletteData(byte[] nclr)
    {
        var offset = NcgrSpriteCodec.ReadNclrHeader(nclr).ColorsOffset;
        return (offset, Math.Min(256, (nclr.Length - offset) / 2));
    }

    /// <summary>
    /// Picks the figure's colours for the available palette indices (weighted farthest-point,
    /// like <see cref="PaletteQuantizer"/>): returns pixel -> index (0 = transparent), the
    /// (colour, index) assignments, and how many distinct colours the art wanted.
    /// </summary>
    private static (Func<Rgba32, int> Resolve, List<(Rgba32 Colour, int Index)> Assigned, int Wanted) BuildColours(
        Image<Rgba32> content, List<int> indices)
    {
        var counts = new Dictionary<Rgba32, int>();
        for (var y = 0; y < content.Height; y++)
            for (var x = 0; x < content.Width; x++)
            {
                var p = content[x, y];
                if (p.A != 0) counts[p] = counts.GetValueOrDefault(p) + 1;
            }

        var assigned = new List<(Rgba32 Colour, int Index)>();
        var candidates = counts.ToList();
        while (assigned.Count < indices.Count && candidates.Count > 0)
        {
            var best = 0;
            var bestScore = -1L;
            for (var i = 0; i < candidates.Count; i++)
            {
                var minDist = assigned.Count == 0 ? 1L : assigned.Min(a => Dist2(a.Colour, candidates[i].Key));
                var score = minDist * candidates[i].Value;
                if (score > bestScore) { bestScore = score; best = i; }
            }
            assigned.Add((candidates[best].Key, indices[assigned.Count]));
            candidates.RemoveAt(best);
        }

        var exact = new Dictionary<Rgba32, int>();
        foreach (var (colour, index) in assigned) exact.TryAdd(colour, index);
        int Resolve(Rgba32 pixel)
        {
            if (pixel.A == 0 || assigned.Count == 0) return 0;
            if (exact.TryGetValue(pixel, out var index)) return index;
            return assigned.MinBy(a => Dist2(a.Colour, pixel)).Index;
        }
        return (Resolve, assigned, counts.Count);
    }

    /// <summary>The source's non-backdrop pixels, backdrop made transparent.</summary>
    private static Image<Rgba32> CropToContent(Image<Rgba32> source)
    {
        var corner = source[0, 0];
        bool IsBackdrop(Rgba32 p) =>
            p.A < 128 || (corner.A >= 128
                && Math.Abs(p.R - corner.R) <= BackgroundTolerance
                && Math.Abs(p.G - corner.G) <= BackgroundTolerance
                && Math.Abs(p.B - corner.B) <= BackgroundTolerance);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
            {
                if (IsBackdrop(source[x, y])) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        if (maxX < 0) throw new ArgumentException("The image is all backdrop.");

        var content = new Image<Rgba32>(maxX - minX + 1, maxY - minY + 1);
        for (var y = 0; y < content.Height; y++)
            for (var x = 0; x < content.Width; x++)
            {
                var p = source[minX + x, minY + y];
                content[x, y] = IsBackdrop(p) ? new Rgba32(0, 0, 0, 0) : p with { A = 255 };
            }
        return content;
    }

    private static Rgba32[] ReadColours(byte[] nclr)
    {
        var (offset, count) = PaletteData(nclr);
        var colours = new Rgba32[256];
        for (var i = 0; i < 256; i++)
            colours[i] = i < count ? NitroColor.FromBgr555(BitConverter.ToUInt16(nclr, offset + i * 2)) : default;
        return colours;
    }

    private static long Dist2(Rgba32 a, Rgba32 b)
    {
        long dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return dr * dr + dg * dg + db * db;
    }
}
