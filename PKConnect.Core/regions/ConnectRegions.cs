using PKHeX.Core;

namespace PKConnect.Core.regions;

/// <summary>
/// Region slugs ("kanto", "johto", "hoenn" ...) and the games that belong to each. A trainer's
/// home region is where their journey started: that region's games launch as themselves, every
/// other game launches with its Connect changes for a visitor.
/// </summary>
public static class ConnectRegions
{
    private static readonly HashSet<string> Known =
        ["kanto", "johto", "hoenn", "sinnoh", "unova", "kalos", "alola", "galar", "paldea"];

    private static readonly Dictionary<string, string> RegionByGameId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = "kanto",
        ["blue"] = "kanto",
        ["green"] = "kanto",
        ["yellow"] = "kanto",
        ["firered"] = "kanto",
        ["leafgreen"] = "kanto",
        ["gold"] = "johto",
        ["silver"] = "johto",
        ["crystal"] = "johto",
        ["ruby"] = "hoenn",
        ["sapphire"] = "hoenn",
        ["emerald"] = "hoenn",
        ["heartgold"] = "johto",
        ["soulsilver"] = "johto",
        ["diamond"] = "sinnoh",
        ["pearl"] = "sinnoh",
        ["platinum"] = "sinnoh",
        ["black"] = "unova",
        ["white"] = "unova",
        ["black2"] = "unova",
        ["white2"] = "unova",
        // pokeemerald-expansion builds, by the game they are built as.
        ["emeraldexpansion"] = "hoenn",
        ["fireredexpansion"] = "kanto",
        ["leafgreenexpansion"] = "kanto",
    };

    /// <summary>The slug in its canonical form, or null if it isn't a region.</summary>
    public static string? Normalize(string? region)
    {
        var slug = region?.Trim().ToLowerInvariant();
        return slug is not null && Known.Contains(slug) ? slug : null;
    }

    public static string? ForGameId(string? gameId) =>
        gameId is not null && RegionByGameId.TryGetValue(gameId.Trim(), out var region) ? region : null;

    /// <summary>The region of a save file's game.</summary>
    public static string? ForSave(SaveFile save, string? path = null) =>
        ForGameVersion(PKVault.Core.SaveExtension.For(save)?.PlayedVersion ?? save.Version);

    public static string? ForGameVersion(GameVersion version) => version switch
    {
        GameVersion.RD or GameVersion.GN or GameVersion.BU or GameVersion.YW or GameVersion.RB or GameVersion.RBY
            or GameVersion.FR or GameVersion.LG or GameVersion.FRLG => "kanto",
        GameVersion.GD or GameVersion.SI or GameVersion.C or GameVersion.GS or GameVersion.GSC
            or GameVersion.HG or GameVersion.SS or GameVersion.HGSS => "johto",
        GameVersion.R or GameVersion.S or GameVersion.E or GameVersion.RS or GameVersion.RSE => "hoenn",
        GameVersion.D or GameVersion.P or GameVersion.Pt or GameVersion.DP or GameVersion.DPPt => "sinnoh",
        GameVersion.B or GameVersion.W or GameVersion.B2 or GameVersion.W2 or GameVersion.BW
            or GameVersion.B2W2 or GameVersion.Gen5 => "unova",
        _ => null,
    };

    /// <summary>
    /// Whether launching <paramref name="gameId"/> is a visit for a trainer from
    /// <paramref name="homeRegion"/>. A trainer with no (known) home region is at home everywhere.
    /// </summary>
    public static bool IsVisiting(string gameId, string? homeRegion) =>
        Normalize(homeRegion) is { } home && ForGameId(gameId) is { } region && home != region;
}