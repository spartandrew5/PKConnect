using System.Buffers.Binary;
using System.Reflection;
using PKHeX.Core;

namespace PKConnect.Core.formats;

/// <summary>
/// The species past #493 that the Platinum Connect build adds (at their real National Dex numbers),
/// and the rules for moving them in and out of a Platinum save.
/// </summary>
/// <remarks>
/// The save's byte layout is vanilla, so PKHeX reads it as a normal <see cref="SAV4Pt"/>; only the
/// slots holding an added species need <see cref="ExpansionPK4"/> (PKHeX's Gen 4 table has no stats
/// for them). Main storage never holds that type: an added species is stored as an official PK5 and
/// converted at the save boundary, the same way pokeemerald-expansion saves are fed (see
/// <see cref="ExpansionTravel"/>).
///
/// The table is the game's own data: <c>platinum_added_species.bin</c> holds the 44-byte
/// <c>pl_personal.narc</c> entry of every added species, taken from the decomp build the base patch
/// is made from. Regenerate it whenever that build gains species.
/// </remarks>
public static class Gen4AddedSpecies
{
    private const string ResourceName = "PKConnect.Core.formats.data.platinum_added_species.bin";
    private const int MaxVanillaSpecies = 493;
    private const int MaxVanillaMove = 467;

    /// <summary>The build's last move: it has every Gen 5 move (Fusion Bolt, #559).</summary>
    public const int MaxMove = 559;
    private const int MaxItemPt = 467;
    private const int NatureCount = 25;
    private const int GameTypeMystery = 9;

    private static readonly Lazy<Dictionary<ushort, PersonalInfo4>> entries = new(Load);

    /// <summary>Every added species the build has, in Dex order.</summary>
    public static IReadOnlyList<ushort> Species => [.. entries.Value.Keys.Order()];

    public static bool Contains(ushort species) => species > MaxVanillaSpecies && entries.Value.ContainsKey(species);

    /// <summary>
    /// True when <paramref name="species"/> only reaches a game of <paramref name="context"/> as an added
    /// species: there's no variant of that context to create, its Gen 5 variant is used instead.
    /// </summary>
    public static bool IsBridged(ushort species, EntityContext context)
        => context == EntityContext.Gen4 && Contains(species);

    /// <summary>Whether <paramref name="save"/> belongs to a game built with the added species.</summary>
    public static bool SupportedBy(SaveFile save) => save is SAV4Pt or SAV4HGSS;

    /// <summary>
    /// The last move <paramref name="save"/>'s game has: every supported build has all of Gen 5's moves.
    /// (Kept per save so a build that lags behind can be given a lower number again.)
    /// </summary>
    public static int MaxMoveFor(SaveFile save) => MaxMove;

    /// <summary>The game's own entry for an added species; PKHeX's table for everything else.</summary>
    public static PersonalInfo4 GetPersonalInfo(ushort species, byte form)
        => entries.Value.TryGetValue(species, out var info) ? info : PersonalTable.HGSS.GetFormEntry(species, form);

    /// <summary>Re-reads a PK4 holding an added species as <see cref="ExpansionPK4"/>; anything else is returned as is.</summary>
    public static PKM Upgrade(PKM pk)
        => pk is PK4 pk4 && IsAddedContent(pk4) ? new ExpansionPK4(pk4.Data.ToArray()) : pk;

    /// <summary>
    /// True when the Pokémon holds something PKHeX's Gen 4 doesn't know: an added species, or one of the
    /// build's Gen 5 moves (which any species can know).
    /// </summary>
    public static bool IsAddedContent(PKM pk)
        => Contains(pk.Species) || IsAddedMove(pk.Move1) || IsAddedMove(pk.Move2) || IsAddedMove(pk.Move3) || IsAddedMove(pk.Move4);

    public static bool IsAddedMove(ushort move) => move is > MaxVanillaMove and <= MaxMove;

    /// <summary>Whether <paramref name="species"/> can be in the build at all: Gen 4's own, or an added one.</summary>
    public static bool IsInBuild(ushort species) => species is > 0 and <= MaxVanillaSpecies || Contains(species);

    /// <summary>The type PKHeX's save writers accept: PK4 for an <see cref="ExpansionPK4"/>, anything else as is.</summary>
    public static PKM Downgrade(PKM pk, SaveFile save)
    {
        if (pk is not ExpansionPK4 added)
            return pk;
        added = WithoutMovesPast(added, MaxMoveFor(save));
        // PKHeX would fill missing party stats from its own table (entry 0 for these species).
        if (added.Species != 0 && !added.PartyStatsPresent)
            added.ResetPartyStats();
        return added.ToPK4();
    }

    /// <summary>
    /// A copy that only knows moves up to <paramref name="maxMove"/> (the same Pokémon when it already
    /// does). A move the game has no data for would be a glitch move in it.
    /// </summary>
    private static ExpansionPK4 WithoutMovesPast(ExpansionPK4 pk, int maxMove)
    {
        if (pk.Move1 <= maxMove && pk.Move2 <= maxMove && pk.Move3 <= maxMove && pk.Move4 <= maxMove)
            return pk;

        var copy = new ExpansionPK4(pk.Data.ToArray());
        for (var i = 0; i < 4; i++)
        {
            if (copy.GetMove(i) > maxMove)
                copy.SetMove(i, 0);
        }
        copy.FixMoves();
        if (copy.Move1 == 0)
            copy.Move1 = (ushort)Move.Tackle; // it has to know something
        copy.HealPP();
        copy.RefreshChecksum();
        return copy;
    }

