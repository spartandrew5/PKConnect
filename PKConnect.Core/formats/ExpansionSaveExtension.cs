using PKHeX.Core;
using PKVault.Core;

namespace PKConnect.Core.formats;

/// <summary>
/// A pokeemerald-expansion save (see <see cref="ExpansionSaveDetector"/>). PKHeX loads it as a
/// Gen 3 save and its slot offsets are right, but the Pokémon in the slots are the expansion's
/// own format, which PKHeX's sealed PK3 path would misread. So slots are read as raw bytes at
/// the save's own offsets and decoded through <see cref="ExpansionPK3"/>, and written the same way.
///
/// Not handled yet: the expansion's Pokédex flags, which live elsewhere than the vanilla ones -
/// the counts PKHeX gives for such a save are not the game's, and nothing is marked seen or caught.
/// </summary>
public sealed class ExpansionSaveExtension(SAV3 save, string gameId) : SaveExtension
{
    /// <summary>Which expansion build wrote the save: "emeraldexpansion", "fireredexpansion" or "leafgreenexpansion".</summary>
    public string GameId => gameId;

    public static SaveExtension? For(SaveFile save) =>
        save is SAV3 sav3 && ExpansionSaveDetector.DetectGameId(save, save.Metadata.FilePath) is { } id
            ? new ExpansionSaveExtension(sav3, id)
            : null;

    public override SaveExtension CloneFor(SaveFile clone) => new ExpansionSaveExtension((SAV3)clone, gameId);

    public override PKM BlankPKM => new ExpansionPK3();

    public override IReadOnlyList<PKM> GetPartyData() => ReadParty();

    public override IReadOnlyList<PKM> GetBoxData(int box) => ReadBoxes(box);

    public override IReadOnlyList<PKM> GetAllPKM() => [.. ReadBoxes().Concat(ReadParty()).Where(pk => pk.Species != 0)];

    // SAV3's party and box offsets are relative to its reassembled blocks (LargeBlock.PartyBuffer,
    // Storage), not to the sector-rotated file.
    private List<ExpansionPK3> ReadParty()
    {
        List<ExpansionPK3> result = [];
        var buffer = save.LargeBlock.PartyBuffer;
        for (var slot = 0; slot < save.PartyCount; slot++)
            result.Add(new ExpansionPK3(buffer.Slice(save.GetPartyOffset(slot), ExpansionPK3.Size3Party).ToArray()));
        return result;
    }

    private List<ExpansionPK3> ReadBoxes(int? onlyBox = null)
    {
        List<ExpansionPK3> result = [];
        var storage = save.Storage;
        IEnumerable<int> boxes = onlyBox is { } box ? [box] : Enumerable.Range(0, save.BoxCount);
        foreach (var b in boxes)
            for (var slot = 0; slot < save.BoxSlotCount; slot++)
                result.Add(new ExpansionPK3(storage.Slice(save.GetBoxSlotOffset(b, slot), ExpansionPK3.Size3Stored).ToArray()));
        return result;
    }

    // PKHeX's writers take nothing but PK3, so slots are written straight into the blocks, the
    // way SaveFile.SetPartySlotAtIndex and SAV3.SetPartyValues do.
    public override bool SetPartySlot(PKM pkm, int index, EntityImportSettings settings)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)index, 5u);
        var pk = Expect(pkm);

        var count = save.PartyCount;
        if (pk.Species != 0)
        {
            if (count <= index)
                save.LargeBlock.PartyCount = (byte)(index + 1);
            if (!pk.PartyStatsPresent)
                pk.ResetPartyStats();
            if (!SAV3.IsMail(pk.HeldItem))
                pk.HeldMailID = -1;
            pk.RefreshChecksum();
        }
        else if (count > index)
        {
            save.LargeBlock.PartyCount = (byte)index;
        }
        pk.WriteEncryptedDataParty(save.LargeBlock.PartyBuffer[save.GetPartyOffset(index)..]);
        return true;
    }

    public override bool SetBoxSlot(PKM pkm, int box, int slot, EntityImportSettings settings) =>
        WriteBoxSlot(pkm, save.GetBoxSlotOffset(box, slot));

    public override bool SetBoxSlot(PKM pkm, int index, EntityImportSettings settings) =>
        WriteBoxSlot(pkm, save.GetBoxSlotOffset(index));

    private bool WriteBoxSlot(PKM pkm, int offset)
    {
        var pk = Expect(pkm);
        if (pk.Species != 0)
            pk.RefreshChecksum();
        pk.WriteEncryptedDataStored(save.Storage[offset..]);
        return true;
    }

    // Main storage never holds the expansion format: a Pokémon is stored as an official PK3 / PK4 /
    // PK5 variant and becomes an ExpansionPK3 only on its way into the save.
    public override PKM? Adopt(PKM pk, Func<Type, PKM> convertTo) =>
        pk is ExpansionPK3 || pk.Species == 0 ? null : convertTo(typeof(ExpansionPK3));

    public override PKM GetDaycareSlot(int index) =>
        new ExpansionPK3(save.GetDaycareSlot(index).Span[..ExpansionPK3.Size3Stored].ToArray());

    // The other slots (FRLG's Route 5 daycare) are read through vanilla PK3 and would misdecode.
    public override bool ReadsExtraSlots => false;

    public override bool? IsPresentInGame(ushort species, byte form) =>
        ExpansionSpeciesTable.TryGet(ExpansionSpeciesTable.GetRawSpecies(species), out _);

    public override bool HasVanillaPokedex => false;

    /// <summary>Contexts the save can take a variant from (PID semantics shared with Gen 3).</summary>
    private static bool IsBridge(EntityContext context) => context is EntityContext.Gen3 or EntityContext.Gen4 or EntityContext.Gen5;

    // The main variant first when it is Gen 3/4/5 (it keeps its original data), then the newest
    // bridge format there is - Gen 5 holds Gen 5 species and moves.
    public override bool TryFindVariant(IReadOnlyList<PkmVariantEntity> variants, out PkmVariantEntity? variant)
    {
        variant = variants.FirstOrDefault(candidate => candidate.IsMain && IsBridge(candidate.Context))
            ?? variants.FirstOrDefault(candidate => candidate.Context == EntityContext.Gen5)
            ?? variants.FirstOrDefault(candidate => candidate.Context == EntityContext.Gen4)
            ?? variants.FirstOrDefault(candidate => candidate.Context == EntityContext.Gen3);
        return true;
    }

    // Gen 4 when the species exists there, Gen 5 for the ones Gen 4 doesn't have.
    public override EntityContext? ContextToCreate(ushort species) => ExpansionConverter.BridgeContextFor(species);

    public override bool? CanEnter(EntityContext context, ushort species) => true;

    private static ExpansionPK3 Expect(PKM pkm) => pkm as ExpansionPK3
        ?? throw new ArgumentException($"Expected {nameof(ExpansionPK3)} for an expansion save, got {pkm.GetType().Name}");
}