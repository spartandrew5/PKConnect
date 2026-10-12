using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PKConnect.Core.nds;

/// <summary>
/// Codec for the Gen 4 (HGSS) trainer-card protagonist. The sprite sheet
/// (a/0/4/9 member 44) is an 8bpp NCGR, but the character is NOT a simple
/// rectangular slice of it: an NSCR BG tilemap (member 54 = hero, 55 =
/// heroine) assembles a 40x72 figure from a SCATTERED, non-contiguous set of
/// the sheet's tiles (upper body, lower body and feet come from three
/// different sheet regions). Confirmed against real HG bytes. Editing the
/// sheet as a flat raster therefore looks fine for the unchanged vanilla art
/// (a byte-exact round trip) but shears/segments any NEW art, because the
/// tilemap re-orders the tiles on screen. This codec follows the tilemap for
/// both extract and inject, so the tiles a caller writes land exactly where
/// the game reads them.
///
/// The palette (member 0, a 256-colour bank duplicated across members 0..24)
/// is shared: hero and heroine reference DISJOINT tiles but a few
/// black/outline/skin colours overlap. Injecting one gender only overwrites
/// its EXCLUSIVE palette indices and reuses shared ones, leaving the other
/// gender byte-identical.
/// </summary>
public static class Gen4TrainerCardCodec
{
    private const int TileSize = 8;
    private const int BytesPerTile8Bpp = 64;

    public readonly record struct Cell(int LocalX, int LocalY, int TileIndex, bool FlipH, bool FlipV);

    /// <summary>
    /// <see cref="RawTilesWide"/>/<see cref="MinCol"/>/<see cref="MinRow"/>/
    /// <see cref="DataOffset"/> let a caller map a local (col,row) tile
    /// position back to its raw entry offset in the source NSCR bytes - see
    /// <see cref="Inject"/>'s free-tile allocation, which patches the tilemap
    /// itself rather than only the tile pixel data.
    /// </summary>
    public readonly record struct CharMap(int Width, int Height, IReadOnlyList<Cell> Cells, int RawTilesWide, int MinCol, int MinRow, int DataOffset);

    private static int TileByteOffset(int dataOffset, int tileIndex) => dataOffset + tileIndex * BytesPerTile8Bpp;

    /// <summary>
    /// Reads the character's on-screen layout from its NSCR tilemap: every
    /// non-empty BG cell, re-based so the character's bounding box starts at
    /// (0,0). <see cref="CharMap.Width"/>/<see cref="CharMap.Height"/> are the
    /// figure's pixel size (40x72 for HGSS).
    /// </summary>
    public static CharMap ReadMap(byte[] nscr)
    {
        if (System.Text.Encoding.ASCII.GetString(nscr, 0, 4) != "RCSN") throw new InvalidDataException("Not an NSCR file.");
        var scrn = BitConverter.ToUInt16(nscr, 0x0C);
        var w = BitConverter.ToUInt16(nscr, scrn + 8);
        var dataOffset = scrn + 0x14;
        var dataSize = (int)BitConverter.ToUInt32(nscr, scrn + 16);
        var tilesWide = w / TileSize;

        var raw = new List<(int Col, int Row, int Tile, bool H, bool V)>();
        int minCol = int.MaxValue, minRow = int.MaxValue, maxCol = -1, maxRow = -1;
        var entries = dataSize / 2;
        for (var e = 0; e < entries; e++)
        {
            var pos = dataOffset + e * 2;
            if (pos + 2 > nscr.Length) break;
            var v = BitConverter.ToUInt16(nscr, pos);
            var tile = v & 0x3FF;
            if (tile == 0) continue; // 0 = empty
            var col = e % tilesWide;
            var row = e / tilesWide;
            raw.Add((col, row, tile, ((v >> 10) & 1) == 1, ((v >> 11) & 1) == 1));
            if (col < minCol) minCol = col;
            if (col > maxCol) maxCol = col;
            if (row < minRow) minRow = row;
            if (row > maxRow) maxRow = row;
        }
        if (raw.Count == 0) throw new InvalidDataException("NSCR references no tiles.");

        var cells = raw
            .Select(c => new Cell((c.Col - minCol) * TileSize, (c.Row - minRow) * TileSize, c.Tile, c.H, c.V))
            .ToList();
        return new CharMap((maxCol - minCol + 1) * TileSize, (maxRow - minRow + 1) * TileSize, cells, tilesWide, minCol, minRow, dataOffset);
    }

