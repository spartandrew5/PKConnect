using PKConnect.Core.formats;
using PKConnect.Core.launch;
using PKHeX.Core;
using PKVault.Core;

namespace PKConnect.Core.regions;

/// <summary>A region a trainer can travel to, with its badges in gym order.</summary>
public record RegionDTO(string Id, string Name, string[] Badges);

/// <summary>
/// A game a trainer can be in.
/// </summary>
/// <param name="Region">The region its story is set in.</param>
/// <param name="Regions">Every region whose gyms it has (HeartGold: Johto, then Kanto).</param>
/// <param name="MaxSpecies">The last National Dex number that exists in the game.</param>
/// <param name="MaxMove">The last move id that exists in the game.</param>
/// <param name="Playable">Connect can launch it and has a save to start a visit from.</param>
public record GameDTO(
    string Id, string Name, string Region, string[] Regions, int Generation,
    int MaxSpecies, int MaxMove, bool Playable, string? Note = null);

public record CatalogDTO(List<RegionDTO> Regions, List<GameDTO> Games);

/// <summary>
/// The regions and games Connect knows, in the order they are shown. The one place that says
/// what a game is called, where it is set and what can live in it.
/// </summary>
public static class ConnectCatalog
{
    public static readonly IReadOnlyList<RegionDTO> Regions =
    [
        new("kanto", "Kanto", ["Boulder", "Cascade", "Thunder", "Rainbow", "Soul", "Marsh", "Volcano", "Earth"]),
        new("johto", "Johto", ["Zephyr", "Hive", "Plain", "Fog", "Storm", "Mineral", "Glacier", "Rising"]),
        new("hoenn", "Hoenn", ["Stone", "Knuckle", "Dynamo", "Heat", "Balance", "Feather", "Mind", "Rain"]),
        new("sinnoh", "Sinnoh", ["Coal", "Forest", "Cobble", "Fen", "Relic", "Mine", "Icicle", "Beacon"]),
        // Black and White's eight; Black 2 and White 2 swap Trio and Freeze for Toxic and Wave.
        new("unova", "Unova", ["Trio", "Basic", "Insect", "Bolt", "Quake", "Jet", "Freeze", "Legend"]),
    ];

    private const string Gen1Note = "Held items and abilities don't exist in this Kanto. They come back when you travel on.";
    private const string Gen2Note = "Abilities and natures don't exist in this Johto. They come back when you travel on.";

    private sealed record Entry(
        string Id, string Name, string Region, int Generation, int MaxSpecies, int MaxMove,
        GameVersion Version, string? Note = null, string[]? Regions = null);

    // The Connect builds of the Gen 4 games have Gen 5's species and moves; the expansion builds
    // have them too (and more than Connect lets travel in).
    private static readonly Entry[] Games =
    [
        new("yellow", "Yellow", "kanto", 1, 151, 165, GameVersion.YW, Gen1Note),
        new("firered", "FireRed", "kanto", 3, 386, 354, GameVersion.FR),
        new("leafgreen", "LeafGreen", "kanto", 3, 386, 354, GameVersion.LG),
        new("fireredexpansion", "FireRed (expansion)", "kanto", 3, ExpansionConverter.MaxOfficialSpecies, Gen4AddedSpecies.MaxMove, GameVersion.FR),
        new("leafgreenexpansion", "LeafGreen (expansion)", "kanto", 3, ExpansionConverter.MaxOfficialSpecies, Gen4AddedSpecies.MaxMove, GameVersion.LG),
        new("crystal", "Crystal", "johto", 2, 251, 251, GameVersion.C, Gen2Note, ["johto", "kanto"]),
        new("heartgold", "HeartGold", "johto", 4, ExpansionConverter.MaxOfficialSpecies, Gen4AddedSpecies.MaxMove, GameVersion.HG, Regions: ["johto", "kanto"]),
        new("soulsilver", "SoulSilver", "johto", 4, ExpansionConverter.MaxOfficialSpecies, Gen4AddedSpecies.MaxMove, GameVersion.SS, Regions: ["johto", "kanto"]),
        new("emerald", "Emerald", "hoenn", 3, 386, 354, GameVersion.E),
        new("emeraldexpansion", "Emerald (expansion)", "hoenn", 3, ExpansionConverter.MaxOfficialSpecies, Gen4AddedSpecies.MaxMove, GameVersion.E),
        new("platinum", "Platinum", "sinnoh", 4, ExpansionConverter.MaxOfficialSpecies, Gen4AddedSpecies.MaxMove, GameVersion.Pt),
        new("black", "Black", "unova", 5, 649, 559, GameVersion.B),
        new("white", "White", "unova", 5, 649, 559, GameVersion.W),
        new("black2", "Black 2", "unova", 5, 649, 559, GameVersion.B2),
        new("white2", "White 2", "unova", 5, 649, 559, GameVersion.W2),
    ];

