using PKHeX.Core;

namespace PKVault.Core;

/// <summary>
/// Lets another assembly take over parts of how one kind of save is read and written, for saves
/// PKHeX reads but doesn't fully understand (a ROM hack's own Pokémon format inside an otherwise
/// ordinary save, for instance). A <see cref="SaveWrapper"/> asks its extension first and falls
/// back to PKHeX whenever a member answers null or false.
///
/// An extension belongs to one <see cref="SaveFile"/> instance; a factory registered with
/// <see cref="Register"/> decides which saves get one.
/// </summary>
public abstract class SaveExtension
{
    private static readonly List<Func<SaveFile, SaveExtension?>> Factories = [];

    /// <summary>Adds a factory; it returns null for the saves it has nothing to say about.</summary>
    public static void Register(Func<SaveFile, SaveExtension?> factory)
    {
        lock (Factories)
            Factories.Add(factory);
    }

    /// <summary>The first registered extension that takes this save, if any.</summary>
    public static SaveExtension? For(SaveFile save)
    {
        lock (Factories)
        {
            foreach (var factory in Factories)
                if (factory(save) is { } extension)
                    return extension;
        }
        return null;
    }

    /// <summary>The same extension for a clone of its save (see <see cref="SaveWrapper.Clone"/>).</summary>
    public abstract SaveExtension CloneFor(SaveFile clone);

    /// <summary>The party's Pokémon, or null to let PKHeX read them.</summary>
    public virtual IReadOnlyList<PKM>? GetPartyData() => null;

    /// <summary>One box's slots (empty ones included), or null to let PKHeX read them.</summary>
    public virtual IReadOnlyList<PKM>? GetBoxData(int box) => null;

    /// <summary>Every Pokémon in the save's boxes and party, or null to let PKHeX read them.</summary>
    public virtual IReadOnlyList<PKM>? GetAllPKM() => null;

    /// <summary>Writes a party slot; false to let PKHeX write it.</summary>
    public virtual bool SetPartySlot(PKM pk, int index, EntityImportSettings settings) => false;

    /// <summary>Writes a box slot; false to let PKHeX write it.</summary>
    public virtual bool SetBoxSlot(PKM pk, int box, int slot, EntityImportSettings settings) => false;

    /// <summary>Writes a box slot by its index across all boxes; false to let PKHeX write it.</summary>
    public virtual bool SetBoxSlot(PKM pk, int index, EntityImportSettings settings) => false;

    /// <summary>An empty Pokémon of the type this save's slots hold, or null for PKHeX's.</summary>
    public virtual PKM? BlankPKM => null;

    /// <summary>The Pokédex counts, or null for PKHeX's.</summary>
    public virtual int? SeenCount => null;
    public virtual int? CaughtCount => null;

    /// <summary>Whether the save's game has the species and form; null for what PKHeX's table of the game says.</summary>
    public virtual bool? IsPresentInGame(ushort species, byte form) => null;

    /// <summary>
    /// A Pokémon read from the save, or about to go into it, in the form the save keeps it; null
    /// when it is fine as it is. <paramref name="convertTo"/> is the regular conversion.
    /// </summary>
    public virtual PKM? Adopt(PKM pk, Func<Type, PKM> convertTo) => null;

    /// <summary>The type to write this Pokémon as, or null for the type of the save's blank one.</summary>
    public virtual Type? TypeToWrite(PKM pk) => null;

    /// <summary>A daycare slot's Pokémon, or null to let PKHeX read it.</summary>
    public virtual PKM? GetDaycareSlot(int index) => null;

    /// <summary>False when the save's other slots (see <see cref="SaveFile.GetExtraSlots"/>) can't be read by PKHeX.</summary>
    public virtual bool ReadsExtraSlots => true;

    /// <summary>
    /// Added to what a save's id is made from, for saves that would otherwise share the id of
    /// another game's save of the same trainer; null changes nothing.
    /// </summary>
    public virtual string? IdSalt => null;

    /// <summary>The save's Pokédex reader and writer, or null for the one of the save's generation.</summary>
    public virtual DexGenService? CreateDexService() => null;

    /// <summary>
    /// The game the save plays as, when it isn't what PKHeX reports (a ROM hack built on another
    /// game's save structure, or paired versions PKHeX can't tell apart); null otherwise.
    /// </summary>
    public virtual GameVersion? PlayedVersion => null;

    /// <summary>False when the save's Pokédex isn't where PKHeX expects, so its flags are left alone.</summary>
    public virtual bool HasVanillaPokedex => true;

    /// <summary>
    /// Picks which of a stored Pokémon's variants goes into the save; false keeps the regular rule
    /// (the variant of the save's own context). True with a null variant means one has to be
    /// created first, see <see cref="ContextToCreate"/>.
    /// </summary>
    public virtual bool TryFindVariant(IReadOnlyList<PkmVariantEntity> variants, out PkmVariantEntity? variant)
    {
        variant = null;
        return false;
    }

    /// <summary>The context of the variant to create so the species can enter the save; null for the save's own.</summary>
    public virtual EntityContext? ContextToCreate(ushort species) => null;

    /// <summary>Whether a variant of that context can be written into the save; null keeps the regular rule (same context).</summary>
    public virtual bool? CanEnter(EntityContext context, ushort species) => null;
}