    /// <summary>
    /// Patches a single BG entry of <paramref name="nscr"/> (the same raw
    /// bytes <paramref name="map"/> was read from) so the tile grid position
    /// (<paramref name="localCol"/>, <paramref name="localRow"/>) points at
    /// <paramref name="tileIndex"/>, no flip. Used by <see cref="Inject"/> to
    /// add a cell where none existed before (see the free-tile notes there).
    /// </summary>
    private static void SetRawCell(byte[] nscr, CharMap map, int localCol, int localRow, int tileIndex)
    {
        var entryIndex = (map.MinRow + localRow) * map.RawTilesWide + (map.MinCol + localCol);
        var entryOffset = map.DataOffset + entryIndex * 2;
        nscr[entryOffset] = (byte)(tileIndex & 0xFF);
        nscr[entryOffset + 1] = (byte)((tileIndex >> 8) & 0x03);
    }

    private static Rgba32[] ReadPalette(byte[] nclr)
    {
        var lh = NcgrSpriteCodec.ReadNclrHeader(nclr);
        var palette = new Rgba32[lh.ColorCount];
        for (var i = 0; i < lh.ColorCount; i++)
        {
            palette[i] = NitroColor.FromBgr555(BitConverter.ToUInt16(nclr, lh.ColorsOffset + i * 2));
        }
        return palette;
    }

    /// <summary>Decodes the character assembled by <paramref name="nscr"/> from the sheet - the true on-card pose.</summary>
    public static Image<Rgba32> DecodeCharacter(byte[] ncgr, byte[] nclr, byte[] nscr)
    {
        var ch = NcgrSpriteCodec.ReadNcgrHeader(ncgr);
        if (ch.Depth != 4) throw new NotSupportedException($"Trainer-card NCGR depth {ch.Depth} is not 8bpp/256-colour.");
        var palette = ReadPalette(nclr);
        var map = ReadMap(nscr);

        var image = new Image<Rgba32>(map.Width, map.Height);
        foreach (var cell in map.Cells)
        {
            var tileStart = TileByteOffset(ch.DataOffset, cell.TileIndex);
            if (tileStart + BytesPerTile8Bpp > ncgr.Length) continue;
            for (var py = 0; py < TileSize; py++)
            {
                for (var px = 0; px < TileSize; px++)
                {
                    var sx = cell.FlipH ? 7 - px : px;
                    var sy = cell.FlipV ? 7 - py : py;
                    image[cell.LocalX + px, cell.LocalY + py] = palette[ncgr[tileStart + sy * TileSize + sx]];
                }
            }
        }
        return image;
    }

    /// <summary>
    /// Tile slots in the sheet that are all-zero bytes AND unreferenced by
    /// either the write or protect map - genuine spare capacity confirmed by
    /// scanning real HG/DP/Pt ROMs (each sheet has 25-38 such tiles; none
    /// contain leftover nonzero art). Index 0 is always excluded: the NSCR
    /// format here treats a raw entry value of 0 as "no tile" (see
    /// <see cref="ReadMap"/>), so pointing a new cell at index 0 would
    /// immediately be read back as empty again.
    /// </summary>
    private static Queue<int> FindFreeTiles(byte[] ncgr, int dataOffset, CharMap writeMap, CharMap protectMap)
    {
        var totalTiles = (ncgr.Length - dataOffset) / BytesPerTile8Bpp;
        var used = new HashSet<int>();
        foreach (var c in writeMap.Cells) used.Add(c.TileIndex);
        foreach (var c in protectMap.Cells) used.Add(c.TileIndex);

        var free = new Queue<int>();
        for (var t = 1; t < totalTiles; t++)
        {
            if (used.Contains(t)) continue;
            var tileStart = dataOffset + t * BytesPerTile8Bpp;
            if (tileStart + BytesPerTile8Bpp > ncgr.Length) break;
            var allZero = true;
            for (var b = 0; b < BytesPerTile8Bpp; b++) if (ncgr[tileStart + b] != 0) { allZero = false; break; }
            if (allZero) free.Enqueue(t);
        }
        return free;
    }

