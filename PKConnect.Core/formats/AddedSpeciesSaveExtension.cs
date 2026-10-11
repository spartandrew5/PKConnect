using PKHeX.Core;
using PKVault.Core;

namespace PKConnect.Core.formats;

/// <summary>
/// Platinum and HeartGold/SoulSilver saves, whose Connect builds have species and moves past
/// Gen 4's last (see <see cref="Gen4AddedSpecies"/>). The save itself is an ordinary Gen 4 save:
/// only a slot holding added content is read as an <see cref="ExpansionPK4"/>, which knows the
/// build's stats, and it is written back as plain PK4 bytes.
/// </summary>
public sealed class AddedSpeciesSaveExtension(SaveFile save) : SaveExtension
{
    public static SaveExtension? For(SaveFile save) => Gen4AddedSpecies.SupportedBy(save) ? new AddedSpeciesSaveExtension(save) : null;

    public override SaveExtension CloneFor(SaveFile clone) => new AddedSpeciesSaveExtension(clone);

    public override IReadOnlyList<PKM> GetPartyData() => [.. save.PartyData.Select(Gen4AddedSpecies.Upgrade)];

    public override IReadOnlyList<PKM> GetBoxData(int box) => [.. save.GetBoxData(box).Select(Gen4AddedSpecies.Upgrade)];

    public override IReadOnlyList<PKM> GetAllPKM() => [.. save.GetAllPKM().Select(Gen4AddedSpecies.Upgrade)];

    public override bool SetPartySlot(PKM pk, int index, EntityImportSettings settings)
    {
        save.SetPartySlotAtIndex(Gen4AddedSpecies.Downgrade(pk, save), index, VanillaSettings(pk, settings));
        return true;
    }

    public override bool SetBoxSlot(PKM pk, int box, int slot, EntityImportSettings settings)
    {
        save.SetBoxSlotAtIndex(Gen4AddedSpecies.Downgrade(pk, save), box, slot, VanillaSettings(pk, settings));
        return true;
    }

    public override bool SetBoxSlot(PKM pk, int index, EntityImportSettings settings)
    {
        save.SetBoxSlotAtIndex(Gen4AddedSpecies.Downgrade(pk, save), index, VanillaSettings(pk, settings));
        return true;
    }

    // Main storage keeps the added species as PK5, and a slot PKHeX read as a plain PK4 (daycare)
    // needs the build's own stats table.
    public override PKM? Adopt(PKM pk, Func<Type, PKM> convertTo) => pk switch
    {
        // No-op unless it holds an added species or move.
        PK4 => Gen4AddedSpecies.Upgrade(pk) is var upgraded && !ReferenceEquals(upgraded, pk) ? upgraded : null,
        // Straight from Gen 5, so it keeps its Gen 5 moves.
        PK5 when Gen4AddedSpecies.IsInBuild(pk.Species) => convertTo(typeof(ExpansionPK4)),
        _ => null,
    };

    public override bool? IsPresentInGame(ushort species, byte form) => Gen4AddedSpecies.Contains(species) ? true : null;

    // An added species keeps its own type: the save's vanilla one has no data for it.
    public override Type? TypeToWrite(PKM pk) => pk is ExpansionPK4 ? typeof(ExpansionPK4) : null;

    public override bool TryFindVariant(IReadOnlyList<PkmVariantEntity> variants, out PkmVariantEntity? variant)
    {
        variant = null;
        if (variants.Count == 0)
            return false;

        // A species the build adds only exists as a Gen 5 variant.
        if (Gen4AddedSpecies.Contains(variants[0].Species))
        {
            variant = variants.FirstOrDefault(candidate => candidate.Context == EntityContext.Gen5);
            return true;
        }

        // The build also has Gen 5's moves, so a Gen 5 Pokémon goes in as it is and keeps them,
        // where a Gen 4 variant would have dropped them.
        variant = variants.FirstOrDefault(candidate => candidate.IsMain && candidate.Context == EntityContext.Gen5);
        return variant is not null;
    }

    public override EntityContext? ContextToCreate(ushort species) => Gen4AddedSpecies.Contains(species) ? EntityContext.Gen5 : null;

    public override bool? CanEnter(EntityContext context, ushort species) =>
        context == EntityContext.Gen5 && Gen4AddedSpecies.IsInBuild(species) ? true : null;

    // The vanilla Pokédex has no room for the added species, so the game doesn't track them either.
    private static EntityImportSettings VanillaSettings(PKM pk, EntityImportSettings settings)
        => pk is ExpansionPK4 ? settings with { UpdatePokeDex = EntityImportOption.Disable } : settings;
}