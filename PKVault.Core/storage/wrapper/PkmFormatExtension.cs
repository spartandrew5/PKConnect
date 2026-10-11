using PKHeX.Core;

namespace PKVault.Core;

/// <summary>
/// Lets another assembly add a Pokémon format PKHeX doesn't have (a ROM hack's own structure, for
/// instance). Such a format has no conversion of its own to every generation: it names the official
/// formats it converts to and from, and the regular conversion chain does the rest. Main storage
/// only ever holds official formats.
/// </summary>
public abstract class PkmFormatExtension
{
    private static readonly List<PkmFormatExtension> Extensions = [];

    public static void Register(PkmFormatExtension extension)
    {
        lock (Extensions)
            Extensions.Add(extension);
    }

    /// <summary>The extension that added this Pokémon type, if one did.</summary>
    public static PkmFormatExtension? For(Type pkmType)
    {
        lock (Extensions)
        {
            foreach (var extension in Extensions)
                if (extension.Handles(pkmType))
                    return extension;
        }
        return null;
    }

    public static PkmFormatExtension? For(PKM pk) => For(pk.GetType());

    public abstract bool Handles(Type pkmType);

    /// <summary>Where the type sits among the official ones (see <see cref="PkmConvertService.GetPKMTypeWeight"/>).</summary>
    public abstract int TypeWeight(Type pkmType);

    /// <summary>This format's Pokémon as the official format that suits a conversion towards <paramref name="targetType"/>.</summary>
    public abstract PKM ToOfficial(PKM pk, Type targetType);

    /// <summary>
    /// A Pokémon of any other format as this one. <paramref name="convert"/> is the regular chain,
    /// to bring <paramref name="current"/> to an official format this one converts from.
    /// </summary>
    public abstract PKM FromOfficial(PKM current, Type targetType, Func<PKM, Type, PKM> convert);

    /// <summary>Whether the format has the species; PKHeX's species limit for the type doesn't say.</summary>
    public abstract bool HasSpecies(ushort species);

    /// <summary>The official type main storage keeps this Pokémon as.</summary>
    public abstract Type StorageType(PKM pk);

    /// <summary>False when the Pokémon holds something no official format can.</summary>
    public virtual bool CanBeStored(PKM pk) => true;

    /// <summary>Whether it can be evolved while it sits in its save.</summary>
    public virtual bool CanEvolveInSave(PKM pk) => true;

    // What PKHeX's base classes answer wrongly for the format; null keeps PKHeX's answer.
    public virtual int? Ability(PKM pk) => null;
    public virtual Nature? Nature(PKM pk) => null;
    public virtual bool? IsNicknamed(PKM pk) => null;
    public virtual int? MaxNicknameLength(PKM pk) => null;

    /// <summary>The moves the Pokémon can be taught, when PKHeX's legality doesn't know the format's learnsets; null otherwise.</summary>
    public virtual IReadOnlyList<ushort>? LearnableMoves(PKM pk) => null;

    /// <summary>The held item as its current-generation id, to compare with the stored official variant.</summary>
    public virtual int? ModernHeldItem(PKM pk) => null;

    /// <summary>Sets the nickname (or the species name when it has none); false to let the regular code do it.</summary>
    public virtual bool ApplyNickname(PKM pk, string nickname, bool isNicknamed) => false;
}