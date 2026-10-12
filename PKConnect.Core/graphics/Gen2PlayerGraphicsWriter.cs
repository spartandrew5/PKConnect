using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PKConnect.Core.graphics;

/// <summary>
/// Writes the Gen 2 (Crystal) player sprites:
///  - walk / bike: 16x96 2bpp tile blocks (same layout as Gen 1), in place;
///  - fish: the separate front/back/side 8x16 poses written as three consecutive
///    tile pairs (see <see cref="ArrangeFishTiles"/>);
///  - front (7x7) + trainer-card (5x7) images: uncompressed, column-major, in place;
///  - back battle image (6x6): LZ3-compressed, relocated to a free bank + repointed;
///  - sprite palette: a single byte (RED/GREEN/... → (ordinal|8)&lt;&lt;4), plus
///    the 2-colour trainer-class palette for the battle sprite.
/// </summary>
public static class Gen2PlayerGraphicsWriter
{
    public static (List<(int Offset, byte[] Data)> Edits, List<PlayerGraphicsSlotResult> Results)
        BuildEdits(string gameId, byte[] rom, GraphicsPackStanza stanza, string packDir,
            IReadOnlyList<(int Start, int End)>? reservedRanges = null)
    {
        var edits = new List<(int Offset, byte[] Data)>();
        var results = new List<PlayerGraphicsSlotResult>();

        var rev = Gen2PlayerGraphicsOffsets.ForGame(gameId);
        if (rev is null)
        {
            results.Add(new("*", false, $"No Gen 2 offsets for game '{gameId}'"));
            return (edits, results);
        }

        GbImageSlotWriter.TryWriteFile(rom, packDir, stanza.Get("WalkSprite"), rev.Walk, edits, results);
        GbImageSlotWriter.TryWriteFile(rom, packDir, stanza.Get("BikeSprite"), rev.Bike, edits, results);

        TryFishSprite(rom, packDir, stanza, rev, edits, results);
        TryFrontAndCard(rom, packDir, stanza, rev, edits, results);
        TryBackImage(rom, packDir, stanza.Get("BackImage"), rev, reservedRanges, edits, results);
        TrySpritePalette(rom, stanza.Get("SpritePalette"), rev, edits, results);

        return (edits, results);
    }

