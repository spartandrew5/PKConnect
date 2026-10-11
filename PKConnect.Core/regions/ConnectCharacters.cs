namespace PKConnect.Core.regions;

/// <summary>
/// A character a trainer can look like.
/// </summary>
/// <param name="Id">The folder of its sprite sets, e.g. "hilda".</param>
/// <param name="Generations">The generations it has sprites for ("gen3"); elsewhere the game's own protagonist is kept.</param>
public record CharacterDTO(string Id, string Name, int Order, string[] Generations);

/// <summary>
/// The characters whose sprite sets come with the program (connect-data/protagonists, beside it):
/// one folder each, with a character.ini and a folder per generation.
/// </summary>
public static class ConnectCharacters
{
    private static string Root => Path.Combine(AppContext.BaseDirectory, "connect-data", "protagonists");

    public static List<CharacterDTO> All()
    {
        if (!Directory.Exists(Root))
            return [];

        return [.. Directory.EnumerateDirectories(Root)
            .Where(folder => File.Exists(Path.Combine(folder, "character.ini")))
            .Select(folder =>
            {
                var ini = File.ReadAllLines(Path.Combine(folder, "character.ini"))
                    .Select(line => line.Split('=', 2))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
                var id = Path.GetFileName(folder);
                return new CharacterDTO(
                    id,
                    ini.GetValueOrDefault("Name", id),
                    int.TryParse(ini.GetValueOrDefault("Order"), out var order) ? order : int.MaxValue,
                    [.. Directory.EnumerateDirectories(folder).Select(gen => Path.GetFileName(gen)).Order()]);
            })
            .OrderBy(character => character.Order).ThenBy(character => character.Name)];
    }

    /// <summary>
    /// The picture a character is known by: the battle sprite of its newest generation. Null for
    /// an id that isn't one of the characters.
    /// </summary>
    public static string? PortraitPath(string characterId)
    {
        // Looked up among the folders that exist, never built from what was asked for.
        var character = All().FirstOrDefault(c => string.Equals(c.Id, characterId, StringComparison.OrdinalIgnoreCase));
        if (character is null)
            return null;

        foreach (var generation in character.Generations.OrderDescending())
        {
            var fronts = Path.Combine(Root, character.Id, generation, "front");
            if (!Directory.Exists(fronts))
                continue;
            var portrait = Directory.EnumerateDirectories(fronts).Order()
                .Select(set => Path.Combine(set, "front.png"))
                .FirstOrDefault(File.Exists);
            if (portrait is not null)
                return portrait;
        }
        return null;
    }
}