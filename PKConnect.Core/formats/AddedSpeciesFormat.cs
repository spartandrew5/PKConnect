using PKHeX.Core;
using PKVault.Core;

namespace PKConnect.Core.formats;

/// <summary>
/// How PKVault's conversion treats <see cref="ExpansionPK4"/>. The species a Connect build adds to
/// Gen 4 only exist from Gen 5 on, so PK5 is both their bridge and how main storage keeps them.
/// </summary>
public sealed class AddedSpeciesFormat : PkmFormatExtension
{
    public override bool Handles(Type pkmType) => pkmType == typeof(ExpansionPK4);

    public override int TypeWeight(Type pkmType) => PkmConvertService.GetPKMTypeWeight(typeof(PK4));

    public override PKM ToOfficial(PKM pk, Type targetType) => Gen4AddedSpecies.ToPK5((ExpansionPK4)pk);

    public override PKM FromOfficial(PKM current, Type targetType, Func<PKM, Type, PKM> convert) =>
        Gen4AddedSpecies.FromPK5(current as PK5 ?? (PK5)convert(current, typeof(PK5)));

    public override bool HasSpecies(ushort species) => Gen4AddedSpecies.IsInBuild(species);

    public override Type StorageType(PKM pk) => typeof(PK5);

    // Their trade evolutions are done on the stored Gen 5 variant, which has the evolution data,
    // and synchronized into the save.
    public override bool CanEvolveInSave(PKM pk) => false;
}