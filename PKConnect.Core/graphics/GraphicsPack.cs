namespace PKConnect.Core.graphics;

/// <summary>
/// One <c>[stanza]</c> of a pack's <c>info.ini</c>, targeting a single ROM type.
/// UPR-FVX convention: keys are slot names (<c>FrontImage</c>, <c>WalkSprite</c>,
/// <c>BikeSprite</c>, <c>FishSprite</c>, ...) whose values are PNG filenames
/// relative to the pack folder, plus mode switches (<c>FishSpriteMode</c>,
/// <c>RunSpriteMode</c>, <c>SpritePalette</c>).
/// </summary>
public class GraphicsPackStanza
{
    public string Title { get; init; } = "";

    /// <summary>Raw key → value for every line in the stanza.</summary>
    public Dictionary<string, string> Fields { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gen1 | Gen2 | FRLG | RSE (as written in info.ini).</summary>
    public string RomType => Get("RomType") ?? "";

    public string? Get(string key) => Fields.TryGetValue(key, out var v) ? v : null;
}

/// <summary>
/// A custom-player-graphics pack: a folder with an <c>info.ini</c> (one or more
/// stanzas) and loose PNGs. Ported from UPR-FVX's <c>GraphicsPack</c>.
/// </summary>
public class GraphicsPack
{
    /// <summary>
    /// Selection id: the folder name ("red", "chris"), or - when the folder
    /// holds more than one stanza for the same ROM type - the folder plus a
    /// "@variant" suffix ("brendan_gbc@emerald").
    /// </summary>
    public string PackId { get; init; } = "";

    /// <summary>Absolute path to the pack folder.</summary>
    public string PackDir { get; init; } = "";

    /// <summary>The "@variant" slug from <see cref="PackId"/>, or null for a whole-folder pack.</summary>
    public string? Variant { get; init; }

    public List<GraphicsPackStanza> Stanzas { get; init; } = [];

    public GraphicsPackStanza? StanzaFor(string romType)
    {
        var forType = Stanzas.Where(s => s.RomType.Equals(romType, StringComparison.OrdinalIgnoreCase)).ToList();
        if (forType.Count == 0)
        {
            return null;
        }
        if (Variant is null)
        {
            return forType[0];
        }
        return forType.FirstOrDefault(s => VariantSlug(s.Title).Equals(Variant, StringComparison.OrdinalIgnoreCase))
               ?? forType[0];
    }

    public bool Supports(string romType) => StanzaFor(romType) is not null;

    /// <summary>
    /// Like <see cref="StanzaFor"/>, but only returns a stanza whose title
    /// actually slugs to <paramref name="variant"/> - never silently falls
    /// back to "the first stanza of this RomType" the way <see cref="StanzaFor"/>
    /// does when a caller already knows the variant it wants. Needed so a
    /// cross-game variant selection (e.g. "lucas@diamond-pearl" launched
    /// against Platinum) can be checked against a SIBLING RomType's stanza
    /// before falling back to this game's own exact-RomType stanza, which
    /// might just be that same character's unmodified vanilla look for this
    /// game - see PlayerGraphicsPatchService.BuildPlayerGraphicsEdits.
    /// </summary>
    public GraphicsPackStanza? StanzaForExactVariant(string romType, string variant) =>
        Stanzas.FirstOrDefault(s =>
            s.RomType.Equals(romType, StringComparison.OrdinalIgnoreCase)
            && VariantSlug(s.Title).Equals(variant, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Slug for a stanza title (the parenthetical suffix if present, else the
    /// whole title). MUST stay in sync with <c>variantSlug()</c> in
    /// scripts/generate-trainer-manifest.mjs. Two stanza titles in one folder
    /// that slug the same are unsupported (the manifest generator warns); this
    /// falls back to the first matching stanza.
    /// </summary>
    public static string VariantSlug(string title)
    {
        var paren = System.Text.RegularExpressions.Regex.Match(title ?? "", @"\(([^)]*)\)\s*$");
        var basis = paren.Success ? paren.Groups[1].Value : title ?? "";
        var slug = System.Text.RegularExpressions.Regex.Replace(basis.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "v" : slug;
    }
}
