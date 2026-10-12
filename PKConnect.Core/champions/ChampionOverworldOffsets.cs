using System.Text.Json;

namespace PKConnect.Core.champions;

/// <summary>
/// One region champion NPC's overworld appearance: a walk-frame pic table +
/// the palette resource its paletteTag resolves to.
/// </summary>
public record ChampionOverworldInfo(int PicTableBase, int PaletteAddr, int FrameWidth, int FrameHeight);

/// <summary>
/// Two sources for <see cref="ChampionOverworldInfo"/>, matching the two
/// launch paths in PatchRoute:
///
///  - <see cref="FromBasePatchManifest"/>: a *visiting* launch applies the
///    Connect base patch first, which (as of the "champion placeholder"
///    rework) bakes in a single-purpose NPC — its own dedicated pic table AND
///    a non-shared palette — used only in the champion's room, so redirecting
///    it can never affect any other appearance of the real character
///    elsewhere in the boss-rush story. The manifest's "championPlaceholder.overworld"
///    section carries these addresses directly; read from there instead of
///    hardcoding, since they're base-patch-build-specific.
///
///  - <see cref="ForGame"/>: a *home-region* launch never applies the base
///    patch (it's the player's own untouched vanilla ROM), so no placeholder
///    exists there — this falls back to the real character's own vanilla
///    resource. That is a known, narrower limitation carried over from the
///    original design (see [[region-current-champion-feature]]): it only
///    matters if a home-region trainer replays after some other trainer has
///    already taken that region's title, and would show that other trainer's
///    sprite in the real character's other vanilla appearances too. Fixing it
///    would need the same placeholder treatment baked into the untouched
///    vanilla ROM, which isn't something PKVault can do without the user's
///    own decomp rebuild — so it's accepted for now, not silently patched
///    around.
///
/// Values in <see cref="ByGame"/> are ROM file offsets (symbol address minus
/// 0x08000000), verified against a real decomp .sym:
///   Emerald: EverGrandeCity_ChampionsRoom_EventScript_Wallace's own NPC.
///     sPicTable_Wallace  @ 0x0850792C -> 0x50792C
///     gObjectEventPal_Npc4 @ 0x084AD918 -> 0x4AD918 (paletteTag 0x1106, cross-
///       checked via sObjectEventSpritePalettes @ 0x0850BBC8) — a SHARED NPC
///       slot (Wallace has no dedicated one in vanilla), so this path also
///       carries the shared-palette caveat the placeholder path doesn't.
///   Ruby/Sapphire: gObjectEventGraphicsInfo_Steven's own NPC (R/S's champion
///     already IS Steven, so this is genuinely his own resource, not a
///     borrowed shared slot - still carries the same "another trainer's
///     colors would show on his other cameos" caveat if a different champion
///     is recorded, since there's no placeholder in the untouched vanilla ROM).
///     gObjectEventPicTable_Steven @ Ruby 0x0836ff38 -> 0x36ff38, Sapphire
///       0x0836fec8 -> 0x36fec8
///     gObjectEventPalette3 (paletteTag 0x1106, OBJ_EVENT_PAL_TAG_3) @ Ruby
///       0x08323c08 -> 0x323c08, Sapphire 0x08323b98 -> 0x323b98 - verified
///       against a clean decomp build's own .sym, not the base patch manifest
///       (which reflects the relinked build's shifted addresses).
/// </summary>
public static class ChampionOverworldOffsets
{
    private static readonly Dictionary<string, ChampionOverworldInfo> ByGame = new()
    {
        ["emerald"] = new(0x50792C, 0x4AD918, 16, 32),
        ["ruby"] = new(0x36FF38, 0x323C08, 16, 32),
        ["sapphire"] = new(0x36FEC8, 0x323B98, 16, 32),
    };

    // Ruby/Sapphire's commonly-circulated revision 2 dumps have this NPC's
    // pic table/palette at different addresses - same verification approach
    // as ByGame, against a REVISION=2 build. See GymTrainerTableData for why
    // R/S alone need this (Emerald has only one revision in the wild).
    private static readonly Dictionary<string, ChampionOverworldInfo> ByGameRev2 = new()
    {
        ["ruby"] = new(0x36FF50, 0x323C20, 16, 32),
        ["sapphire"] = new(0x36FEE0, 0x323BB0, 16, 32),
    };

    /// <summary>The home-region (vanilla, no base patch) fallback — the real character's own resource.</summary>
    public static ChampionOverworldInfo? ForGame(string gameId, byte[]? rom = null)
    {
        if (rom is { Length: > 0xBC } && rom[0xBC] == 2
            && ByGameRev2.TryGetValue(gameId, out var rev2Info))
        {
            return rev2Info;
        }
        return ByGame.GetValueOrDefault(gameId);
    }

    /// <summary>
    /// The visiting-launch (base-patched) placeholder's own resource, read
    /// straight from that base patch's manifest ("championPlaceholder.overworld").
    /// Null if the manifest predates the placeholder rework or the game has none.
    /// </summary>
    public static ChampionOverworldInfo? FromBasePatchManifest(JsonElement raw)
    {
        if (!raw.TryGetProperty("championPlaceholder", out var placeholder)
            || !placeholder.TryGetProperty("overworld", out var overworld)
            || !overworld.TryGetProperty("picTable", out var picTable) || picTable.ValueKind != JsonValueKind.Object
            || !overworld.TryGetProperty("palette", out var palette))
        {
            return null;
        }

        return new ChampionOverworldInfo(
            PicTableBase: picTable.GetProperty("offset").GetInt32(),
            PaletteAddr: palette.GetProperty("offset").GetInt32(),
            FrameWidth: picTable.GetProperty("frameWidth").GetInt32(),
            FrameHeight: picTable.GetProperty("frameHeight").GetInt32());
    }
}
