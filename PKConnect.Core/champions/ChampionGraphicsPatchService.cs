using PKConnect.Core.launch;
using PKConnect.Core.patching;
using PKConnect.Core.graphics;

namespace PKConnect.Core.champions;

/// <summary>
/// Home-region launch path (vanilla ROM, no Connect base patch): patches the
/// region's current champion NPC's battle front sprite to the trainer profile's
/// own chosen appearance — the same graphics pack shown on their Trainer Card
/// and in the Hall of Fame — so the champion you face actually looks like the
/// trainer who holds the title, not just fields their team
/// (<see cref="ChampionTeamPatchService"/>).
///
/// Reuses the exact same asset-writing routine as the player's own front
/// sprite (<see cref="Gen3PlayerGraphicsWriter.WriteFrontImageAt"/>), just
/// aimed at a different <see cref="Gen3FrontSlot"/>: the champion trainer's
/// own <c>trainerPic</c> byte (read live from their gTrainers[] struct) indexes
/// into the SAME shared gTrainerFrontPicTable the player's index does, so no
/// new table slot is added — the champion's existing entry is redirected to
/// freshly-relocated image + palette data, exactly like team patching redirects
/// the party pointer without shifting anything else in the ROM.
///
/// Also patches the champion NPC's overworld (walking) sprite — see
/// <see cref="ChampionOverworldOffsets"/> for the two address sources (the
/// visiting-launch base-patch's own dedicated "champion placeholder" NPC vs.
/// the home-region vanilla fallback, which still targets the real character).
///
/// GBA (Emerald/FireRed/LeafGreen/Ruby/Sapphire) only for now — Crystal/Yellow's
/// champion *team* patch is still unverified real-ROM offsets (see
/// ConnectTeamPatchService's GB path), so their graphics aren't attempted here either.
/// </summary>
public static class ChampionGraphicsPatchService
{
    public record ChampionGraphicsResult(string GameId, int TrainerId, bool Applied, string? Reason);

    /// <summary>gameId → the info.ini RomType stanza whose FrontImage applies.</summary>
    private static readonly Dictionary<string, string> RomTypeByGame = new()
    {
        ["firered"] = "FRLG",
        ["leafgreen"] = "FRLG",
        ["ruby"] = "RSE",
        ["sapphire"] = "RSE",
        ["emerald"] = "RSE",
    };

    public static (List<(int Offset, byte[] Data)> Edits, List<ChampionGraphicsResult> Results) BuildChampionGraphicsEdits(
        string gameId,
        byte[] rom,
        PlayerGraphicsRequest? championGraphics,
        RomFreeSpaceAllocator? allocator = null,
        // The visiting-launch placeholder's own overworld resource (see
        // ChampionOverworldOffsets.FromBasePatchManifest) — pass this from the
        // base-patched ROM's own manifest. Null (the default) falls back to the
        // vanilla/home-region resource in ChampionOverworldOffsets.ForGame.
        ChampionOverworldInfo? overworldOverride = null)
    {
        var edits = new List<(int Offset, byte[] Data)>();
        var results = new List<ChampionGraphicsResult>();

        var config = ChampionBattleConfigLoader.Load();
        if (!RomTypeByGame.TryGetValue(gameId, out var romType)
            || !config.GbaChampionTrainerIds.TryGetValue(gameId, out var trainerIds)
            || trainerIds.Length == 0
            || !GymTrainerTableData.IsSupportedGame(gameId))
        {
            return (edits, results);
        }

        var rev = Gen3PlayerGraphicsOffsets.ForRom(rom);
        if (rev is null)
        {
            foreach (var tid in trainerIds)
            {
                results.Add(new(gameId, tid, false, "Unsupported/non-vanilla Gen 3 ROM revision"));
            }
            return (edits, results);
        }

        if (championGraphics is null)
        {
            // Not an error — the champion may be a bundled default (no trainer
            // profile / appearance to draw from) rather than a real trainer.
            return (edits, results);
        }

        if (string.IsNullOrWhiteSpace(championGraphics.Character)
            && championGraphics.GenSprites is null
            && championGraphics.GenPacks?.GetValueOrDefault("3") is null)
        {
            return (edits, results);
        }

        var (resolved, error) = PlayerGraphicsPatchService.ResolveGraphics(championGraphics, 3, romType);
        if (resolved is null)
        {
            foreach (var tid in trainerIds)
            {
                results.Add(new(gameId, tid, false, error));
            }
            return (edits, results);
        }
        var stanza = resolved.Stanza;
        var packDir = resolved.PackDir;

        var fileName = stanza.Get("FrontImage");
        allocator ??= new RomFreeSpaceAllocator(rom, rev.FreeSpaceStart);

        foreach (var trainerId in trainerIds)
        {
            var structOffset = GymTrainerTableData.GetTrainerStructOffset(gameId, trainerId, rom);
            if (structOffset < 0 || structOffset + GymTrainerTableData.TrainerStructSize > rom.Length)
            {
                results.Add(new(gameId, trainerId, false, "Trainer struct offset is out of range for this ROM file"));
                continue;
            }

            // struct Trainer { ... /*0x03*/ u8 trainerPic; ... } (GymTrainerTableData).
            var trainerPic = rom[structOffset + 0x03];
            var slot = new Gen3FrontSlot(
                PointerEntry: rev.TrainerFrontImagesTable + trainerPic * 8,
                PaletteEntry: rev.TrainerFrontImagesTable + rev.TrainerFrontPaletteRel + trainerPic * 8,
                rev.Front.TilesWide, rev.Front.TilesTall);

            var (applied, reason) = Gen3PlayerGraphicsWriter.WriteFrontImageAt(rom, packDir, fileName, slot, allocator, edits);
            results.Add(new(gameId, trainerId, applied, reason));
        }

        // The champion's overworld (walking) sprite — their own dedicated pic
        // table, so this doesn't touch anything the player's own overworld
        // graphics uses. One NPC per game (not per trainerId — FireRed's 3
        // champion trainerIds are the same rival NPC across starters).
        if ((overworldOverride ?? ChampionOverworldOffsets.ForGame(gameId, rom)) is { } overworld)
        {
            var walkFile = stanza.Get("WalkSprite");
            var palette = Gen3PlayerGraphicsWriter.DerivePalette(packDir, walkFile);
            if (palette is null)
            {
                results.Add(new(gameId, trainerIds[0], false,
                    "OverworldWalk: no usable palette (walk sheet isn't 16-colour indexed)"));
            }
            else
            {
                edits.Add((overworld.PaletteAddr, palette.ToBytes()));

                var owResults = new List<PlayerGraphicsSlotResult>();
                var walkSlot = new Gen3OverworldSlot("Walk", "WalkSprite", 0, overworld.FrameWidth, overworld.FrameHeight);
                Gen3PlayerGraphicsWriter.TryOverworldSlotAt(
                    rom, packDir, walkFile, overworld.PicTableBase, walkSlot, palette,
                    framePermutation: null, combinedFrames: null, sourceFrameWidth: null, edits, owResults);

                foreach (var r in owResults)
                {
                    results.Add(new(gameId, trainerIds[0], r.Applied, $"OverworldWalk: {r.Reason}"));
                }
            }
        }

        return (edits, results);
    }
}
