using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;

namespace PKConnect.Core.graphics;

/// <summary>
/// A profile's sprite picks for one generation: sprite-set ids of the form
/// <c>&lt;character&gt;/genN/&lt;kind&gt;/&lt;variant&gt;</c> (see <see cref="ProtagonistSprites"/>).
/// Any may be null: front / overworld then default to the character's first set,
/// back to the set named like the chosen front.
/// </summary>
public record GenSpriteSelection(string? Front, string? Back, string? Overworld);

/// <summary>
/// Reads the protagonist sprite folders (connect-data/protagonists, beside the program):
/// <code>
///   slots.json                                     standard filename -> writer slot key, per gen + kind
///   &lt;character&gt;/genN/{front,back,overworld}/&lt;variant&gt;/   standard files + optional meta.ini
/// </code>
/// and combines a front, back and overworld set into one
/// <see cref="GraphicsPackStanza"/> whose file values are absolute paths, so the
/// existing per-gen writers run unchanged (<c>Path.Combine(packDir, absolute)</c>
/// returns the absolute path).
/// </summary>
public static partial class ProtagonistSprites
{
    /// <summary>Environment variable that, if set, points directly at the protagonists folder.</summary>
    public const string RootEnvVar = "PKCONNECT_PROTAGONISTS_DIR";

    private static readonly string[] Kinds = ["front", "back", "overworld"];

