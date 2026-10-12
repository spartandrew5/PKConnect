using PKConnect.Core.nds;
using Serilog;

namespace PKConnect.Core.graphics;

/// <summary>
/// Result of attempting to write one player-graphics slot (front sprite, walk
/// sprite, bike sprite, ...) into the ROM. A slot that doesn't validate against
/// the ROM is skipped, never force-written.
/// </summary>
public record PlayerGraphicsSlotResult(string Slot, bool Applied, string? Reason);

/// <summary>
/// The player-graphics selection for a trainer profile, sent by the frontend on
/// launch. <see cref="Character"/> is the protagonist id (e.g. "red").
/// <see cref="GenSprites"/> maps a generation number ("1".."4") to the chosen
/// front / back / overworld sprite sets (see <see cref="ProtagonistSprites"/>);
/// when it's present it wins. <see cref="GenPacks"/> is the legacy shape (generation
/// -> UPR-FVX pack folder id under public/trainers), kept until those packs are retired.
/// </summary>
public record PlayerGraphicsRequest(
    string? Character,
    Dictionary<string, string>? GenPacks,
    Dictionary<string, GenSpriteSelection>? GenSprites = null
);

/// <summary>
/// The sprites to write for one generation: a stanza (slot key -> file) plus the
/// folder its relative file values resolve against. <see cref="Label"/> names the
/// source for logs; <see cref="Notes"/> are non-fatal fallbacks worth reporting.
/// </summary>
public record ResolvedPlayerGraphics(GraphicsPackStanza Stanza, string PackDir, string Label, List<string> Notes);

/// <summary>
/// Ports UPR-FVX's custom player graphics ROM patching (RomHandler.setCustomPlayerGraphics /
/// CustomPlayerGraphics / GraphicsPack) to PKVault.
///
/// Maps a game id to its generation, resolves the trainer profile's chosen pack
/// (folder + info.ini stanza, with a Gen1&lt;-&gt;Gen2 / FRLG&lt;-&gt;RSE sibling
/// fallback), and dispatches to the gen-specific writer. Returns the (offset,
/// bytes) edits the launch flow merges into the single .ips, plus a per-slot
/// diagnostic list.
/// </summary>
public static class PlayerGraphicsPatchService
{
    /// <summary>
    /// The 7 player-graphics-patchable games and the generation whose pack /
    /// sprite format applies. yellow -> Gen 1, crystal -> Gen 2, the GBA games
    /// -> Gen 3 (FireRed/LeafGreen use the FRLG pack stanza, Ruby/Sapphire/
    /// Emerald the RSE stanza; that split is resolved when reading info.ini).
    /// </summary>
    private static readonly Dictionary<string, int> GenByGame = new()
    {
        ["yellow"] = 1,
        ["crystal"] = 2,
        ["firered"] = 3,
        ["leafgreen"] = 3,
        ["ruby"] = 3,
        ["sapphire"] = 3,
        ["emerald"] = 3,
        // pokeemerald-expansion builds: same sprites/stanza as the vanilla game, addresses from the build's .elf.
        [ExpansionGameId] = 3,
        [FireRedExpansionGameId] = 3,
        [LeafGreenExpansionGameId] = 3,
        ["heartgold"] = 4,
        ["soulsilver"] = 4,
        ["platinum"] = 4,
        ["diamond"] = 4,
        ["pearl"] = 4,
        ["black"] = 5,
        ["white"] = 5,
        ["black2"] = 5,
        ["white2"] = 5,
    };

    /// <summary>gameId → the info.ini RomType stanza that applies.</summary>
    private static readonly Dictionary<string, string> RomTypeByGame = new()
    {
        ["yellow"] = "Gen1",
        ["crystal"] = "Gen2",
        ["firered"] = "FRLG",
        ["leafgreen"] = "FRLG",
        ["ruby"] = "RSE",
        ["sapphire"] = "RSE",
        ["emerald"] = "RSE",
        [ExpansionGameId] = "RSE",
        [FireRedExpansionGameId] = "FRLG",
        [LeafGreenExpansionGameId] = "FRLG",
        ["heartgold"] = "HGSS",
        ["soulsilver"] = "HGSS",
        ["platinum"] = "PT",
        ["diamond"] = "DP",
        ["pearl"] = "DP",
        ["black"] = "BW",
        ["white"] = "BW",
        ["black2"] = "B2W2",
        ["white2"] = "B2W2",
    };

