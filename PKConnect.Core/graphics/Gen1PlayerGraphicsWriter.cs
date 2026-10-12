using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PKConnect.Core.graphics;

/// <summary>
/// Writes the Gen 1 (Yellow) player sprites:
///  - walk / bike / fishing overworld sprites - uncompressed, fixed-size 2bpp
///    tile blocks written in place at static offsets.
///  - the front battle sprite - Gen1-compressed, written into a free bank, with
///    all 6 front-image pointers and 6 bank bytes repointed (mirrors UPR-FVX's
///    IndirectBankDataRewriter).
///  - the back battle sprite, entangled with the Old Man / Prof. Oak back images
///    (stored contiguously, relocated together).
/// </summary>
public static class Gen1PlayerGraphicsWriter
{
    public static (List<(int Offset, byte[] Data)> Edits, List<PlayerGraphicsSlotResult> Results)
        BuildEdits(string gameId, byte[] rom, GraphicsPackStanza stanza, string packDir,
            IReadOnlyList<(int Start, int End)>? reservedRanges = null)
    {
        var edits = new List<(int Offset, byte[] Data)>();
        var results = new List<PlayerGraphicsSlotResult>();

        var rev = Gen1PlayerGraphicsOffsets.ForGame(gameId);
        if (rev is null)
        {
            results.Add(new("*", false, $"No Gen 1 offsets for game '{gameId}'"));
            return (edits, results);
        }

        var allocator = new GbBankAllocator(rom, rev.UnusedBanks, reservedRanges);

        TryFrontImage(rom, packDir, stanza.Get("FrontImage"), rev, allocator, edits, results);
        TryBackImage(rom, packDir, stanza.Get("BackImage"), rev, allocator, edits, results);

        GbImageSlotWriter.TryWriteFile(rom, packDir, stanza.Get("WalkSprite"), rev.Walk, edits, results);
        GbImageSlotWriter.TryWriteFile(rom, packDir, stanza.Get("BikeSprite"), rev.Bike, edits, results);

        var fishMode = (stanza.Get("FishSpriteMode") ?? "separate").ToLowerInvariant();
        if (fishMode == "separate")
        {
            GbImageSlotWriter.TryWriteFile(rom, packDir, stanza.Get("FishFrontSprite"), rev.FishFront, edits, results);
            GbImageSlotWriter.TryWriteFile(rom, packDir, stanza.Get("FishBackSprite"), rev.FishBack, edits, results);
            GbImageSlotWriter.TryWriteFile(rom, packDir, stanza.Get("FishSideSprite"), rev.FishSide, edits, results);
        }
        else if (fishMode == "combined")
        {
            TryCombinedFish(rom, packDir, stanza.Get("FishSprite"), rev, edits, results);
        }
        else
        {
            results.Add(new("FishSprite", false, $"FishSpriteMode='{fishMode}' not supported"));
        }

        return (edits, results);
    }

