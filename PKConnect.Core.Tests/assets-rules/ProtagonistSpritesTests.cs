namespace PKConnect.Core.Tests.assetsrules;

/// <summary>
/// The trainer sprites that come with the program. Only sprites taken from the official games
/// are bundled; a set drawn or adapted by someone else needs its author's credit first.
/// </summary>
public class ProtagonistSpritesTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "connect-data", "protagonists");

    private static Dictionary<string, string> Ini(string path) => File.ReadAllLines(path)
        .Select(line => line.Split('=', 2))
        .Where(parts => parts.Length == 2)
        .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());

    [Fact]
    public void EveryBundledSet_IsFromAnOfficialGame()
    {
        var sets = Directory.EnumerateFiles(Root, "meta.ini", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(sets);

        foreach (var meta in sets)
        {
            var name = Path.GetRelativePath(Root, Path.GetDirectoryName(meta)!);
            var ini = Ini(meta);
            Assert.True(ini.GetValueOrDefault("Creator") == "Gamefreak", $"{name}: creator is '{ini.GetValueOrDefault("Creator")}'");
            Assert.True(ini.GetValueOrDefault("Adapter", "N/A") == "N/A", $"{name}: adapted by '{ini.GetValueOrDefault("Adapter")}'");
        }
    }

    [Fact]
    public void EverySet_IsOfTheGenerationItsCharacterIsFrom()
    {
        // A character drawn in another generation's style isn't from a game.
        var native = new Dictionary<string, string[]>
        {
            ["red"] = ["gen1", "gen3"], ["leaf"] = ["gen3"],
            ["ethan"] = ["gen2", "gen4"], ["kris"] = ["gen2"], ["lyra"] = ["gen4"],
            ["brendan"] = ["gen3"], ["may"] = ["gen3"],
            ["lucas"] = ["gen4"], ["dawn"] = ["gen4"],
            ["hilbert"] = ["gen5"], ["hilda"] = ["gen5"], ["nate"] = ["gen5"], ["rosa"] = ["gen5"],
        };

        foreach (var character in Directory.EnumerateDirectories(Root))
        {
            var id = Path.GetFileName(character);
            Assert.True(native.ContainsKey(id), $"{id} has no official set");
            Assert.True(File.Exists(Path.Combine(character, "character.ini")), $"{id}: no character.ini");
            var gens = Directory.EnumerateDirectories(character).Select(Path.GetFileName).ToList();
            Assert.NotEmpty(gens);
            Assert.All(gens, gen => Assert.Contains(gen, native[id]));
        }
    }

    [Fact]
    public void EverySet_HasItsFrontBackAndOverworld()
    {
        foreach (var gen in Directory.EnumerateDirectories(Root).SelectMany(Directory.EnumerateDirectories))
        {
            var variants = Directory.EnumerateDirectories(Path.Combine(gen, "front")).Select(Path.GetFileName).ToList();
            foreach (var kind in new[] { "back", "overworld" })
                Assert.Equal(variants, Directory.EnumerateDirectories(Path.Combine(gen, kind)).Select(Path.GetFileName).ToList());
        }
    }
}