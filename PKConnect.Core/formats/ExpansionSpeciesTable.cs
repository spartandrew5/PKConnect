using System.Reflection;
using System.Text.Json.Serialization;
using PKHeX.Core;

namespace PKConnect.Core.formats;

/// <summary>
/// Base stats/types/abilities/gender/growth-rate data for pokeemerald-expansion's species roster
/// (up to species id 1572), which extends far past what PKHeX's own Gen3 <see cref="PersonalTable"/>
/// covers (real Gen3 species only, ~411 entries). Backed by <c>pkhexExt/data/expansion_species.json</c>,
/// generated from the decomp repo's own species_info headers via
/// <c>pkhexExt/data/extract-expansion-species.js</c> (not run at build time - regenerate manually
/// if the local decomp checkout's species roster changes).
/// </summary>
public static class ExpansionSpeciesTable
{
    private const string ResourceName = "PKConnect.Core.formats.data.expansion_species.json";

    private static readonly Lazy<Dictionary<ushort, ExpansionSpeciesEntry>> entries = new(Load);

    public static bool TryGet(ushort species, out ExpansionSpeciesEntry entry)
        => entries.Value.TryGetValue(species, out entry!);

    private static readonly Lazy<Dictionary<ushort, ushort>> rawByNatDex = new(() =>
    {
        // Lowest raw id wins, i.e. the base form (forms are appended after base species).
        var result = new Dictionary<ushort, ushort>();
        foreach (var (raw, e) in entries.Value.OrderBy(kv => kv.Key))
            if (e.NatDexNum > 0)
                result.TryAdd((ushort)e.NatDexNum, raw);
        return result;
    });

    /// <summary>
    /// Expansion species ids only match National Dex numbers up to the point where regional forms,
    /// Megas, Gigantamax etc. start being interleaved (e.g. raw 956 is Alolan Rattata, Pecharunt is
    /// raw 1434). Maps a raw expansion species id to its National Dex number (base species for forms).
    /// </summary>
    public static ushort GetNatDex(ushort rawSpecies)
        => TryGet(rawSpecies, out var e) && e.NatDexNum > 0 ? (ushort)e.NatDexNum : rawSpecies;

    /// <summary>Inverse of <see cref="GetNatDex"/>, resolving to the base-form raw species id.</summary>
    public static ushort GetRawSpecies(ushort natDex)
        => rawByNatDex.Value.TryGetValue(natDex, out var raw) ? raw : natDex;

    /// <summary>
    /// Builds a <see cref="PersonalInfo3"/> for the given species. Falls back to a zeroed entry
    /// (still a valid, non-throwing object) if the species isn't in the extracted table.
    /// </summary>
    public static PersonalInfo3 GetPersonalInfo(ushort species)
    {
        var raw = new byte[PersonalInfo3.SIZE];
        var info = new PersonalInfo3(raw);
        if (!TryGet(species, out var e))
            return info;

        info.HP = e.BaseHP;
        info.ATK = e.BaseAtk;
        info.DEF = e.BaseDef;
        info.SPE = e.BaseSpe;
        info.SPA = e.BaseSpA;
        info.SPD = e.BaseSpD;
        info.Type1 = (byte)e.Type1;
        info.Type2 = (byte)e.Type2;
        info.CatchRate = (byte)e.CatchRate;
        info.BaseEXP = e.BaseExp;
        info.Gender = (byte)e.GenderRatio;
        info.HatchCycles = (byte)e.EggCycles;
        info.EXPGrowth = (byte)e.GrowthRate;
        info.EggGroup1 = e.EggGroup1;
        info.EggGroup2 = e.EggGroup2;
        info.Ability1 = e.Ability1;
        info.Ability2 = e.Ability2;
        return info;
    }

    /// <summary>
    /// The expansion widens the ability index to 2 bits to support a 3rd (hidden) ability slot,
    /// which <see cref="PersonalInfo3"/> has no room for (it only models Ability1/Ability2).
    /// </summary>
    public static int GetHiddenAbility(ushort species)
        => TryGet(species, out var e) ? e.AbilityHidden : 0;

    public static IReadOnlyList<ushort> GetLearnableMoves(ushort rawSpecies)
        => TryGet(rawSpecies, out var e) ? e.LearnableMoves : [];

    private static Dictionary<ushort, ExpansionSpeciesEntry> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {ResourceName}");

        var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, ExpansionSpeciesEntry>>(stream)
            ?? [];

        var result = new Dictionary<ushort, ExpansionSpeciesEntry>(raw.Count);
        foreach (var (key, value) in raw)
        {
            if (ushort.TryParse(key, out var species))
                result[species] = value;
        }
        return result;
    }
}

public sealed class ExpansionSpeciesEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("baseHP")] public int BaseHP { get; set; }
    [JsonPropertyName("baseAtk")] public int BaseAtk { get; set; }
    [JsonPropertyName("baseDef")] public int BaseDef { get; set; }
    [JsonPropertyName("baseSpe")] public int BaseSpe { get; set; }
    [JsonPropertyName("baseSpA")] public int BaseSpA { get; set; }
    [JsonPropertyName("baseSpD")] public int BaseSpD { get; set; }
    [JsonPropertyName("type1")] public int Type1 { get; set; }
    [JsonPropertyName("type2")] public int Type2 { get; set; }
    [JsonPropertyName("catchRate")] public int CatchRate { get; set; }
    [JsonPropertyName("baseExp")] public int BaseExp { get; set; }
    [JsonPropertyName("genderRatio")] public int GenderRatio { get; set; }
    [JsonPropertyName("eggCycles")] public int EggCycles { get; set; }
    [JsonPropertyName("growthRate")] public int GrowthRate { get; set; }
    [JsonPropertyName("eggGroup1")] public int EggGroup1 { get; set; }
    [JsonPropertyName("eggGroup2")] public int EggGroup2 { get; set; }
    [JsonPropertyName("ability1")] public int Ability1 { get; set; }
    [JsonPropertyName("ability2")] public int Ability2 { get; set; }
    [JsonPropertyName("abilityHidden")] public int AbilityHidden { get; set; }
    /// <summary>Not yet reliable - see pokeemerald-expansion-support memory: source enum has
    /// conditional #if blocks the extractor doesn't evaluate. Needed for Phase 2 (dex tracking), not before.</summary>
    [JsonPropertyName("natDexNum")] public int NatDexNum { get; set; }
    /// <summary>Level-up + egg + teachable-candidate + universal moves, across pre-evolutions (see extractor).</summary>
    [JsonPropertyName("learnableMoves")] public ushort[] LearnableMoves { get; set; } = [];
}
