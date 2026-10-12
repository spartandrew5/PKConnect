using SixLabors.ImageSharp;

namespace PKConnect.Core.regions;

/// <summary>
/// One sprite set of a character: its battle front, its back, or its overworld sheet, as drawn
/// for one game.
/// </summary>
/// <param name="Id">"&lt;character&gt;/gen3/front/emerald" - also its folder.</param>
/// <param name="Kind">"front", "back" or "overworld".</param>
/// <param name="Frame">The part of the preview image to show: x, y, width, height. Null for all of it.</param>
public record SpriteSetDTO(string Id, int Generation, string Kind, string Variant, string Title, int[]? Frame);

/// <summary>
/// A character a trainer can look like.
/// </summary>
/// <param name="Id">The folder of its sprite sets, e.g. "hilda".</param>
/// <param name="Generations">The generations it has sprites for ("gen3"); elsewhere the game's own protagonist is kept.</param>
public record CharacterDTO(string Id, string Name, int Order, string[] Generations, List<SpriteSetDTO> Sets);

/// <summary>
/// The characters whose sprite sets come with the program (connect-data/protagonists, beside it):
/// <code>
///   &lt;character&gt;/character.ini                          Name=, Order=
///   &lt;character&gt;/genN/{front,back,overworld}/&lt;variant&gt;/   the set's images + meta.ini
/// </code>
/// </summary>
public static class ConnectCharacters
{
    private static readonly string[] Kinds = ["front", "back", "overworld"];

    // The size of one walking frame, to show the first of a sheet.
    private static readonly Dictionary<string, (int Width, int Height)> WalkFrame = new()
    {
        ["gen1"] = (16, 16), ["gen2"] = (16, 16), ["gen3"] = (16, 32), ["gen4"] = (32, 32), ["gen5"] = (32, 32),
    };

    private static string Root => Path.Combine(AppContext.BaseDirectory, "connect-data", "protagonists");

    private static List<CharacterDTO>? cached;
    private static readonly Lock gate = new();

    public static List<CharacterDTO> All()
    {
        lock (gate)
            return cached ??= Read();
    }

    private static Dictionary<string, string> Ini(string path) => !File.Exists(path)
        ? []
        : File.ReadAllLines(path)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

    private static List<CharacterDTO> Read()
    {
        if (!Directory.Exists(Root))
            return [];

        return [.. Directory.EnumerateDirectories(Root)
            .Where(folder => File.Exists(Path.Combine(folder, "character.ini")))
            .Select(folder =>
            {
                var ini = Ini(Path.Combine(folder, "character.ini"));
                var id = Path.GetFileName(folder);
                var generations = Directory.EnumerateDirectories(folder).Select(gen => Path.GetFileName(gen)).Order().ToArray();

                List<SpriteSetDTO> sets = [];
                foreach (var gen in generations)
                    foreach (var kind in Kinds)
                    {
                        var kindFolder = Path.Combine(folder, gen, kind);
                        if (!Directory.Exists(kindFolder) || !int.TryParse(gen.AsSpan(3), out var number))
                            continue;
                        foreach (var set in Directory.EnumerateDirectories(kindFolder).Order(StringComparer.Ordinal))
                        {
                            var variant = Path.GetFileName(set);
                            var meta = Ini(Path.Combine(set, "meta.ini"));
                            sets.Add(new($"{id}/{gen}/{kind}/{variant}", number, kind, variant,
                                meta.GetValueOrDefault("Title", variant), PreviewFrame(set, gen, kind, meta)));
                        }
                    }

                return new CharacterDTO(
                    id,
                    ini.GetValueOrDefault("Name", id),
                    int.TryParse(ini.GetValueOrDefault("Order"), out var order) ? order : int.MaxValue,
                    generations,
                    sets);
            })
            .OrderBy(character => character.Order).ThenBy(character => character.Name)];
    }

    private static string PreviewFile(string kind) => kind == "overworld" ? "walk.png" : $"{kind}.png";

    // The battle pose's first frame, or the overworld's first walking frame.
    private static int[]? PreviewFrame(string setFolder, string gen, string kind, Dictionary<string, string> meta)
    {
        var file = Path.Combine(setFolder, PreviewFile(kind));
        if (!File.Exists(file))
            return null;
        var info = Image.Identify(file);
        var (w, h) = (info.Width, info.Height);

        if (kind != "overworld")
        {
            // A Gen 5 back is a sheet of pieces with the standing pose at its left edge.
            if (gen == "gen5" && kind == "back") return [0, 0, 64, h];
            // A back is often a strip of square frames.
            if (h > w) return [0, 0, w, w];
            if (w > h * 1.5) return [0, 0, h, h];
            return null;
        }

        var (fw, fh) = WalkFrame.GetValueOrDefault(gen, (16, 16));
        if (w <= fw && h <= fh)
            return null;
        // A sheet may not begin on the pose facing the player; its meta.ini then says which frame does.
        var index = int.TryParse(meta.GetValueOrDefault("WalkPreviewFrame"), out var n) ? n : 0;
        return h > w ? [0, index * fh, fw, fh] : [index * fw, 0, fw, fh];
    }

    /// <summary>
    /// The image a sprite set is shown by, or null for an id that isn't one of the sets. The id
    /// is looked up among the sets that exist, never used as a path.
    /// </summary>
    public static string? PreviewPath(string setId)
    {
        var set = All().SelectMany(character => character.Sets).FirstOrDefault(s => s.Id == setId);
        if (set is null)
            return null;
        var path = Path.Combine(Root, Path.Combine(set.Id.Split('/')), PreviewFile(set.Kind));
        return File.Exists(path) ? path : null;
    }
}