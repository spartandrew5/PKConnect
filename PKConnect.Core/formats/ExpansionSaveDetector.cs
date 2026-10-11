using PKHeX.Core;

namespace PKConnect.Core.formats;

/// <summary>
/// Detects whether a loaded Gen3 save is from pokeemerald-expansion rather than a vanilla game.
/// </summary>
/// <remarks>
/// No ROM/game-code signature is available here - PKVault only sees the .sav bytes, and the
/// expansion's save container/checksum layout is byte-identical to vanilla. Instead this relies on
/// a structural anomaly that can't happen in a genuine vanilla save: vanilla PKHeX's own <see cref="PK3"/>
/// decodes species from the same 16-bit field the expansion repurposes as species(11 bits)+teraType(5 bits).
/// A real, checksum-valid, non-empty slot (nonzero PID) can never have <c>Species == 0</c> in a vanilla
/// game - if PKHeX reads one anyway, the bit layout underneath it must have changed.
/// Empirically confirmed (2026-09-23) against a real expansion save: every occupied party slot reads as
/// Species=0 through vanilla PK3 while still reporting ChecksumValid=true.
/// </remarks>
public static class ExpansionSaveDetector
{
    /// <summary>Game id / save file name PKVault uses for Emerald-build expansion saves (travel base saves).</summary>
    public const string GameId = "emeraldexpansion";
    public const string FireRedGameId = "fireredexpansion";
    public const string LeafGreenGameId = "leafgreenexpansion";

    public static readonly IReadOnlyList<string> GameIds = [GameId, FireRedGameId, LeafGreenGameId];

    public static bool IsExpansionGameId(string? gameId) =>
        gameId is not null && GameIds.Contains(gameId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Vanilla game an expansion build is based on (region, badges, theme, pack stanza).</summary>
    public static GameVersion BaseVersion(string gameId) => gameId.ToLowerInvariant() switch
    {
        FireRedGameId => GameVersion.FR,
        LeafGreenGameId => GameVersion.LG,
        _ => GameVersion.E,
    };

    public static bool TryDetect(SaveFile save, string? path) => DetectGameId(save, path) is not null;

    /// <summary>
    /// Which expansion build a save belongs to, or null for a vanilla save.
    ///
    /// Every expansion build - including <c>make firered</c> / <c>make leafgreen</c> -
    /// uses the Emerald-based save structs, so PKHeX loads them all as
    /// <see cref="SAV3E"/>, and nothing in the save says which build wrote it. The
    /// file name does: the decomp names its saves <c>pokefirered.sav</c> /
    /// <c>pokeleafgreen.sav</c>, and PKVault names the saves it creates after the
    /// game id. A vanilla FireRed/LeafGreen save always loads as SAV3FRLG, so a
    /// SAV3E save carrying those names can only be an expansion FRLG build.
    ///
    /// A save is expansion if it's named for one (a fresh pre-starter save has no
    /// Pokémon to inspect) or structurally (see <see cref="TryDetect(SaveFile)"/>);
    /// an unnamed structural match is taken to be the Emerald build.
    /// </summary>
    public static string? DetectGameId(SaveFile save, string? path)
    {
        if (save is not SAV3)
            return null;

        var name = path is null ? "" : Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        var named = save is not SAV3E ? null
            : name.Contains(FireRedGameId) || name.Contains("firered") ? FireRedGameId
            : name.Contains(LeafGreenGameId) || name.Contains("leafgreen") ? LeafGreenGameId
            : name.Contains(GameId) ? GameId
            : null;

        if (named is not null)
            return named;
        return TryDetect(save) ? GameId : null;
    }

    public static bool TryDetect(SaveFile save)
    {
        if (save is not SAV3 sav3)
            return false;

        if (sav3.HasParty && AnySlotLooksLikeExpansion(sav3.PartyData))
            return true;

        if (sav3.HasBox)
        {
            for (var box = 0; box < sav3.BoxCount; box++)
            {
                if (AnySlotLooksLikeExpansion(sav3.GetBoxData(box)))
                    return true;
            }
        }

        return false;
    }

    private static bool AnySlotLooksLikeExpansion(IEnumerable<PKM> slots)
    {
        foreach (var pk in slots)
        {
            if (pk.PID != 0 && pk.ChecksumValid && pk.Species == 0)
                return true;
        }
        return false;
    }
}