    private static string BaseSavesDirectory => Path.Combine(AppContext.BaseDirectory, "connect-data", "base-saves");

    private static GameDTO ToDTO(Entry game) => new(
        game.Id, game.Name, game.Region, game.Regions ?? [game.Region], game.Generation, game.MaxSpecies, game.MaxMove,
        Playable: LaunchPatchService.IsSupportedGame(game.Id)
            && Directory.Exists(BaseSavesDirectory)
            && Directory.EnumerateFiles(BaseSavesDirectory, $"{game.Id}.*").Any(),
        game.Note);

    public static CatalogDTO Get() => new([.. Regions], [.. Games.Select(ToDTO)]);

    public static GameDTO? Game(string? gameId) =>
        Games.FirstOrDefault(game => string.Equals(game.Id, gameId?.Trim(), StringComparison.OrdinalIgnoreCase)) is { } found ? ToDTO(found) : null;

    public static string RegionName(string regionId) => Regions.FirstOrDefault(r => r.Id == regionId)?.Name ?? regionId;

    private static bool SameFormat(GameVersion a, GameVersion b) =>
        a == b
        || (a is GameVersion.B or GameVersion.W && b is GameVersion.B or GameVersion.W)
        || (a is GameVersion.B2 or GameVersion.W2 && b is GameVersion.B2 or GameVersion.W2)
        || (a is GameVersion.HG or GameVersion.SS && b is GameVersion.HG or GameVersion.SS)
        || (a is GameVersion.FR or GameVersion.LG && b is GameVersion.FR or GameVersion.LG);

    /// <summary>The game a save file belongs to, or null for a game Connect doesn't know.</summary>
    public static string? GameIdFor(SaveFile save)
    {
        var extension = SaveExtension.For(save);
        if (extension is ExpansionSaveExtension expansion)
            return expansion.GameId;

        var version = extension?.PlayedVersion ?? save.Version;
        // PKHeX can't tell HeartGold from SoulSilver by the save alone.
        if (save is SAV4HGSS && version is not (GameVersion.HG or GameVersion.SS))
            version = GameVersion.HG;
        var detected = Games.FirstOrDefault(game => game.Version == version && !ExpansionSaveDetector.IsExpansionGameId(game.Id));

        // Paired versions share one save format, and a save made for one is played in the other
        // as it is (a trainer's Black save may say White inside). The saves Connect creates are
        // named for their game, so the name decides between games of the same pair.
        var named = Games.FirstOrDefault(game => string.Equals(game.Id, Path.GetFileNameWithoutExtension(save.Metadata.FilePath), StringComparison.OrdinalIgnoreCase));
        if (detected is not null && named is not null && named.Generation == detected.Generation && named.Region == detected.Region
            && !ExpansionSaveDetector.IsExpansionGameId(named.Id) && SameFormat(named.Version, detected.Version))
            return named.Id;
        return detected?.Id;
    }
}