    /// <summary>
    /// Sibling stanzas to fall back to when the exact RomType is absent, tried
    /// in order. HGSS/DP/PT share the same overworld-sprite sheet layout (24
    /// stacked 32x32 frames - see Gen4PlayerGraphicsOffsets), so a pack with
    /// only a "Lucas (Platinum)" stanza still works on HeartGold.
    /// </summary>
    private static readonly Dictionary<string, string[]> SiblingRomTypes = new()
    {
        ["Gen1"] = ["Gen2"],
        ["Gen2"] = ["Gen1"],
        ["FRLG"] = ["RSE"],
        ["RSE"] = ["FRLG"],
        ["HGSS"] = ["DP", "PT"],
        ["DP"] = ["HGSS", "PT"],
        ["PT"] = ["HGSS", "DP"],
        ["BW"] = ["B2W2"],
        ["B2W2"] = ["BW"],
    };

    /// <summary>pokeemerald-expansion builds (see ExpansionSaveDetector.GameIds).</summary>
    public const string ExpansionGameId = PKConnect.Core.formats.ExpansionSaveDetector.GameId;
    public const string FireRedExpansionGameId = PKConnect.Core.formats.ExpansionSaveDetector.FireRedGameId;
    public const string LeafGreenExpansionGameId = PKConnect.Core.formats.ExpansionSaveDetector.LeafGreenGameId;

    public static bool IsSupportedGame(string gameId) => GenByGame.ContainsKey(gameId);