    /// <summary>A combined 16x24 fish.png, split into the three 1x2-tile fishing slots (front, back, side).</summary>
    private static void TryCombinedFish(
        byte[] rom, string packDir, string? fileName, Gen1PlayerGraphicsOffsets.Revision rev,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            results.Add(new("FishSprite", false, "not provided by pack"));
            return;
        }
        var path = Path.Combine(packDir, fileName);
        if (!File.Exists(path))
        {
            results.Add(new("FishSprite", false, $"file not found: {fileName}"));
            return;
        }
        try
        {
            using var sheet = Image.Load<Rgba32>(path);
            var parts = GbFishSprites.SplitCombined(sheet);
            if (parts is null)
            {
                results.Add(new("FishSprite", false, $"expected a 16x24 combined fishing sheet, got {sheet.Width}x{sheet.Height}"));
                return;
            }
            try
            {
                GbImageSlotWriter.TryWriteImage(rom, parts[0], rev.FishFront, edits, results);
                GbImageSlotWriter.TryWriteImage(rom, parts[1], rev.FishBack, edits, results);
                GbImageSlotWriter.TryWriteImage(rom, parts[2], rev.FishSide, edits, results);
            }
            finally
            {
                parts.ForEach(p => p.Dispose());
            }
        }
        catch (Exception ex)
        {
            results.Add(new("FishSprite", false, $"load/split failed: {ex.Message}"));
        }
    }

    private static void TryFrontImage(
        byte[] rom,
        string packDir,
        string? fileName,
        Gen1PlayerGraphicsOffsets.Revision rev,
        GbBankAllocator allocator,
        List<(int Offset, byte[] Data)> edits,
        List<PlayerGraphicsSlotResult> results)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            results.Add(new("FrontImage", false, "not provided by pack"));
            return;
        }

        var path = Path.Combine(packDir, fileName);
        if (!File.Exists(path))
        {
            results.Add(new("FrontImage", false, $"file not found: {fileName}"));
            return;
        }

        var side = rev.FrontTiles * GbTileCodec.TileSize;

        byte[] compressed;
        try
        {
            using var image = Image.Load<Rgba32>(path);
            if (image.Width != side || image.Height != side)
            {
                results.Add(new("FrontImage", false,
                    $"expected {side}x{side}, got {image.Width}x{image.Height}"));
                return;
            }
            var tileData = GbTileCodec.Encode(image);
            compressed = Gen1SpriteCompressor.Compress(tileData, rev.FrontTiles);
        }
        catch (Exception ex)
        {
            results.Add(new("FrontImage", false, $"encode/compress failed: {ex.Message}"));
            return;
        }

        // Every pointer site must currently agree, and every bank byte must
        // currently agree - otherwise we're not on vanilla Yellow (U) and must
        // not touch it (same spirit as the gym patchers' original-byte checks).
        if (!GbPointerUtil.AllReadSameWord(rom, rev.FrontPointerSites, out var oldPtr))
        {
            results.Add(new("FrontImage", false, "front-image pointer sites disagree - wrong revision/romhack?"));
            return;
        }
        if (!GbPointerUtil.AllReadSameByte(rom, rev.FrontBankSites, out _))
        {
            results.Add(new("FrontImage", false, "front-image bank bytes disagree - wrong revision/romhack?"));
            return;
        }

        var newOffset = allocator.Allocate(compressed.Length, preferBank: rev.UnusedBanks.FirstOrDefault());
        if (newOffset is null)
        {
            results.Add(new("FrontImage", false, "no free ROM bank space for the compressed front sprite"));
            return;
        }

        var newBank = (byte)(newOffset.Value / GbBankAllocator.BankSize);

        edits.Add((newOffset.Value, compressed));
        foreach (var site in rev.FrontPointerSites)
        {
            edits.Add((site, GbPointerUtil.PtrBytes(newOffset.Value)));
        }
        foreach (var site in rev.FrontBankSites)
        {
            edits.Add((site, [newBank]));
        }

        results.Add(new("FrontImage", true,
            $"repointed 0x{oldPtr:X4} -> bank 0x{newBank:X2} ({compressed.Length} bytes)"));
    }

    /// <summary>
    /// The Gen 1 player back sprite is stored immediately before the catching-
    /// tutorial Old Man's back sprite and (Yellow only) Prof. Oak's. Relocating
    /// the player sprite means carrying those two along verbatim and repointing
    /// all four sites. Ported from UPR-FVX's rewritePlayerBackImage /
    /// repointOldManBackImage / repointOakBackImage.
    /// </summary>
    private static void TryBackImage(
        byte[] rom,
        string packDir,
        string? fileName,
        Gen1PlayerGraphicsOffsets.Revision rev,
        GbBankAllocator allocator,
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

        byte[] newPlayerBack;
        try
        {
            using var image = Image.Load<Rgba32>(path);
            if (image.Width != side || image.Height != side)
            {
                results.Add(new("BackImage", false,
                    $"expected {side}x{side}, got {image.Width}x{image.Height}"));
                return;
            }
            newPlayerBack = Gen1SpriteCompressor.Compress(GbTileCodec.Encode(image), rev.BackTiles);
        }
        catch (Exception ex)
        {
            results.Add(new("BackImage", false, $"encode/compress failed: {ex.Message}"));
            return;
        }

        var backSites = new[] { rev.BackPrimaryPointerSite }.Concat(rev.BackSecondaryPointerSites).ToArray();
        if (!GbPointerUtil.AllReadSameWord(rom, backSites, out _))
        {
            results.Add(new("BackImage", false, "back-image pointer sites disagree - wrong revision/romhack?"));
            return;
        }
        if (!GbPointerUtil.AllReadSameByte(rom, rev.BackBankSites, out var oldBank))
        {
            results.Add(new("BackImage", false, "back-image bank bytes disagree - wrong revision/romhack?"));
            return;
        }

        // Old Man (and, on Yellow, Prof. Oak) back sprites live in oldBank right
        // after the player's; carry them along unchanged.
        int oldManSrc, oakSrc = 0, oldManLen, oakLen = 0;
        byte[] oldManBytes, oakBytes = [];
        try
        {
            oldManSrc = GbPointerUtil.ReadPointer(rom, rev.OldManPointerSite, oldBank);
            oldManLen = Gen1SpriteCompressor.GetCompressedLength(rom, oldManSrc);
            oldManBytes = rom[oldManSrc..(oldManSrc + oldManLen)];

            if (rev.HasOakBackImage)
            {
                oakSrc = GbPointerUtil.ReadPointer(rom, rev.OakPointerSite, oldBank);
                oakLen = Gen1SpriteCompressor.GetCompressedLength(rom, oakSrc);
                oakBytes = rom[oakSrc..(oakSrc + oakLen)];
            }
        }
        catch (Exception ex)
        {
            results.Add(new("BackImage", false, $"could not read Old Man / Oak back sprites: {ex.Message}"));
            return;
        }

        var blob = new byte[newPlayerBack.Length + oldManBytes.Length + oakBytes.Length];
        newPlayerBack.CopyTo(blob, 0);
        oldManBytes.CopyTo(blob, newPlayerBack.Length);
        oakBytes.CopyTo(blob, newPlayerBack.Length + oldManBytes.Length);

        var newOffset = allocator.Allocate(blob.Length, preferBank: rev.UnusedBanks.FirstOrDefault());
        if (newOffset is null)
        {
            results.Add(new("BackImage", false, "no free ROM bank space for the compressed back sprites"));
            return;
        }

        var newBank = (byte)(newOffset.Value / GbBankAllocator.BankSize);
        var newOldMan = newOffset.Value + newPlayerBack.Length;
        var newOak = newOldMan + oldManBytes.Length;

        edits.Add((newOffset.Value, blob));
        foreach (var site in backSites)
        {
            edits.Add((site, GbPointerUtil.PtrBytes(newOffset.Value)));
        }
        foreach (var site in rev.BackBankSites)
        {
            edits.Add((site, [newBank]));
        }
        edits.Add((rev.OldManPointerSite, GbPointerUtil.PtrBytes(newOldMan)));
        if (rev.HasOakBackImage)
        {
            edits.Add((rev.OakPointerSite, GbPointerUtil.PtrBytes(newOak)));
        }

        results.Add(new("BackImage", true,
            $"relocated player+oldman{(rev.HasOakBackImage ? "+oak" : "")} to bank 0x{newBank:X2} " +
            $"({newPlayerBack.Length}+{oldManLen}+{oakLen} bytes)"));
    }
}