    private static readonly string[] RelativeCandidates =
    [
        // Beside the program, with the rest of Connect's data.
        Path.Combine("connect-data", "protagonists"),
    ];

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]*$")]
    private static partial Regex SegmentPattern();

    /// <summary>Resolves the protagonists root, or null if none of the candidates exist.</summary>
    public static string? ResolveRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable(RootEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        foreach (var rel in RelativeCandidates)
        {
            var full = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, rel));
            if (File.Exists(Path.Combine(full, "slots.json")))
            {
                return full;
            }
        }

        Log.Warning("[player-graphics] protagonists folder not found (set {Env} to override)", RootEnvVar);
        return null;
    }

    /// <summary>
    /// Builds the combined stanza for <paramref name="character"/> in generation
    /// <paramref name="gen"/> (1-4). <paramref name="romType"/> is the target game's
    /// RomType, recorded on the stanza. Returns null with a reason when nothing
    /// usable resolves; <paramref name="notes"/> collects non-fatal fallbacks.
    /// </summary>
    public static (GraphicsPackStanza? Stanza, string? Error) Resolve(
        string root, string? character, int gen, string romType, GenSpriteSelection? selection, List<string> notes)
    {
        var slots = LoadSlots(root);
        if (slots is null)
        {
            return (null, $"slots.json missing or unreadable under {root}");
        }
        selection ??= new(null, null, null);

        // The character: from any chosen id, else the request's own.
        character = FirstSegment(selection.Front) ?? FirstSegment(selection.Overworld) ?? FirstSegment(selection.Back) ?? character;
        if (string.IsNullOrWhiteSpace(character) || !SegmentPattern().IsMatch(character))
        {
            return (null, "No protagonist selected");
        }

        // Gen 1 and Gen 2 art is interchangeable (the writers re-fit it), so either may stand in for the other.
        string[] genFolders = gen switch
        {
            1 => ["gen1", "gen2"],
            2 => ["gen2", "gen1"],
            _ => [$"gen{gen}"],
        };

        var front = PickSet(root, selection.Front, "front", character, genFolders, notes);
        var overworld = PickSet(root, selection.Overworld, "overworld", character, genFolders, notes);

        // Back: the explicit pick, else the back set named like the chosen front, else the first.
        string? back;
        if (!string.IsNullOrWhiteSpace(selection.Back))
        {
            back = PickSet(root, selection.Back, "back", character, genFolders, notes);
        }
        else if (front is not null && Directory.Exists(Path.Combine(root, front.Replace("/front/", "/back/"))))
        {
            back = front.Replace("/front/", "/back/");
        }
        else
        {
            back = PickSet(root, null, "back", character, genFolders, notes);
        }

        if (front is null && back is null && overworld is null)
        {
            return (null, $"'{character}' has no gen{gen} sprite sets");
        }

        var stanza = new GraphicsPackStanza
        {
            Title = $"{character}: front={front ?? "-"} back={back ?? "-"} overworld={overworld ?? "-"}",
        };
        stanza.Fields["RomType"] = romType;

        foreach (var (kind, id) in new[] { ("front", front), ("back", back), ("overworld", overworld) })
        {
            if (id is null)
            {
                continue;
            }
            var setGen = id.Split('/')[1];
            var dir = Path.Combine(root, id);
            if (!slots.TryGetValue(setGen, out var genSlots) || !genSlots.TryGetValue(kind, out var fileToKey))
            {
                notes.Add($"{id}: no slot table for {setGen} {kind}");
                continue;
            }
            foreach (var (file, key) in fileToKey)
            {
                var path = Path.Combine(dir, file);
                if (File.Exists(path))
                {
                    stanza.Fields[key] = path;
                }
            }

            if (kind == "overworld")
            {
                ApplyOverworldMeta(stanza, setGen, ReadIni(Path.Combine(dir, "meta.ini")));
            }
        }

        return (stanza, null);
    }

    /// <summary>Mode switches the writers read, derived from the overworld set's meta.ini + files.</summary>
    private static void ApplyOverworldMeta(GraphicsPackStanza stanza, string setGen, Dictionary<string, string> meta)
    {
        if (setGen == "gen3")
        {
            // Format only decides the sheet layout: RSE (default) or FRLG run-frame order.
            var format = meta.GetValueOrDefault("Format") ?? "RSE";
            stanza.Fields["RunSpriteMode"] = format.Equals("FRLG", StringComparison.OrdinalIgnoreCase) ? "frlg" : "rse";
        }
        if (setGen is "gen1" or "gen2")
        {
            // GB fishing: three pose files ("separate") or one combined sheet.
            if (stanza.Get("FishFrontSprite") is not null)
            {
                stanza.Fields["FishSpriteMode"] = "separate";
            }
            else if (stanza.Get("FishSprite") is not null)
            {
                stanza.Fields["FishSpriteMode"] = "combined";
            }
        }
        if (meta.TryGetValue("SpritePalette", out var spritePalette))
        {
            stanza.Fields["SpritePalette"] = spritePalette;
        }
    }

    /// <summary>
    /// A valid set id of <paramref name="kind"/> for this character, or the first
    /// such set in <paramref name="genFolders"/> order when none (or an invalid one) was given.
    /// </summary>
    private static string? PickSet(
        string root, string? requested, string kind, string character, string[] genFolders, List<string> notes)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            // A set shared by both GB gens lives in one folder only, so also try the id in the other one.
            foreach (var candidate in new[] { requested, GbTwin(requested) })
            {
                var parts = candidate?.Split('/');
                if (parts is { Length: 4 }
                    && parts.All(p => SegmentPattern().IsMatch(p))
                    && parts[2] == kind
                    && genFolders.Contains(parts[1])
                    && Directory.Exists(Path.Combine(root, candidate!)))
                {
                    return candidate;
                }
            }
            notes.Add($"{kind} set '{requested}' not found - using the default");
        }

        foreach (var gen in genFolders)
        {
            var kindDir = Path.Combine(root, character, gen, kind);
            if (!Directory.Exists(kindDir))
            {
                continue;
            }
            var first = Directory.GetDirectories(kindDir).Select(Path.GetFileName).Order(StringComparer.Ordinal).FirstOrDefault();
            if (first is not null)
            {
                return $"{character}/{gen}/{kind}/{first}";
            }
        }
        return null;
    }

    /// <summary>The same set id in the other GB generation's folder (gen1 &lt;-&gt; gen2), else null.</summary>
    private static string? GbTwin(string id) =>
        id.Contains("/gen1/") ? id.Replace("/gen1/", "/gen2/")
        : id.Contains("/gen2/") ? id.Replace("/gen2/", "/gen1/")
        : null;

    /// <summary>The character segment of a well-formed set id, else null (so a bad id can't pick the character).</summary>
    private static string? FirstSegment(string? id)
    {
        var parts = id?.Split('/');
        return parts is { Length: 4 } && parts.All(p => SegmentPattern().IsMatch(p)) ? parts[0] : null;
    }

    /// <summary>slots.json "gens": gen -> kind -> (filename -> slot key).</summary>
    private static Dictionary<string, Dictionary<string, Dictionary<string, string>>>? LoadSlots(string root)
    {
        var path = Path.Combine(root, "slots.json");
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var result = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>();
            foreach (var gen in doc.RootElement.GetProperty("gens").EnumerateObject())
            {
                var kinds = new Dictionary<string, Dictionary<string, string>>();
                foreach (var kind in Kinds)
                {
                    if (gen.Value.TryGetProperty(kind, out var map))
                    {
                        kinds[kind] = map.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
                    }
                }
                result[gen.Name] = kinds;
            }
            return result;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[player-graphics] couldn't read {Path}", path);
            return null;
        }
    }

    /// <summary>Flat <c>Key=Value</c> file; <c>//</c> / <c>;</c> comment lines and trailing <c>// comments</c> stripped.</summary>
    private static Dictionary<string, string> ReadIni(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return result;
        }
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith(';'))
            {
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var value = line[(eq + 1)..].Trim();
            var comment = value.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
            {
                value = value[..comment].Trim();
            }
            result[line[..eq].Trim()] = value;
        }
        return result;
    }
}
