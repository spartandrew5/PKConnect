using PKHeX.Core;
using PKVault.Core;

namespace PKConnect.Core.formats;

/// <summary>
/// Pokédex for a pokeemerald-expansion save. The generic Gen 3 path doesn't fit: it reads vanilla
/// PK3s (garbage here), stops at #386, uses the Gen 3 personal table and the dead vanilla dex
/// flags. Species run through <see cref="ExpansionDexFlags.MaxDexSpecies"/>, with personal data
/// from the expansion's own species table and flags from <see cref="ExpansionDexFlags"/>.
/// Forms aren't tracked (form 0 only).
/// </summary>
public class ExpansionDexService(ExpansionSaveExtension extension, SAV3 save) : DexGenService(save)
{
    private readonly ExpansionDexFlags? flags = extension.DexFlags;
    private readonly uint saveId = new SaveWrapper(save).Id;

    private static PersonalInfo3 Personal(ushort species) =>
        ExpansionSpeciesTable.GetPersonalInfo(ExpansionSpeciesTable.GetRawSpecies(species));

    public override Task<bool> UpdateDexWithSave(Dictionary<ushort, Dictionary<uint, DexItemDTO>> dex, StaticSpeciesData staticSpecies, HashSet<ushort>? speciesSet)
    {
        var pkmBySpecies = extension.GetAllPKM()
            .Where(pkm => !pkm.IsEgg)
            .GroupBy(pkm => pkm.Species)
            .ToDictionary(g => g.Key, g => g.ToList());

        for (ushort species = 1; species <= ExpansionDexFlags.MaxDexSpecies; species++)
        {
            if (speciesSet != null && !speciesSet.Contains(species))
            {
                continue;
            }

            var pkmList = pkmBySpecies.GetValueOrDefault(species) ?? [];
            var pi = Personal(species);
            List<Gender> genders = pi.OnlyMale ? [Gender.Male]
                : pi.OnlyFemale ? [Gender.Female]
                : pi.Genderless ? [Gender.Genderless]
                : [Gender.Male, Gender.Female];

            var forms = genders.Select(gender =>
            {
                var owned = pkmList.FindAll(pkm => (Gender)pkm.Gender == gender);
                return GetDexItemForm(species, owned.Count > 0, owned.Any(pkm => pkm.IsShiny), 0, gender) with
                {
                    Context = save.Context,
                    Generation = save.Generation,
                };
            }).ToList();

            if (!dex.TryGetValue(species, out var arr))
            {
                arr = [];
                dex.Add(species, arr);
            }
            arr[saveId] = new DexItemDTO(
                Id: $"{species}_{saveId}",
                Species: species,
                SaveId: saveId,
                Forms: forms,
                Languages: [GetSaveLanguage()]
            );
        }

        return Task.FromResult(true);
    }

    protected override DexItemForm GetDexItemForm(ushort species, bool isOwned, bool isOwnedShiny, byte form, Gender gender)
    {
        var pi = Personal(species);

        var isCaught = isOwned || (flags?.GetCaught(species) ?? false);
        var isSeen = isCaught || (flags?.GetSeen(species) ?? false);

        return new DexItemForm(
            Id: DexLoader.GetId(species, form, gender),
            Species: species,
            Form: form,
            Gender: gender,
            Types: GetTypes(pi),
            Abilities: GetAbilities(pi),
            AbilityHidden: ExpansionSpeciesTable.GetHiddenAbility(ExpansionSpeciesTable.GetRawSpecies(species)),
            BaseStats: GetBaseStats(pi),
            IsSeen: isSeen,
            IsSeenShiny: isOwnedShiny,
            IsSeenAlpha: false,
            IsCaught: isCaught,
            IsOwned: isOwned,
            IsOwnedShiny: isOwnedShiny
        );
    }

    protected override IEnumerable<LanguageID> GetDexLanguages(ushort species) => [];

    /// <summary>Only written when the flag offsets came from the build's symbols (see <see cref="ExpansionDexFlags"/>).</summary>
    public override Task EnableSpeciesForm(EnableSpeciesFormPayload payload)
    {
        if (flags is not { IsExact: true } || payload.Species is 0 or > ExpansionDexFlags.MaxDexSpecies)
        {
            return Task.CompletedTask;
        }

        if (payload.IsSeen || payload.IsCaught)
            flags.SetSeen(payload.Species);

        if (payload.IsCaught)
            flags.SetCaught(payload.Species);

        return Task.CompletedTask;
    }
}