    private static List<int> UsedIndices(byte[] ncgr, int dataOffset, CharMap map)
    {
        var used = new SortedSet<int>();
        foreach (var cell in map.Cells)
        {
            var tileStart = TileByteOffset(dataOffset, cell.TileIndex);
            if (tileStart + BytesPerTile8Bpp > ncgr.Length) continue;
            for (var b = 0; b < BytesPerTile8Bpp; b++)
            {
                var idx = ncgr[tileStart + b];
                if (idx != 0) used.Add(idx);
            }
        }
        return used.ToList();
    }

    /// <summary>
    /// A "flat background" pack image is rarely byte-exact across its whole
    /// matte - PNG re-saves/edits leave 1-2-unit-per-channel noise on
    /// stray pixels (confirmed on a real pack: a front.png whose corner was
    /// (192,184,152) had scattered (192,184,153)-ish pixels reaching every
    /// edge). Exact equality treated those as real content, blowing the crop
    /// bounding box out to the FULL source image and shrinking the fitted
    /// pose far more than intended - looking identical to the old "shrunk
    /// into a corner" bug even though the cause here is unrelated. A small
    /// per-channel tolerance absorbs that noise without risking a match
    /// against genuinely different (character) colours.
    /// </summary>
    private const int BackgroundTolerance = 6;

    private static bool IsBackground(Rgba32 p, Rgba32 bg) =>
        Math.Abs(p.R - bg.R) <= BackgroundTolerance &&
        Math.Abs(p.G - bg.G) <= BackgroundTolerance &&
        Math.Abs(p.B - bg.B) <= BackgroundTolerance &&
        Math.Abs(p.A - bg.A) <= BackgroundTolerance;