    /// <summary>
    /// Crystal's back battle sprite (6x6) is LZ3-compressed and referenced by a
    /// pointer + bank byte. Encode column-major, compress, write into a free
    /// bank and repoint both pointer sites + both bank bytes. Mirrors UPR-FVX's
    /// rewriteChrisBackImage for Crystal (no catching-tutorial Dude entanglement,
    /// unlike Gold/Silver).
    /// </summary>
    private static void TryBackImage(
        byte[] rom,
        string packDir,
        string? fileName,
        Gen2PlayerGraphicsOffsets.Revision rev,
        IReadOnlyList<(int Start, int End)>? reservedRanges,
        List<(int Offset, byte[] Data)> edits,
        List<PlayerGraphicsSlotResult> results)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            results.Add(new("BackImage", false, "not provided by pack"));
            return;
        }

        var path = Path.Combine(packDir, fileName);
        if (!File.Exists(path))
        {
            results.Add(new("BackImage", false, $"file not found: {fileName}"));
            return;
        }

        var side = rev.BackTiles * GbTileCodec.TileSize;

        byte[] compressed;
        try
        {
            using var image = Image.Load<Rgba32>(path);
            if (image.Width != side || image.Height != side)
            {
                results.Add(new("BackImage", false,
                    $"expected {side}x{side}, got {image.Width}x{image.Height}"));
                return;
            }
            compressed = Gen2SpriteCompressor.Compress(GbTileCodec.Encode(image, columnMajor: true));
        }
        catch (Exception ex)
        {
            results.Add(new("BackImage", false, $"encode/compress failed: {ex.Message}"));
            return;
        }

        var pointerSites = new[] { rev.BackPrimaryPointerSite }.Concat(rev.BackSecondaryPointerSites).ToArray();
        if (!GbPointerUtil.AllReadSameWord(rom, pointerSites, out var oldWord))
        {
            results.Add(new("BackImage", false, "back-image pointer sites disagree - wrong revision/romhack?"));
            return;
        }
        if (!GbPointerUtil.AllReadSameByte(rom, rev.BackBankSites, out _))
        {
            results.Add(new("BackImage", false, "back-image bank bytes disagree - wrong revision/romhack?"));
            return;
        }

        var allocator = new GbBankAllocator(rom, rev.UnusedBanks, reservedRanges);
        var newOffset = allocator.Allocate(compressed.Length, preferBank: rev.UnusedBanks.FirstOrDefault());
        if (newOffset is null)
        {
            results.Add(new("BackImage", false, "no free ROM bank space for the compressed back sprite"));
            return;
        }

        var newBank = (byte)(newOffset.Value / GbBankAllocator.BankSize);

        edits.Add((newOffset.Value, compressed));
        foreach (var site in pointerSites)
        {
            edits.Add((site, GbPointerUtil.PtrBytes(newOffset.Value)));
        }
        foreach (var site in rev.BackBankSites)
        {
            edits.Add((site, [newBank]));
        }

        results.Add(new("BackImage", true,
            $"repointed 0x{oldWord:X4} -> bank 0x{newBank:X2} ({compressed.Length} bytes LZ3)"));
    }

    /// <summary>
    /// Crystal stores the front battle sprite (7x7) and the trainer-card image
    /// (5x7) uncompressed and column-major, written in place. The trainer-card
    /// image is a tile-column-1..5 crop of the front unless the pack ships its
    /// own <c>TrainerCardImage=</c> PNG (matches UPR-FVX's
    /// Gen2PlayerCharacterGraphics.getTrainerCardImage).
    /// </summary>
    private static void TryFrontAndCard(
        byte[] rom,
        string packDir,
        GraphicsPackStanza stanza,
        Gen2PlayerGraphicsOffsets.Revision rev,
        List<(int Offset, byte[] Data)> edits,
        List<PlayerGraphicsSlotResult> results)
    {
        var frontName = stanza.Get("FrontImage");
        if (string.IsNullOrWhiteSpace(frontName))
        {
            results.Add(new("FrontImage", false, "not provided by pack"));
            results.Add(new("TrainerCardImage", false, "no front image to derive from"));
            return;
        }

        var frontPath = Path.Combine(packDir, frontName);
        if (!File.Exists(frontPath))
        {
            results.Add(new("FrontImage", false, $"file not found: {frontName}"));
            return;
        }

        Image<Rgba32> front;
        try
        {
            front = Image.Load<Rgba32>(frontPath);
        }
        catch (Exception ex)
        {
            results.Add(new("FrontImage", false, $"load failed: {ex.Message}"));
            return;
        }

        using (front)
        {
            GbImageSlotWriter.TryWriteImage(rom, front, rev.Front, edits, results, columnMajor: true);
            TryImagePalette(rom, front, rev, edits, results);

            var cardName = stanza.Get("TrainerCardImage");
            if (!string.IsNullOrWhiteSpace(cardName))
            {
                GbImageSlotWriter.TryWriteFile(rom, packDir, cardName, rev.TrainerCard, edits, results,
                    columnMajor: true);
                return;
            }

            // Derive the card from the front: tile columns 1..5, all rows.
            var px = GbTileCodec.TileSize;
            var cropX = px;
            var cropW = rev.TrainerCard.TilesWide * px;
            var cropH = rev.TrainerCard.TilesTall * px;
            if (front.Width < cropX + cropW || front.Height < cropH)
            {
                results.Add(new("TrainerCardImage", false,
                    $"front image {front.Width}x{front.Height} too small to derive a {cropW}x{cropH} card"));
                return;
            }

            using var card = front.Clone(ctx => ctx.Crop(new Rectangle(cropX, 0, cropW, cropH)));
            GbImageSlotWriter.TryWriteImage(rom, card, rev.TrainerCard, edits, results, columnMajor: true);
        }
    }

    /// <summary>
    /// Lays the three separate 8x16 fishing sprites (front, back, side) out as a 24x16 image whose row-major
    /// tile order is [front top, front bottom, back top, back bottom, side top, side bottom]. Crystal's
    /// LoadFishingGFX reads that data as consecutive tile *pairs* - the bottom row of the down / up / left
    /// standing frames - and each 8x16 file holds its pair stacked (the same convention as the Gen 1 writer's
    /// 1x2 fish slots). Stitching the files side by side instead interleaved the pairs (front got front-top +
    /// back-top, etc.). Returns null unless all three are 8x16.
    /// </summary>
    public static Image<Rgba32>? ArrangeFishTiles(IReadOnlyList<Image<Rgba32>> parts)
    {
        if (parts.Count != 3 || parts.Any(p => p.Width != GbTileCodec.TileSize || p.Height != GbTileCodec.TileSize * 2))
        {
            return null;
        }
        const int t = GbTileCodec.TileSize;
        var canvas = new Image<Rgba32>(3 * t, 2 * t);
        for (var k = 0; k < 6; k++)
        {
            var part = parts[k / 2];
            var srcY = (k % 2) * t;
            using var tile = part.Clone(ctx => ctx.Crop(new Rectangle(0, srcY, t, t)));
            var dest = new Point((k % 3) * t, (k / 3) * t);
            canvas.Mutate(ctx => ctx.DrawImage(tile, dest, 1f));
        }
        return canvas;
    }

    /// <summary>A combined 16x24 fish.png: split into the three 8x16 pairs, then laid out like separate files.</summary>
    private static void TryCombinedFish(
        byte[] rom, string packDir, string? fileName, Gen2PlayerGraphicsOffsets.Revision rev,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            results.Add(new("Fish", false, "not provided by pack"));
            return;
        }
        var path = Path.Combine(packDir, fileName);
        if (!File.Exists(path))
        {
            results.Add(new("Fish", false, $"file not found: {fileName}"));
            return;
        }
        try
        {
            using var sheet = Image.Load<Rgba32>(path);
            var parts = GbFishSprites.SplitCombined(sheet);
            if (parts is null)
            {
                results.Add(new("Fish", false, $"expected a 16x24 combined fishing sheet, got {sheet.Width}x{sheet.Height}"));
                return;
            }
            try
            {
                using var arranged = ArrangeFishTiles(parts)!;
                GbImageSlotWriter.TryWriteImage(rom, arranged, rev.Fish, edits, results);
            }
            finally
            {
                parts.ForEach(p => p.Dispose());
            }
        }
        catch (Exception ex)
        {
            results.Add(new("Fish", false, $"load/split failed: {ex.Message}"));
        }
    }

    private static void TryFishSprite(
        byte[] rom,
        string packDir,
        GraphicsPackStanza stanza,
        Gen2PlayerGraphicsOffsets.Revision rev,
        List<(int Offset, byte[] Data)> edits,
        List<PlayerGraphicsSlotResult> results)
    {
        var mode = (stanza.Get("FishSpriteMode") ?? "separate").ToLowerInvariant();
        if (mode == "combined")
        {
            TryCombinedFish(rom, packDir, stanza.Get("FishSprite"), rev, edits, results);
            return;
        }
        if (mode != "separate")
        {
            results.Add(new("Fish", false, $"FishSpriteMode='{mode}' not supported"));
            return;
        }

        var names = new[] { stanza.Get("FishFrontSprite"), stanza.Get("FishBackSprite"), stanza.Get("FishSideSprite") };
        if (names.Any(string.IsNullOrWhiteSpace))
        {
            results.Add(new("Fish", false, "pack is missing one of the separate fishing sprites"));
            return;
        }

        var loaded = new List<Image<Rgba32>>();
        try
        {
            foreach (var name in names)
            {
                var path = Path.Combine(packDir, name!);
                if (!File.Exists(path))
                {
                    results.Add(new("Fish", false, $"file not found: {name}"));
                    return;
                }
                loaded.Add(Image.Load<Rgba32>(path));
            }

            using var stitched = ArrangeFishTiles(loaded);
            if (stitched is null)
            {
                results.Add(new("Fish", false,
                    "each separate fishing sprite must be 8x16 (got " +
                    string.Join(", ", loaded.Select(i => $"{i.Width}x{i.Height}")) + ")"));
                return;
            }
            GbImageSlotWriter.TryWriteImage(rom, stitched, rev.Fish, edits, results);
        }
        catch (Exception ex)
        {
            results.Add(new("Fish", false, $"load/stitch failed: {ex.Message}"));
        }
        finally
        {
            foreach (var img in loaded)
            {
                img.Dispose();
            }
        }
    }

    /// <summary>
    /// Writes the player's 2-colour trainer-class palette (the battle-sprite /
    /// trainer-card colours), derived from the front image. Mirrors UPR-FVX's
    /// rewriteChrisImagePalette → writeTrainerPalette for Crystal.
    /// </summary>
    private static void TryImagePalette(
        byte[] rom,
        Image<Rgba32> front,
        Gen2PlayerGraphicsOffsets.Revision rev,
        List<(int Offset, byte[] Data)> edits,
        List<PlayerGraphicsSlotResult> results)
    {
        var paletteBytes = Gen2ImagePalette.DeriveTrainerPaletteBytes(front);
        if (paletteBytes is null)
        {
            results.Add(new("ImagePalette", false, "front image has an unusable colour count (need 1-4)"));
            return;
        }

        int tableOffset;
        try
        {
            tableOffset = GbPointerUtil.ReadPointer(
                rom, rev.TrainerPalettesPointerSite, rev.TrainerPalettesPointerBank);
        }
        catch (Exception ex)
        {
            results.Add(new("ImagePalette", false, $"could not resolve palette table: {ex.Message}"));
            return;
        }

        var offset = tableOffset + rev.PlayerTrainerClass * 4;
        if (offset < 0 || offset + paletteBytes.Length > rom.Length)
        {
            results.Add(new("ImagePalette", false, "palette table offset out of range for this ROM"));
            return;
        }

        if (rom.AsSpan(offset, paletteBytes.Length).SequenceEqual(paletteBytes))
        {
            results.Add(new("ImagePalette", true, "already matches (no-op)"));
            return;
        }

        edits.Add((offset, paletteBytes));
        results.Add(new("ImagePalette", true, $"trainer class {rev.PlayerTrainerClass} @ 0x{offset:X}"));
    }

    private static void TrySpritePalette(
        byte[] rom,
        string? name,
        Gen2PlayerGraphicsOffsets.Revision rev,
        List<(int Offset, byte[] Data)> edits,
        List<PlayerGraphicsSlotResult> results)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            results.Add(new("SpritePalette", false, "not provided by pack"));
            return;
        }

        var value = Gen2PlayerGraphicsOffsets.SpritePaletteByte(name);
        if (value is null)
        {
            results.Add(new("SpritePalette", false, $"unknown palette '{name}'"));
            return;
        }

        // Guard: both SpawnPlayer immediates must currently hold their vanilla
        // PAL_NPC_RED / PAL_NPC_BLUE values. A follower hack / decomp build /
        // romhack moves SpawnPlayer, and blindly writing here would corrupt an
        // unrelated instruction while leaving the real palette untouched.
        var vanilla = Gen2PlayerGraphicsOffsets.CrystalSpritePaletteVanilla;
        for (var i = 0; i < rev.SpritePaletteByteOffsets.Length; i++)
        {
            var off = rev.SpritePaletteByteOffsets[i];
            if (off < 0 || off >= rom.Length || rom[off] != vanilla[i])
            {
                results.Add(new("SpritePalette", false,
                    "SpawnPlayer palette bytes don't match retail Crystal (U) - romhack/decomp build? Skipped."));
                return;
            }
        }

        var wrote = 0;
        foreach (var offset in rev.SpritePaletteByteOffsets)
        {
            if (rom[offset] != value.Value)
            {
                edits.Add((offset, [value.Value]));
                wrote++;
            }
        }

        // Also set the OverworldSprites table default (PAL_OW_* index) for the
        // Chris walk / bike slots - the value the game falls back to when the
        // MAPOBJECT_PALETTE override isn't in effect. Guarded on vanilla so a
        // shifted table on a hack doesn't get corrupted.
        var owIndex = Gen2PlayerGraphicsOffsets.SpritePaletteOwIndex(name);
        var owVanilla = Gen2PlayerGraphicsOffsets.CrystalOwSpriteTableVanilla;
        if (owIndex is not null)
        {
            for (var i = 0; i < rev.OwSpriteTablePaletteOffsets.Length; i++)
            {
                var off = rev.OwSpriteTablePaletteOffsets[i];
                if (off >= 0 && off < rom.Length && rom[off] == owVanilla[i] && owIndex.Value != owVanilla[i])
                {
                    edits.Add((off, [owIndex.Value]));
                    wrote++;
                }
            }
        }

        results.Add(new("SpritePalette", true,
            wrote == 0
                ? "already matches (no-op)"
                : $"{name.ToUpperInvariant()} -> {wrote} slot(s)"));
    }
}
