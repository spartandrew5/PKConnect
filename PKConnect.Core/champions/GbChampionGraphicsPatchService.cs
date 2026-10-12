using System.Text.Json;
using PKConnect.Core.launch;
using PKConnect.Core.patching;
using PKConnect.Core.graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PKConnect.Core.champions;

/// <summary>
/// Patches the region's current champion NPC's battle front sprite (Crystal:
/// + palette) and overworld walking sprite into the GB/GBC games (Crystal's
/// Lance, Yellow's final rival battle). Team patching is
/// <see cref="ConnectTeamPatchService"/> (visiting) and
/// <see cref="GbHomeChampionPatchService"/> (home, Crystal only - Yellow's
/// party format is identical either way).
///
/// Every launch gets a dedicated, non-shared champion placeholder resource,
/// from one of two mutually-exclusive sources depending on context: a
/// visiting launch's Connect base patch ("championPlaceholder" - only exists
/// once that patch is applied), or a home-region launch's always-on micro
/// patch ("homeRegionChampionPlaceholder" - see
/// <see cref="GbHomeChampionPatchService"/>, applied even though the Connect
/// patch itself never is there). Either way the champion pic's *pointer table
/// entry* sits at a fixed file offset the relevant patch doesn't move, so the
/// actual pic data address is always resolved live off that entry rather than
/// hardcoded. Neither game's champion pic entry carries a bank byte to
/// repoint, so this only ever overwrites in place, capped at whichever
/// context's manifest-declared capacity — a custom sprite that doesn't
/// compress small enough is skipped, never force-written past it.
///
/// The overworld sprite likewise always targets that context's dedicated
/// picture data (never the real character's own shared sprite - Crystal's
/// LanceSpriteGFX and Yellow's BlueSprite are each used by several other
/// vanilla scenes, so overwriting either would silently re-skin the character
/// everywhere else too).
///
/// Crystal's overworld sprite *palette* is a shared preset-tag system (one of
/// 8 vanilla PAL_OW_* colour sets, not a per-NPC RGB table), and Yellow has no
/// per-sprite overworld palette at all - neither is patched here.
/// </summary>
public static class GbChampionGraphicsPatchService
{
    public record GbChampionGraphicsResult(string Slot, bool Applied, string? Reason);

    private static readonly Dictionary<string, string> RomTypeByGame = new()
    {
        ["crystal"] = "Gen2",
        ["yellow"] = "Gen1",
    };

    public static bool IsSupportedGame(string gameId) => RomTypeByGame.ContainsKey(gameId);

    public static (List<(int Offset, byte[] Data)> Edits, List<GbChampionGraphicsResult> Results) BuildChampionGraphicsEdits(
        string gameId,
        byte[] rom,
        PlayerGraphicsRequest? championGraphics,
        bool isVisitingLaunch)
    {
        var edits = new List<(int Offset, byte[] Data)>();
        var results = new List<GbChampionGraphicsResult>();

        if (!RomTypeByGame.TryGetValue(gameId, out var romType) || championGraphics is null)
        {
            return (edits, results);
        }

        var gen = gameId == "crystal" ? 2 : 1;
        if (string.IsNullOrWhiteSpace(championGraphics.Character)
            && championGraphics.GenSprites is null
            && championGraphics.GenPacks?.GetValueOrDefault(gen.ToString()) is null)
        {
            return (edits, results);
        }

        var (resolved, error) = PlayerGraphicsPatchService.ResolveGraphics(championGraphics, gen, romType);
        if (resolved is null)
        {
            results.Add(new("*", false, error));
            return (edits, results);
        }
        var stanza = resolved.Stanza;
        var packDir = resolved.PackDir;

        // Two independent resources describe the champion placeholder,
        // resolved by launch context: the Connect base patch's own
        // "championPlaceholder" (only present once that patch is applied - a
        // visiting launch) vs. the always-on home micro-patch's
        // "homeRegionChampionPlaceholder" (a home-region launch never applies
        // the Connect patch, so that section wouldn't exist there).
        var placeholder = isVisitingLaunch
            ? BasePatchService.Load(gameId).Manifest.Raw.GetProperty("championPlaceholder")
            : GbHomeChampionPatchService.Load(gameId).Raw.GetProperty("homeRegionChampionPlaceholder");
        var frontPic = placeholder.GetProperty("frontPic");

        if (gameId == "crystal")
        {
            TryCrystalFrontPic(rom, packDir, stanza, frontPic, edits, results);
        }
        else
        {
            TryYellowFrontPic(rom, packDir, stanza, frontPic, edits, results);
        }

        if (placeholder.TryGetProperty("overworld", out var overworld))
        {
            TryOverworld(rom, packDir, stanza, overworld, edits, results);
        }

        return (edits, results);
    }