    /// <summary>
    /// Fits <paramref name="source"/> into <paramref name="w"/>x<paramref name="h"/>: exact size passes
    /// through untouched (so a hand-authored figure is respected byte-for-byte),
    /// otherwise the source's non-transparent content is centred horizontally
    /// and bottom-aligned (feet at the base), scaled DOWN only if it doesn't
    /// fit - never up, so a front sprite keeps its native proportions instead
    /// of stretching to fill.
    ///
    /// This places content across the whole canvas without regard for which
    /// grid positions the write map actually has tiles for - see
    /// <see cref="Inject"/>'s free-tile allocation, which is what actually
    /// keeps a pose shaped differently from vanilla's (e.g. a battle-stance
    /// FrontImage reaching into a corner vanilla's own pose never used) from
    /// losing pixels there.
    ///
    /// <paramref name="verticalOffset"/>/<paramref name="horizontalOffset"/>
    /// nudge the fitted pose up/left from its default centred-bottom
    /// position, when a specific game's card window sits off from a literal
    /// bottom-of-canvas alignment (confirmed by eye in-game; currently only
    /// Platinum needs this - see its <see cref="Gen4TrainerCardSlot"/>).
    /// Safe to tune freely: <see cref="Inject"/>'s free-tile allocation
    /// backfills whatever grid cells the shifted pose lands on, so this can
    /// never reintroduce clipping.
    /// </summary>
    public static Image<Rgba32> FitToCanvas(Image<Rgba32> source, int w, int h, int verticalOffset = 0, int horizontalOffset = 0)
    {
        if (source.Width == w && source.Height == h) return source.Clone();

        var corner = source[0, 0];
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
            {
                var p = source[x, y];
                if (p.A == 0 || IsBackground(p, corner)) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        if (maxX < 0) { minX = 0; minY = 0; maxX = source.Width - 1; maxY = source.Height - 1; }

        var cw = maxX - minX + 1;
        var chh = maxY - minY + 1;
        using var content = source.Clone(c => c.Crop(new Rectangle(minX, minY, cw, chh)));
        content.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    if (IsBackground(row[x], corner)) row[x] = new Rgba32(0, 0, 0, 0);
            }
        });

        var scale = Math.Min(1f, Math.Min((float)w / cw, (float)h / chh)); // downscale-only: keep native proportions
        var sw = Math.Max(1, (int)MathF.Round(cw * scale));
        var sh = Math.Max(1, (int)MathF.Round(chh * scale));
        using var scaled = scale < 1f
            ? content.Clone(c => c.Resize(sw, sh, KnownResamplers.NearestNeighbor))
            : content.Clone();

        var canvas = new Image<Rgba32>(w, h);
        var destX = Math.Max(0, (w - sw) / 2 - horizontalOffset);
        var destY = Math.Max(0, h - sh - 1 - verticalOffset); // nudged up/left to match where the card's own visible window sits
        canvas.Mutate(c => c.DrawImage(scaled, new Point(destX, destY), 1f));
        return canvas;
    }

    /// <summary><paramref name="WrittenPalettes"/>: 256-colour members that got the new exclusive colours. <paramref name="NscrChanged"/>: true if any new cell was added to <paramref name="nscrWrite"/> (see <see cref="Inject"/>'s free-tile notes) - the caller must add an edit for that NARC member too, since it isn't otherwise touched.</summary>
    public readonly record struct InjectResult(IReadOnlyList<int> WrittenPalettes, bool NscrChanged);

    /// <summary>
    /// Writes <paramref name="source"/> into the sheet at the exact tiles the
    /// write-gender's tilemap reads, and rewrites only that gender's exclusive
    /// palette indices. <paramref name="palettes"/> (256-colour members to keep
    /// in sync) are mutated in place.
    ///
    /// The write map's own tile-grid bounding box isn't fully covered by
    /// cells - vanilla's pose only occupies a staggered/diagonal subset of
    /// the rectangle's 8x8 tiles, entire corners having no cell at all. A
    /// pose shaped differently from vanilla's can therefore need pixels at a
    /// grid position the map never allocated a tile for. Real HG/DP/Pt ROMs
    /// each have 25-38 genuinely spare (all-zero, unreferenced) tile slots in
    /// this same sheet (confirmed by scanning); after the normal per-cell
    /// write below, this method also walks every EMPTY grid position within
    /// the canvas, and for any that the fitted pose actually needs (non-
    /// background pixels land there), claims one free tile, writes the pixel
    /// data into it, and patches <paramref name="nscrWrite"/>'s own raw entry
    /// to point at it (see <see cref="SetRawCell"/>) - growing the tilemap's
    /// effective coverage instead of dropping those pixels. If the sheet's
    /// free tiles run out, any remaining gaps are left exactly as before
    /// (silently dropped), matching the old behaviour.
    ///
    /// <paramref name="verticalOffset"/>/<paramref name="horizontalOffset"/>
    /// pass straight through to <see cref="FitToCanvas"/> - see its remarks.
    ///
    /// Colour budget: capped at whatever the vanilla figure's own tiles
    /// happened to use (<paramref name="palettes"/>' 256-colour bank is a
    /// full per-badge-tier card THEME shared with the border/star UI, not
    /// dedicated to the character - claiming an "unused" index for extra
    /// colours was tried and reverted after it corrupted the border in-game).
    /// A pack needing more colours than that budget has its least-essential
    /// one merged into its nearest kept neighbour instead.
    /// </summary>
    public static InjectResult Inject(
        byte[] ncgr, IReadOnlyList<byte[]> palettes, Image<Rgba32> source,
        byte[] nscrWrite, byte[] nscrProtect, int verticalOffset = 0, int horizontalOffset = 0)
    {
        var ch = NcgrSpriteCodec.ReadNcgrHeader(ncgr);
        if (ch.Depth != 4) throw new NotSupportedException($"Trainer-card NCGR depth {ch.Depth} is not 8bpp/256-colour.");
        if (palettes.Count == 0) throw new ArgumentException("At least one palette member is required.");

        var writeMap = ReadMap(nscrWrite);
        var protectMap = ReadMap(nscrProtect);

        using var pose = FitToCanvas(source, writeMap.Width, writeMap.Height, verticalOffset, horizontalOffset);

        var writeIndices = UsedIndices(ncgr, ch.DataOffset, writeMap);
        if (writeIndices.Count == 0) throw new InvalidDataException("The tiles to overwrite reference no colours.");
        var protectSet = new HashSet<int>(UsedIndices(ncgr, ch.DataOffset, protectMap));

        var exclusive = writeIndices.Where(i => !protectSet.Contains(i)).ToList();
        var shared = writeIndices.Where(i => protectSet.Contains(i)).ToList();

        var reference = palettes[0];
        var refHeader = NcgrSpriteCodec.ReadNclrHeader(reference);
        Rgba32 ColorAt(byte[] pal, NcgrSpriteCodec.NclrHeader h, int idx) =>
            NitroColor.FromBgr555(BitConverter.ToUInt16(pal, h.ColorsOffset + idx * 2));
        var background = ColorAt(reference, refHeader, 0);

        var counts = new Dictionary<Rgba32, int>();
        for (var y = 0; y < pose.Height; y++)
            for (var x = 0; x < pose.Width; x++)
            {
                var c = pose[x, y];
                if (c.A == 0 || c == background) continue;
                counts[c] = counts.GetValueOrDefault(c) + 1;
            }

        // Tried claiming spare, character-tile-unreferenced indices here for
        // packs needing more colours than the vanilla figure did (226+ of
        // the 256 looked untouched by either gender's card tiles) - REVERTED:
        // confirmed in-game that this 256-colour bank isn't exclusive to the
        // character figure. The 25 duplicated palette members are a full
        // card-THEME variant per badge tier (border/star decorations
        // included), not just a skin-tone recolour - so an index unused by
        // the character tiles can still be load-bearing for the border/UI at
        // some badge count, and recolouring it corrupted the card's top
        // border in-game. There's no way to verify "truly free" from static
        // analysis alone here, so any colour beyond the vanilla figure's own
        // budget is approximated (merged to its nearest kept neighbour)
        // instead, same as before this was tried.

        var picked = new List<Rgba32> { background };
        var resolveSet = new List<(Rgba32 Color, int Index)>();
        foreach (var i in shared)
        {
            var c = ColorAt(reference, refHeader, i);
            picked.Add(c);
            resolveSet.Add((c, i));
        }

        var newAssignments = new List<(Rgba32 Color, int Index)>();
        var candidates = counts.ToList();
        while (newAssignments.Count < exclusive.Count && candidates.Count > 0)
        {
            var best = 0;
            var bestScore = -1L;
            for (var i = 0; i < candidates.Count; i++)
            {
                var (color, freq) = candidates[i];
                var minDist = long.MaxValue;
                foreach (var p in picked)
                {
                    var d = Dist2(p, color);
                    if (d < minDist) minDist = d;
                }
                var score = minDist * freq;
                if (score > bestScore) { bestScore = score; best = i; }
            }
            var chosen = candidates[best].Key;
            var index = exclusive[newAssignments.Count];
            newAssignments.Add((chosen, index));
            resolveSet.Add((chosen, index));
            picked.Add(chosen);
            candidates.RemoveAt(best);
        }

        int Resolve(Rgba32 color)
        {
            if (color.A == 0 || color == background || resolveSet.Count == 0) return 0;
            var bestIdx = resolveSet[0].Index;
            var bestDist = long.MaxValue;
            foreach (var (c, idx) in resolveSet)
            {
                var d = Dist2(c, color);
                if (d < bestDist) { bestDist = d; bestIdx = idx; }
            }
            return bestIdx;
        }

        foreach (var cell in writeMap.Cells)
        {
            var tileStart = TileByteOffset(ch.DataOffset, cell.TileIndex);
            if (tileStart + BytesPerTile8Bpp > ncgr.Length) continue;
            for (var py = 0; py < TileSize; py++)
            {
                for (var px = 0; px < TileSize; px++)
                {
                    var sx = cell.FlipH ? 7 - px : px;
                    var sy = cell.FlipV ? 7 - py : py;
                    ncgr[tileStart + sy * TileSize + sx] = (byte)Resolve(pose[cell.LocalX + px, cell.LocalY + py]);
                }
            }
        }

        // Fill grid positions the map has no cell for at all, where the pose
        // actually needs one (see this method's remarks).
        var tilesWide = writeMap.Width / TileSize;
        var tilesTall = writeMap.Height / TileSize;
        var hasCell = new bool[tilesTall, tilesWide];
        foreach (var cell in writeMap.Cells) hasCell[cell.LocalY / TileSize, cell.LocalX / TileSize] = true;

        var freeTiles = FindFreeTiles(ncgr, ch.DataOffset, writeMap, protectMap);
        var nscrChanged = false;
        for (var row = 0; row < tilesTall; row++)
        {
            for (var col = 0; col < tilesWide; col++)
            {
                if (hasCell[row, col]) continue;

                var localX = col * TileSize;
                var localY = row * TileSize;
                var needsTile = false;
                for (var py = 0; py < TileSize && !needsTile; py++)
                    for (var px = 0; px < TileSize; px++)
                    {
                        var p = pose[localX + px, localY + py];
                        if (p.A != 0 && p != background) { needsTile = true; break; }
                    }
                if (!needsTile || freeTiles.Count == 0) continue;

                var newTile = freeTiles.Dequeue();
                var tileStart = TileByteOffset(ch.DataOffset, newTile);
                for (var py = 0; py < TileSize; py++)
                    for (var px = 0; px < TileSize; px++)
                        ncgr[tileStart + py * TileSize + px] = (byte)Resolve(pose[localX + px, localY + py]);

                SetRawCell(nscrWrite, writeMap, col, row, newTile);
                nscrChanged = true;
            }
        }

        var referenceColors = writeIndices.Select(i => BitConverter.ToUInt16(reference, refHeader.ColorsOffset + i * 2)).ToArray();
        var written = new List<int>();
        for (var p = 0; p < palettes.Count; p++)
        {
            var pal = palettes[p];
            NcgrSpriteCodec.NclrHeader header;
            try { header = NcgrSpriteCodec.ReadNclrHeader(pal); }
            catch { continue; }
            if (header.ColorCount < 256) continue;

            var matches = true;
            for (var i = 0; i < writeIndices.Count; i++)
            {
                if (BitConverter.ToUInt16(pal, header.ColorsOffset + writeIndices[i] * 2) != referenceColors[i]) { matches = false; break; }
            }
            if (!matches) continue;

            foreach (var (color, index) in newAssignments)
            {
                var bgr555 = NitroColor.ToBgr555(color);
                var at = header.ColorsOffset + index * 2;
                pal[at] = (byte)bgr555;
                pal[at + 1] = (byte)(bgr555 >> 8);
            }
            written.Add(p);
        }
        return new InjectResult(written, nscrChanged);
    }

    private static long Dist2(Rgba32 a, Rgba32 b)
    {
        long dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return dr * dr + dg * dg + db * db;
    }
}
