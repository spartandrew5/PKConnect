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
    };

    /// <summary>The slug in its canonical form, or null if it isn't a region.</summary>
    public static string? Normalize(string? region)
    {
        var slug = region?.Trim().ToLowerInvariant();
        return slug is not null && Known.Contains(slug) ? slug : null;
    }

    public static string? ForGameId(string? gameId) =>
        gameId is not null && RegionByGameId.TryGetValue(gameId.Trim(), out var region) ? region : null;

    /// <summary>
    /// Whether launching <paramref name="gameId"/> is a visit for a trainer from
    /// <paramref name="homeRegion"/>. A trainer with no (known) home region is at home everywhere.
    /// </summary>
    public static bool IsVisiting(string gameId, string? homeRegion) =>
        Normalize(homeRegion) is { } home && ForGameId(gameId) is { } region && home != region;
}