    /// <summary>
    /// Fixed (not per-trainer) sprites a Connect build always carries, from launcher/data/connect-sprites:
    /// HeartGold/SoulSilver's Professor Oak front pic over Red's class (Oak replaces Red at Mt. Silver).
    /// </summary>
    public static (List<(int Offset, byte[] Data)> Edits, List<PlayerGraphicsSlotResult> Results)
        BuildConnectFixedGraphicsEdits(string gameId, byte[] rom)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "connect-data", "connect-sprites");
        return gameId switch
        {
            "heartgold" or "soulsilver" => Gen4PlayerGraphicsWriter.BuildEdits(rom,
                new GraphicsPackStanza { Title = "Professor Oak", Fields = new(StringComparer.OrdinalIgnoreCase) { ["FrontImage"] = "oak_front.png" } },
                dir, Gen4PlayerGraphicsOffsets.HeartGoldOak),
            _ => ([], []),
        };
    }

    /// <summary>
    /// Builds the (offset, bytes) edits that patch this trainer's chosen
    /// protagonist sprites into <paramref name="rom"/>, so callers can merge
    /// them with the base patch / team edits into a single .ips. When no pack
    /// is selected / resolvable the edit list is empty and the results explain why.
    /// <paramref name="reservedRanges"/> are bytes earlier stages of the same patch
    /// already write; relocated sprite data is never allocated over them.
    /// <paramref name="romPath"/> is only needed for pokeemerald-expansion, whose
    /// addresses come from the build's .elf beside the ROM.
    /// </summary>
    public static (List<(int Offset, byte[] Data)> Edits, List<PlayerGraphicsSlotResult> Results)
        BuildPlayerGraphicsEdits(string gameId, byte[] rom, PlayerGraphicsRequest? request,
            IReadOnlyList<(int Start, int End)>? reservedRanges = null, string? romPath = null,
            bool expansionChampion = false)
    {
        var edits = new List<(int Offset, byte[] Data)>();
        var results = new List<PlayerGraphicsSlotResult>();

        if (!GenByGame.TryGetValue(gameId, out var gen))
        {
            return (edits, results);
        }

        var (resolved, error) = ResolveGraphics(request, gen, RomTypeByGame[gameId]);
        if (resolved is null)
        {
            results.Add(new("*", false, error));
            return (edits, results);
        }
        foreach (var note in resolved.Notes)
        {
            results.Add(new("*", true, note));
        }
        var stanza = resolved.Stanza;
        var packDir = resolved.PackDir;

        var (slotEdits, slotResults) = gen switch
        {
            1 => Gen1PlayerGraphicsWriter.BuildEdits(gameId, rom, stanza, packDir, reservedRanges),
            2 => Gen2PlayerGraphicsWriter.BuildEdits(gameId, rom, stanza, packDir, reservedRanges),
            // expansionChampion: the same sprite resolution, applied to the League champion's front pic
            // instead of the player (the champion trainer profile's own appearance).
            3 when PKConnect.Core.formats.ExpansionSaveDetector.IsExpansionGameId(gameId) => expansionChampion
                ? ExpansionPlayerGraphicsWriter.BuildChampionEdits(gameId, rom, romPath, stanza, packDir, reservedRanges)
                : ExpansionPlayerGraphicsWriter.BuildEdits(gameId, rom, romPath, stanza, packDir, reservedRanges),
            3 => Gen3PlayerGraphicsWriter.BuildEdits(rom, stanza, packDir, reservedRanges),
            // expansionChampion here: the Connect League champion's sprites (Platinum: Cynthia's class
            // front pic + the champion-room placeholder model) instead of the player's.
            4 => (expansionChampion ? Gen4PlayerGraphicsOffsets.ChampionForGame(gameId) : Gen4PlayerGraphicsOffsets.ForGame(gameId)) is { } gen4Rev
                ? Gen4PlayerGraphicsWriter.BuildEdits(rom, stanza, packDir, gen4Rev)
                : (new List<(int, byte[])>(),
                   new List<PlayerGraphicsSlotResult> { new("*", false, $"No Gen 4 {(expansionChampion ? "champion " : "")}offsets table for {gameId}") }),
            5 when !expansionChampion && Gen5PlayerGraphicsOffsets.ForGame(gameId) is { } gen5Rev =>
                Gen5PlayerGraphicsWriter.BuildEdits(rom, stanza, packDir, gen5Rev),
            _ => (new List<(int, byte[])>(),
                  new List<PlayerGraphicsSlotResult> { new("*", false, $"No writer for generation {gen}") }),
        };

        edits.AddRange(slotEdits);
        results.AddRange(slotResults);

        Log.Information("[player-graphics] {Game} {Source}: {Applied} applied, {Skipped} skipped",
            gameId, resolved.Label,
            slotResults.Count(r => r.Applied), slotResults.Count(r => !r.Applied));

        return (edits, results);
    }

    /// <summary>
    /// Gen 5: the player's sprites and, for a Connect game, the League champion's battle sprite
    /// (<paramref name="champion"/> = the champion trainer's own appearance) in one go - both
    /// fronts are in the same archive, which can only be rebuilt once per patch. Either may be null.
    /// </summary>
    public static (List<(int Offset, byte[] Data)> Edits, List<PlayerGraphicsSlotResult> Results)
        BuildGen5GraphicsEdits(string gameId, byte[] rom, PlayerGraphicsRequest? player, PlayerGraphicsRequest? champion)
    {
        var results = new List<PlayerGraphicsSlotResult>();
        if (Gen5PlayerGraphicsOffsets.ForGame(gameId) is not { } rev)
        {
            return ([], results);
        }
        var romType = RomTypeByGame[gameId];

        ResolvedPlayerGraphics? playerSprites = null;
        if (player is not null)
        {
            (playerSprites, var error) = ResolveGraphics(player, 5, romType);
            if (playerSprites is null) results.Add(new("*", false, error));
            else results.AddRange(playerSprites.Notes.Select(n => new PlayerGraphicsSlotResult("*", true, n)));
        }

        (GraphicsPackStanza, string, Gen5BattleSlot, int?)? championSprites = null;
        if (champion is not null && Gen5PlayerGraphicsOffsets.ChampionFrontForGame(gameId) is { } championFront)
        {
            var (resolved, error) = ResolveGraphics(champion, 5, romType);
            if (resolved is null) results.Add(new("champion", false, error));
            else championSprites = (resolved.Stanza, resolved.PackDir, championFront, Gen5PlayerGraphicsOffsets.ChampionOverworldMemberForGame(gameId));
        }

        if (playerSprites is null && championSprites is null)
        {
            return ([], results);
        }
        var (edits, slotResults) = Gen5PlayerGraphicsWriter.BuildEdits(rom, playerSprites?.Stanza, playerSprites?.PackDir, rev, championSprites);
        results.AddRange(slotResults);
        Log.Information("[player-graphics] {Game} player={Player} champion={Champion}: {Applied} applied, {Skipped} skipped",
            gameId, playerSprites?.Label ?? "-", championSprites?.Item1.Title ?? "-",
            slotResults.Count(r => r.Applied), slotResults.Count(r => !r.Applied));
        return (edits, results);
    }

    /// <summary>
    /// Resolves a profile's sprite selection for one generation into the stanza +
    /// folder the writers take. <see cref="PlayerGraphicsRequest.GenSprites"/> (the
    /// protagonist sprite sets) wins when present; otherwise the legacy
    /// <see cref="PlayerGraphicsRequest.GenPacks"/> pack id. Shared by the player
    /// and both champion graphics services. Returns null plus a reason when nothing resolves.
    /// </summary>
    public static (ResolvedPlayerGraphics? Resolved, string Error) ResolveGraphics(
        PlayerGraphicsRequest? request, int gen, string romType)
    {
        var notes = new List<string>();
        var genKey = gen.ToString();

        var root = ProtagonistSprites.ResolveRoot();
        if (root is null)
        {
            return (null, "Protagonist sprites folder not found on this machine");
        }
        var (built, buildError) = ProtagonistSprites.Resolve(
            root, request?.Character, gen, romType, request?.GenSprites?.GetValueOrDefault(genKey), notes);
        return built is null
            ? (null, buildError ?? $"No gen{gen} sprites resolved")
            : (new ResolvedPlayerGraphics(built, root, built.Title, notes), "");
    }
}
