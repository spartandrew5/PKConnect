using PKHeX.Core;
using PKVault.Core;

namespace PKConnect.Core.formats;

/// <summary>
/// How PKVault's conversion and display treat <see cref="ExpansionPK3"/>. The format is bridged
/// through the official PK3 / PK4 / PK5 (same PID semantics as Gen 3) and PKVault's regular chain
/// takes over for any other generation; main storage keeps such a Pokémon as PK4, or PK5 when it
/// is a Gen 5 species or knows a Gen 5 move.
/// </summary>
public sealed class ExpansionFormat : PkmFormatExtension
{
    private static int Weight<T>() => PkmConvertService.GetPKMTypeWeight(typeof(T));

    public override bool Handles(Type pkmType) => pkmType == typeof(ExpansionPK3);

    public override int TypeWeight(Type pkmType) => Weight<PK3>();

    // PK5 is the bridge whenever Gen 5+ is on the other side, so Gen 5 species and moves don't get
    // squeezed through PK4.
    public override PKM ToOfficial(PKM pk, Type targetType)
    {
        var target = PkmConvertService.GetPKMTypeWeight(targetType);
        return ExpansionConverter.ToOfficial((ExpansionPK3)pk, preferGen3: target <= Weight<PK3>(), preferGen5: target >= Weight<PK5>());
    }

    public override PKM FromOfficial(PKM current, Type targetType, Func<PKM, Type, PKM> convert)
    {
        if (current is PK3 or PK4 or PK5)
            return ExpansionConverter.FromOfficial(current);

        var weight = PkmConvertService.GetPKMTypeWeight(current.GetType());
        var bridgeType = weight < Weight<PK4>() ? typeof(PK3)
            : weight > Weight<PK5>() ? typeof(PK5)
            : typeof(PK4);
        return ExpansionConverter.FromOfficial(convert(current, bridgeType));
    }

    public override bool HasSpecies(ushort species) => ExpansionSpeciesTable.TryGet(ExpansionSpeciesTable.GetRawSpecies(species), out _);

    public override Type StorageType(PKM pk) => ExpansionConverter.StorageType((ExpansionPK3)pk);

    public override bool CanBeStored(PKM pk) => ExpansionConverter.CanConvertToOfficial((ExpansionPK3)pk);

    // Evolving runs PKHeX's Gen 3 evolution and legality path, which doesn't know the format.
    public override bool CanEvolveInSave(PKM pk) => false;

    // PKHeX's Gen 3 base class seals the ability to a single bit, which can't say "hidden".
    public override int? Ability(PKM pk) => pk is ExpansionPK3 { HiddenAbilityIndex: 2 } expansion
        ? ExpansionSpeciesTable.GetHiddenAbility(expansion.SpeciesInternal)
        : null;

    // The nature its stats use (mint modifier applied) - for a traveled Gen 5+ Pokémon, its real one.
    public override Nature? Nature(PKM pk) => ((ExpansionPK3)pk).StatAlignment;

    public override bool? IsNicknamed(PKM pk) => ExpansionConverter.IsNicknamed((ExpansionPK3)pk);

    public override int? MaxNicknameLength(PKM pk) => ExpansionPK3.NicknameMaxLength;

    // PKHeX's legality only knows Gen 3 learnsets for this context; the decomp's own are used instead.
    public override IReadOnlyList<ushort>? LearnableMoves(PKM pk) => ExpansionSpeciesTable.GetLearnableMoves(((ExpansionPK3)pk).SpeciesInternal);

    public override int? ModernHeldItem(PKM pk) => ((ExpansionPK3)pk).HeldItemModern;

    // The expansion uses modern mixed-case species names ("Torterra") as the un-nicknamed default,
    // where Gen 3's own would be upper-case ("TORTERRA").
    public override bool ApplyNickname(PKM pk, string nickname, bool isNicknamed)
    {
        var expansion = (ExpansionPK3)pk;
        var trimmed = isNicknamed ? nickname.Trim() : "";
        if (trimmed.Length > ExpansionPK3.NicknameMaxLength)
            trimmed = trimmed[..ExpansionPK3.NicknameMaxLength];
        expansion.Nickname = trimmed.Length == 0
            ? SpeciesName.GetSpeciesNameGeneration(expansion.Species, expansion.Language, 9)
            : trimmed;
        return true;
    }
}