    /// <summary>
    /// Resolves the GB pointer-table entry pokecrystal uses for trainer front
    /// pics: a bank byte (stored as bank - 0x36) followed by a 2-byte in-bank
    /// address.
    /// </summary>
    private static int ResolveCrystalPicOffset(byte[] rom, int tableEntryOffset)
    {
        var bank = rom[tableEntryOffset] + 0x36;
        return GbPointerUtil.ReadPointer(rom, tableEntryOffset + 1, bank);
    }

    private static void TryCrystalFrontPic(
        byte[] rom,
        string packDir,
        GraphicsPackStanza stanza,
        JsonElement frontPic,
        List<(int Offset, byte[] Data)> edits,
        List<GbChampionGraphicsResult> results)
    {
        var fileName = stanza.Get("FrontImage");
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

        var tableEntryOffset = frontPic.GetProperty("tableEntryOffset").GetInt32();
        int dataOffset;
        try
        {
            dataOffset = ResolveCrystalPicOffset(rom, tableEntryOffset);
        }
        catch (Exception ex)
        {
            results.Add(new("FrontImage", false, $"could not resolve pic pointer: {ex.Message}"));
            return;
        }

        // Both contexts' manifests give the capacity nested under defaultImage.
        var maxBytes = frontPic.TryGetProperty("defaultImage", out var defaultImage)
            && defaultImage.TryGetProperty("capacity", out var capacityEl)
            ? capacityEl.GetInt32()
            : 532; // fallback: the vanilla ChampionPic's own current size, no known slack.

        using var front = Image.Load<Rgba32>(path);
        var side = 7 * GbTileCodec.TileSize;
        if (front.Width != side || front.Height != side)
        {
            results.Add(new("FrontImage", false, $"expected {side}x{side}, got {front.Width}x{front.Height}"));
            return;
        }

        byte[] compressed;
        try
        {
            compressed = Gen2SpriteCompressor.Compress(GbTileCodec.Encode(front, columnMajor: true));
        }
        catch (Exception ex)
        {
            results.Add(new("FrontImage", false, $"encode/compress failed: {ex.Message}"));
            return;
        }

        if (dataOffset < 0 || dataOffset + compressed.Length > rom.Length)
        {
            results.Add(new("FrontImage", false, "resolved pic data offset is out of range for this ROM"));
            return;
        }

        if (compressed.Length > maxBytes)
        {
            results.Add(new("FrontImage", false,
                $"compressed to {compressed.Length} bytes, only {maxBytes} available in place"));
        }
        else
        {
            edits.Add((dataOffset, compressed));
            results.Add(new("FrontImage", true, $"{compressed.Length}/{maxBytes} bytes @ 0x{dataOffset:X}"));
        }

        // Independent of whether the image itself fit: the champion's own
        // front-pic palette entry is safe to overwrite regardless (see this
        // class's summary), so a too-large custom sprite shouldn't also block
        // at least tinting the vanilla art to the pack's colours.
        if (frontPic.TryGetProperty("paletteTableEntryOffset", out var paletteOffsetEl)
            && paletteOffsetEl.ValueKind == JsonValueKind.Number)
        {
            var paletteBytes = Gen2ImagePalette.DeriveTrainerPaletteBytes(front);
            if (paletteBytes is null)
            {
                results.Add(new("FrontPalette", false, "front image has an unusable colour count (need 1-4)"));
            }
            else
            {
                edits.Add((paletteOffsetEl.GetInt32(), paletteBytes));
                results.Add(new("FrontPalette", true, null));
            }
        }
    }