    /// <summary>
    /// Converts a stored PK5 into the save's format. Gen 4 derives the nature from the PID, so a
    /// Gen 5 native whose stored nature differs gets a new PID with the same nature, gender, shininess
    /// and ability slot.
    /// </summary>
    public static ExpansionPK4 FromPK5(PK5 src)
    {
        if (!IsInBuild(src.Species))
            throw new NotSupportedException($"Species {src.Species} isn't in the Platinum build");

        var pk = new ExpansionPK4
        {
            Species = src.Species,
            // Added species are base form only for now; Gen 4's own keep their form.
            Form = Contains(src.Species) ? (byte)0 : src.Form,
            ID32 = src.ID32,
            Language = src.Language,
            EXP = src.EXP,
            HeldItem = src.HeldItem <= MaxItemPt ? src.HeldItem : 0,
            Gender = src.Gender,
            OriginalTrainerGender = src.OriginalTrainerGender,
            OriginalTrainerFriendship = src.CurrentFriendship,
            MarkingValue = src.MarkingValue,
            // Arrives the way a link trade would: Platinum can't show a Gen 5 origin game or location.
            Version = GameVersion.Pt,
            Ball = src.Ball <= (byte)Ball.Cherish ? src.Ball : (byte)Ball.Poke,
            MetLevel = src.CurrentLevel,
            MetLocation = Locations.LinkTrade4,
            MetDate = src.MetDate ?? EncounterDate.GetDateNDS(),
            PokerusState = src.PokerusState,
            FatefulEncounter = src.FatefulEncounter,
        };
        pk.OriginalTrainerName = src.OriginalTrainerName;

        ExpansionConverter.CopyMovesIVsEVs(src, pk, MaxMove);
        src.CopyContestStatsTo(pk);
        src.CopyRibbonSetCommon3(pk);
        src.CopyRibbonSetEvent3(pk);
        src.CopyRibbonSetCommon4(pk);
        src.CopyRibbonSetEvent4(pk);

        var info = pk.PersonalInfo;
        var twoAbilities = info.Ability2 != 0 && info.Ability2 != info.Ability1;
        var abilityBit = src.HiddenAbility ? 0 : (int)(src.PIDAbility & 1);
        pk.PID = MatchPid(src, info, twoAbilities ? abilityBit : null);
        pk.Ability = twoAbilities && (pk.PID & 1) == 1 ? info.Ability2 : info.Ability1;

        pk.IsNicknamed = src.IsNicknamed;
        pk.Nickname = src.IsNicknamed
            ? src.Nickname
            : SpeciesName.GetSpeciesNameGeneration(src.Species, src.Language, 4);

        pk.ResetPartyStats();
        pk.RefreshChecksum();
        return pk;
    }

    /// <summary>Converts a save's added species into the PK5 main storage keeps (PKHeX's own Gen 4 to 5 transfer).</summary>
    public static PK5 ToPK5(ExpansionPK4 pk)
    {
        if (!IsInBuild(pk.Species))
            throw new NotSupportedException($"Species {pk.Species} isn't in the Platinum build");

        var result = pk.ToPK4().ConvertToPK5();
        result.OriginalTrainerFriendship = pk.CurrentFriendship;
        // By slot, from Gen 5's own table.
        result.RefreshAbility((int)(pk.PID & 1));
        if (!pk.IsNicknamed)
            result.Nickname = SpeciesName.GetSpeciesNameGeneration(pk.Species, pk.Language, 5);

        result.ResetPartyStats();
        result.RefreshChecksum();
        return result;
    }

    /// <summary>The source's own PID when Gen 4 would read the same Pokémon from it, otherwise a matching one derived from it.</summary>
    private static uint MatchPid(PK5 src, PersonalInfo4 info, int? abilityBit)
    {
        var nature = (uint)src.Nature % NatureCount;
        var shiny = src.IsShiny;
        var idXor = (uint)(src.TID16 ^ src.SID16);

        bool Matches(uint pid)
            => pid % NatureCount == nature
                && (!info.IsDualGender || EntityGender.GetFromPIDAndRatio(pid, info.Gender) == src.Gender)
                && (abilityBit is null || (pid & 1) == abilityBit)
                && ((idXor ^ (pid >> 16) ^ (pid & 0xFFFF)) < 8) == shiny;

        var pid = src.PID;
        // Seeded by the source so the same Pokémon is the same Gen 4 Pokémon on every visit.
        var rnd = new Random(unchecked((int)src.PID));
        while (!Matches(pid))
        {
            var low = (uint)rnd.Next(0x10000);
            // A shiny PID is built rather than searched for: the high half is fixed by the low half and the trainer id.
            var high = shiny ? idXor ^ low ^ (uint)rnd.Next(8) : (uint)rnd.Next(0x10000);
            pid = (high << 16) | low;
        }
        return pid;
    }

    private static Dictionary<ushort, PersonalInfo4> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {ResourceName}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var data = buffer.ToArray();

        // [u16 species][44-byte personal entry] per species.
        const int recordSize = 2 + PersonalInfo4.SIZE;
        Dictionary<ushort, PersonalInfo4> result = [];
        for (var offset = 0; offset + recordSize <= data.Length; offset += recordSize)
        {
            var species = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
            // The game numbers its types with the unused "???" at 9; PKHeX's tables don't have it.
            foreach (var typeOffset in (int[])[offset + 2 + 6, offset + 2 + 7])
            {
                if (data[typeOffset] > GameTypeMystery)
                    data[typeOffset]--;
            }
            result[species] = new PersonalInfo4(data.AsMemory(offset + 2, PersonalInfo4.SIZE));
        }
        return result;
    }
}
