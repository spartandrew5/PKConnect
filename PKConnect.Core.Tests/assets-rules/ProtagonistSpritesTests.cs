namespace PKConnect.Core.Tests.assetsrules;

/// <summary>
/// The trainer sprites that come with the program: sprites taken from the official games, and
/// sets drawn or adapted by someone else - which are only bundled with their author's credit,
/// in the set's own meta.ini and in the folder's CREDITS.md.
/// </summary>
public class ProtagonistSpritesTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "connect-data", "protagonists");
    private static readonly string[] Kinds = ["front", "back", "overworld"];

    private static Dictionary<string, string> Ini(string path) => File.ReadAllLines(path)
        .Select(line => line.Split('=', 2))
        .Where(parts => parts.Length == 2)
        .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());

    /// <summary>The people a credit field names, leaving out the games' own makers and "nobody".</summary>
    private static bool NamesSomeone(string? field) =>
        !string.IsNullOrWhiteSpace(field) && field is not ("N/A" or "Gamefreak");

    [Fact]
    public void EveryFanMadeSet_IsCredited()
    {
        var sets = Directory.EnumerateFiles(Root, "meta.ini", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(sets);
        var credits = File.ReadAllText(Path.Combine(Root, "CREDITS.md"));

        foreach (var meta in sets)
        {
            // <character>/<gen>/<kind>/<variant>
            var parts = Path.GetRelativePath(Root, Path.GetDirectoryName(meta)!).Split(Path.DirectorySeparatorChar);
            var name = string.Join('/', parts);
            var ini = Ini(meta);
            var creator = ini.GetValueOrDefault("Creator");
            var adapter = ini.GetValueOrDefault("Adapter");

            Assert.True(!string.IsNullOrWhiteSpace(creator) || NamesSomeone(adapter), $"{name}: nobody is credited");
            if (!NamesSomeone(creator) && !NamesSomeone(adapter))
                continue; // from an official game

            Assert.True(credits.Contains($"`{parts[0]}/{parts[1]}/{parts[3]}`"), $"{name}: not listed in CREDITS.md");
        }
    }

    [Fact]
    public void EveryCharacter_IsDescribed()
    {
        foreach (var character in Directory.EnumerateDirectories(Root))
        {
            var id = Path.GetFileName(character);
            Assert.True(File.Exists(Path.Combine(character, "character.ini")), $"{id}: no character.ini");
            Assert.NotEmpty(Directory.EnumerateDirectories(character));
        }
    }

    [Fact]
    public void EverySet_IsAFrontABackOrAnOverworld_WithPictures()
    {
        foreach (var gen in Directory.EnumerateDirectories(Root).SelectMany(Directory.EnumerateDirectories))
        {
            foreach (var kind in Directory.EnumerateDirectories(gen))
            {
                Assert.Contains(Path.GetFileName(kind), Kinds);
                foreach (var set in Directory.EnumerateDirectories(kind))
                {
                    Assert.True(File.Exists(Path.Combine(set, "meta.ini")), $"{set}: no meta.ini");
                    Assert.NotEmpty(Directory.EnumerateFiles(set, "*.png"));
                }
            }
        }
    }
}