    private static void TryYellowFrontPic(
        byte[] rom,
        string packDir,
        GraphicsPackStanza stanza,
        JsonElement frontPic,
        List<(int Offset, byte[] Data)> edits,
        List<GbChampionGraphicsResult> results)
    {
        var fileName = stanza.Get("FrontImage");
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

        var tableEntryOffset = frontPic.GetProperty("tableEntryOffset").GetInt32();
        var bank = frontPic.GetProperty("bank").GetInt32();
        int dataOffset;
        try
        {
            dataOffset = GbPointerUtil.ReadPointer(rom, tableEntryOffset, bank);
        }
        catch (Exception ex)
        {
            results.Add(new("FrontImage", false, $"could not resolve pic pointer: {ex.Message}"));
            return;
        }

        // Visiting's manifest names this "compressedSize"; the home patch's
        // (much roomier) slot names it "capacity".
        var maxBytes = frontPic.TryGetProperty("capacity", out var capEl) ? capEl.GetInt32()
            : frontPic.TryGetProperty("compressedSize", out var sizeEl) ? sizeEl.GetInt32()
            : 277;

        using var front = Image.Load<Rgba32>(path);
        var side = 7 * GbTileCodec.TileSize;
        if (front.Width != side || front.Height != side)
        {
            results.Add(new("FrontImage", false, $"expected {side}x{side}, got {front.Width}x{front.Height}"));
            return;
        }

        byte[] compressed;
        try
        {
            compressed = Gen1SpriteCompressor.Compress(GbTileCodec.Encode(front), width: 7);
        }
        catch (Exception ex)
        {
            results.Add(new("FrontImage", false, $"encode/compress failed: {ex.Message}"));
            return;
        }

        if (dataOffset < 0 || dataOffset + compressed.Length > rom.Length)
        {
            results.Add(new("FrontImage", false, "resolved pic data offset is out of range for this ROM"));
            return;
        }

        if (compressed.Length > maxBytes)
        {
            results.Add(new("FrontImage", false,
                $"compressed to {compressed.Length} bytes, only {maxBytes} available in place (no bank byte to repoint)"));
            return;
        }

        edits.Add((dataOffset, compressed));
        results.Add(new("FrontImage", true, $"{compressed.Length}/{maxBytes} bytes @ 0x{dataOffset:X}"));
    }

    /// <summary>
    /// The champion placeholder's dedicated overworld sprite - same fixed-size,
    /// uncompressed, in-place layout as the player's own Walk sprite (2x12
    /// tiles: six 16x16 frames stacked), so the same slot writer applies.
    /// </summary>
    private static void TryOverworld(
        byte[] rom,
        string packDir,
        GraphicsPackStanza stanza,
        JsonElement overworld,
        List<(int Offset, byte[] Data)> edits,
        List<GbChampionGraphicsResult> results)
    {
        if (!overworld.TryGetProperty("picData", out var picData) || !picData.TryGetProperty("offset", out var offsetEl))
        {
            return;
        }

        var slot = new Gen1SpriteSlot("OverworldWalk", offsetEl.GetInt32(), 2, 12);
        var slotResults = new List<PlayerGraphicsSlotResult>();
        GbImageSlotWriter.TryWriteFile(rom, packDir, stanza.Get("WalkSprite"), slot, edits, slotResults);
        results.AddRange(slotResults.Select(r => new GbChampionGraphicsResult(r.Slot, r.Applied, r.Reason)));
    }
}
