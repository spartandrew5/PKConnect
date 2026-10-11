using PKHeX.Core;

namespace PKConnect.Core.formats;

/// <summary>
/// Converts between <see cref="ExpansionPK3"/> and official Gen 3/4/5 entities. Gen 3-5 share PID
/// semantics (gender, shininess and ability slot derive from the PID), so these are field copies with
/// no re-rolling; Gen 5's separately stored nature rides on the expansion's mint modifier. Other
/// generations are reached through PKVault's own converter chain via PK3/PK4/PK5 (see PkmConvertService).
/// </summary>
public static class ExpansionConverter
{
    private const int MaxSpecies3 = 386;
    private const int MaxSpecies4 = 493;
    private const int MaxSpecies5 = 649;
    private const int MaxMove3 = 354;
    private const int MaxMove4 = 467;
    private const int MaxMove5 = 559;
    private const int MaxItem4 = 536;
    private const int MaxItem5 = 638;
    private const byte MaxBall4 = (byte)Ball.Cherish;
    private const byte MaxBall5 = (byte)Ball.Dream;
    private const int NatureCount = 25;

    // pokeemerald's METLOC_IN_GAME_TRADE; shown as "met in a trade" for Pokémon from outside Gen 3.
    private const ushort MetLocationInGameTrade = 0xFE;

    /// <summary>Highest Dex number that can travel in and out of an expansion save (Gen 5's last, Genesect).</summary>
    public const int MaxOfficialSpecies = MaxSpecies5;

    /// <summary>Whether the Pokémon has an official format PKVault can store it as (Dex #1-649 for now).</summary>
    public static bool CanConvertToOfficial(ExpansionPK3 pk)
        => pk.Species is > 0 and <= MaxSpecies5 && !pk.IsEgg;

    /// <summary>Whether an official Pokémon can be represented in an expansion save.</summary>
    public static bool CanConvertFromOfficial(PKM pk)
        => pk.Species is > 0 and <= MaxSpecies5;

    /// <summary>
    /// True when only Gen 5's format can hold this Pokémon: a Gen 5 species, or a move Gen 4 doesn't have.
    /// </summary>
    public static bool NeedsGen5(PKM pk)
        => pk.Species > MaxSpecies4 || pk.Move1 > MaxMove4 || pk.Move2 > MaxMove4 || pk.Move3 > MaxMove4 || pk.Move4 > MaxMove4;

    /// <summary>The official format main storage keeps an expansion Pokémon as: PK4, or PK5 when it <see cref="NeedsGen5"/>.</summary>
    public static Type StorageType(ExpansionPK3 pk) => NeedsGen5(pk) ? typeof(PK5) : typeof(PK4);

    /// <summary>The variant context that can feed an expansion save for a species: Gen 4 up to #493, Gen 5 past it.</summary>
    public static EntityContext BridgeContextFor(ushort species)
        => species > MaxSpecies4 ? EntityContext.Gen5 : EntityContext.Gen4;

    /// <summary>
    /// True if the nickname differs from the species name. G3PKM's sealed IsNicknamed only compares
    /// against Gen 3's upper-case names ("TREECKO"), but the expansion uses modern names ("Treecko").
    /// </summary>
    public static bool IsNicknamed(ExpansionPK3 pk)
    {
        foreach (byte generation in (byte[])[3, 9])
        {
            var name = SpeciesName.GetSpeciesNameGeneration(pk.Species, pk.Language, generation);
            if (string.Equals(name, pk.Nickname, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    public static ExpansionPK3 FromOfficial(PKM src)
    {
        if (src is not (PK3 or PK4 or PK5))
            throw new ArgumentException($"Expected PK3, PK4 or PK5, got {src.GetType().Name}");
        if (!CanConvertFromOfficial(src))
            throw new NotSupportedException($"Species {src.Species} can't be converted to pokeemerald-expansion yet");

        var pk = new ExpansionPK3
        {
            PID = src.PID,
            ID32 = src.ID32,
            Language = GetGen3Language(src.Language),
            Species = src.Species,
            FlagHasSpecies = true,
            EXP = src.EXP,
            OriginalTrainerGender = src.OriginalTrainerGender,
            OriginalTrainerFriendship = src.CurrentFriendship,
            Ball = src.Ball,
            MetLevel = src.MetLevel,
            MetLocation = src is PK3 ? src.MetLocation : MetLocationInGameTrade,
            Version = (int)src.Version < 16 ? src.Version : GameVersion.E,
            PokerusStrain = src.PokerusStrain,
            PokerusDays = src.PokerusDays,
            FatefulEncounter = src.FatefulEncounter,
            HeldItemModern = src is PK3 ? GetModernItemFromGen3(src.HeldItem) : (ushort)src.HeldItem,
        };
        pk.OriginalTrainerName = src.OriginalTrainerName;

        CopyMovesIVsEVs(src, pk);
        if (src is IContestStatsReadOnly contest)
            contest.CopyContestStatsTo(pk);
        if (src is IRibbonSetCommon3 common3)
            common3.CopyRibbonSetCommon3(pk);
        if (src is IRibbonSetEvent3 event3)
            event3.CopyRibbonSetEvent3(pk);

        pk.HiddenAbilityIndex = src is PK5 pk5
            ? GetAbilitySlotGen5(pk5)
            : GetAbilitySlot(pk.SpeciesInternal, src.Ability, src.AbilityNumber);
        pk.TeraType = (MoveType)pk.PersonalInfo.Type1;
        // Gen 5 stores the nature apart from the PID. The expansion's mint modifier holds the
        // difference (effective nature = PID nature ^ modifier), so the real nature survives.
        var nature = (int)src.Nature;
        if (nature is >= 0 and < NatureCount)
            pk.StatAlignment = src.Nature;

        if (src.IsEgg)
            pk.IsEgg = true;
        else
            pk.Nickname = src.IsNicknamed ? src.Nickname : SpeciesName.GetSpeciesNameGeneration(src.Species, pk.Language, 9);

        pk.ResetPartyStats();
        pk.RefreshChecksum();
        return pk;
    }

    /// <summary>
    /// Converts to PK3 when <paramref name="preferGen3"/> and the species exists in Gen 3; to PK5 when
    /// <paramref name="preferGen5"/> or the Pokémon <see cref="NeedsGen5"/>; otherwise PK4.
    /// </summary>
    public static PKM ToOfficial(ExpansionPK3 pk, bool preferGen3, bool preferGen5 = false)
    {
        if (!CanConvertToOfficial(pk))
            throw new NotSupportedException($"Species {pk.Species} can't leave pokeemerald-expansion yet");
        if (preferGen3 && pk.Species <= MaxSpecies3) return ToPK3(pk);
        return preferGen5 || pk.Species > MaxSpecies4 ? ToPK5(pk) : ToPK4(pk);
    }

    private static PK3 ToPK3(ExpansionPK3 pk)
    {
        var result = new PK3
        {
            PID = pk.PID,
            ID32 = pk.ID32,
            Language = pk.Language,
            Species = pk.Species,
            EXP = pk.EXP,
            HeldItem = pk.HeldItem,
            OriginalTrainerGender = pk.OriginalTrainerGender,
            OriginalTrainerFriendship = pk.OriginalTrainerFriendship,
            Ball = pk.Ball <= (byte)Ball.Premier ? pk.Ball : (byte)Ball.Poke,
            MetLevel = pk.MetLevel,
            MetLocation = pk.MetLocation,
            Version = pk.Version,
            PokerusStrain = pk.PokerusStrain,
            PokerusDays = pk.PokerusDays,
            FatefulEncounter = pk.FatefulEncounter,
            AbilityBit = pk.HiddenAbilityIndex == 1,
        };
        result.OriginalTrainerName = pk.OriginalTrainerName;
        CopyMovesIVsEVs(pk, result, MaxMove3);
        CopyRibbonsAndContest(pk, result);
        SetNickname(pk, result, 3);

        result.ResetPartyStats();
        result.RefreshChecksum();
        return result;
    }

    private static PK4 ToPK4(ExpansionPK3 pk)
    {
        var modernItem = pk.HeldItemModern;
        var result = new PK4
        {
            PID = pk.PID,
            ID32 = pk.ID32,
            Language = pk.Language,
            Species = pk.Species,
            EXP = pk.EXP,
            HeldItem = modernItem <= MaxItem4 ? modernItem : 0,
            Gender = pk.Gender,
            Ability = GetAbility(pk),
            OriginalTrainerGender = pk.OriginalTrainerGender,
            OriginalTrainerFriendship = pk.OriginalTrainerFriendship,
            Ball = pk.Ball <= MaxBall4 ? pk.Ball : (byte)Ball.Poke,
            // Same met data PKHeX's PK3 -> PK4 conversion uses (a Pal Park transfer).
            MetLevel = pk.CurrentLevel,
            MetLocation = Locations.Transfer3,
            MetDate = EncounterDate.GetDateNDS(),
            Version = pk.Version,
            PokerusStrain = pk.PokerusStrain,
            PokerusDays = pk.PokerusDays,
            FatefulEncounter = pk.FatefulEncounter,
        };
        result.OriginalTrainerName = pk.OriginalTrainerName;
        CopyMovesIVsEVs(pk, result, MaxMove4);
        CopyRibbonsAndContest(pk, result);
        SetNickname(pk, result, 4);

        result.ResetPartyStats();
        result.RefreshChecksum();
        return result;
    }

    private static PK5 ToPK5(ExpansionPK3 pk)
    {
        var modernItem = pk.HeldItemModern;
        var result = new PK5
        {
            PID = pk.PID,
            ID32 = pk.ID32,
            Language = pk.Language,
            Species = pk.Species,
            EXP = pk.EXP,
            HeldItem = modernItem <= MaxItem5 ? modernItem : 0,
            Gender = pk.Gender,
            Ability = GetAbilityGen5(pk),
            HiddenAbility = pk.HiddenAbilityIndex == 2,
            // The nature its stats use (mints, or a Gen 5 native's own) - see ExpansionPK3.StatAlignment.
            Nature = pk.StatAlignment,
            OriginalTrainerGender = pk.OriginalTrainerGender,
            OriginalTrainerFriendship = pk.OriginalTrainerFriendship,
            Ball = pk.Ball <= MaxBall5 ? pk.Ball : (byte)Ball.Poke,
            // Same met data PKHeX's PK4 -> PK5 conversion uses (a Poké Transfer).
            MetLevel = pk.CurrentLevel,
            MetLocation = Locations.Transfer4,
            MetDate = EncounterDate.GetDateNDS(),
            Version = pk.Version,
            PokerusStrain = pk.PokerusStrain,
            PokerusDays = pk.PokerusDays,
            FatefulEncounter = pk.FatefulEncounter,
        };
        result.OriginalTrainerName = pk.OriginalTrainerName;
        CopyMovesIVsEVs(pk, result, MaxMove5);
        CopyRibbonsAndContest(pk, result);
        SetNickname(pk, result, 5);

        result.ResetPartyStats();
        result.RefreshChecksum();
        return result;
    }

    internal static void CopyMovesIVsEVs(PKM src, PKM dest, int maxMove = int.MaxValue)
    {
        dest.Move1 = src.Move1 <= maxMove ? src.Move1 : (ushort)0;
        dest.Move2 = src.Move2 <= maxMove ? src.Move2 : (ushort)0;
        dest.Move3 = src.Move3 <= maxMove ? src.Move3 : (ushort)0;
        dest.Move4 = src.Move4 <= maxMove ? src.Move4 : (ushort)0;
        dest.Move1_PPUps = src.Move1_PPUps;
        dest.Move2_PPUps = src.Move2_PPUps;
        dest.Move3_PPUps = src.Move3_PPUps;
        dest.Move4_PPUps = src.Move4_PPUps;
        dest.FixMoves();
        dest.HealPP();

        dest.IV_HP = src.IV_HP;
        dest.IV_ATK = src.IV_ATK;
        dest.IV_DEF = src.IV_DEF;
        dest.IV_SPE = src.IV_SPE;
        dest.IV_SPA = src.IV_SPA;
        dest.IV_SPD = src.IV_SPD;
        dest.EV_HP = src.EV_HP;
        dest.EV_ATK = src.EV_ATK;
        dest.EV_DEF = src.EV_DEF;
        dest.EV_SPE = src.EV_SPE;
        dest.EV_SPA = src.EV_SPA;
        dest.EV_SPD = src.EV_SPD;
    }

    private static void CopyRibbonsAndContest(ExpansionPK3 src, PKM dest)
    {
        if (dest is IContestStats contest)
            src.CopyContestStatsTo(contest);
        if (dest is IRibbonSetCommon3 common3)
            src.CopyRibbonSetCommon3(common3);
        if (dest is IRibbonSetEvent3 event3)
            src.CopyRibbonSetEvent3(event3);
    }

    private static void SetNickname(ExpansionPK3 src, PKM dest, byte generation)
    {
        if (src.IsEgg)
        {
            dest.IsEgg = true;
            return;
        }
        var nicknamed = IsNicknamed(src);
        dest.IsNicknamed = nicknamed;
        dest.Nickname = nicknamed
            ? src.Nickname
            : SpeciesName.GetSpeciesNameGeneration(src.Species, src.Language, generation);
    }

    /// <summary>Expansion ability slot (0/1 regular, 2 hidden) matching an official Pokémon's ability.</summary>
    private static int GetAbilitySlot(ushort rawSpecies, int ability, int abilityNumber)
    {
        if (TryGetAbilities(rawSpecies, out var abilities))
        {
            if (ability != 0 && ability == abilities.Hidden && ability != abilities.First)
                return 2;
            if (ability != 0 && ability == abilities.Second && ability != abilities.First)
                return 1;
            if (ability != 0 && ability == abilities.First)
                return 0;
        }
        return abilityNumber switch { 4 => 2, 2 => 1, _ => 0 };
    }

    private static int GetAbility(ExpansionPK3 pk)
    {
        if (!TryGetAbilities(pk.SpeciesInternal, out var abilities))
            return pk.Ability;
        return pk.HiddenAbilityIndex switch
        {
            2 when abilities.Hidden != 0 => abilities.Hidden,
            1 when abilities.Second != 0 => abilities.Second,
            _ => abilities.First,
        };
    }

    // Gen 5 <-> expansion goes by ability SLOT, not id: the expansion uses modern abilities, and some
    // changed after Gen 5 (Piplup's hidden ability was Defiant then, Competitive now), so matching ids
    // would hand a PK5 an ability Gen 5 never gave that species. The slot means the same in both.

    /// <summary>Expansion ability slot (0/1 regular, 2 hidden) of a Gen 5 Pokémon.</summary>
    private static int GetAbilitySlotGen5(PK5 pk)
    {
        if (pk.HiddenAbility)
            return 2;
        var official = PersonalTable.B2W2[pk.Species];
        return pk.Ability != official.Ability1 && pk.Ability == official.Ability2 ? 1 : 0;
    }

    /// <summary>The Gen 5 ability sitting in the expansion Pokémon's slot.</summary>
    private static int GetAbilityGen5(ExpansionPK3 pk)
    {
        var official = PersonalTable.B2W2[pk.Species];
        return pk.HiddenAbilityIndex switch
        {
            2 when official.AbilityH != 0 => official.AbilityH,
            1 when official.Ability2 != 0 => official.Ability2,
            _ => official.Ability1,
        };
    }

    /// <summary>
    /// A species' three ability slots from the expansion table - or, where that table has none
    /// (the extractor left Meowth, Gengar and Eevee without abilities and skipped a few species
    /// such as Unown entirely, so they lost their ability on the way out), from the official
    /// Gen 5 data for the same Dex number.
    /// </summary>
    private static bool TryGetAbilities(ushort rawSpecies, out (int First, int Second, int Hidden) abilities)
    {
        abilities = default;
        var inTable = ExpansionSpeciesTable.TryGet(rawSpecies, out var entry);
        if (inTable && entry.Ability1 != 0)
        {
            abilities = (entry.Ability1, entry.Ability2, entry.AbilityHidden);
            return true;
        }

        // Base species share their raw id with their Dex number, which covers the skipped ones.
        var natDex = inTable ? ExpansionSpeciesTable.GetNatDex(rawSpecies) : rawSpecies;
        if (natDex is 0 or > MaxSpecies5)
            return false;
        var official = PersonalTable.B2W2[natDex];
        if (official.Ability1 == 0)
            return false;
        abilities = (official.Ability1, official.Ability2 == official.Ability1 ? 0 : official.Ability2, official.AbilityH);
        return true;
    }

    private static ushort GetModernItemFromGen3(int gen3Item)
    {
        var modern = ItemConverter.GetItemFuture3((ushort)gen3Item);
        return ItemConverter.IsItemTransferable34(modern) ? modern : (ushort)0;
    }

    // Gen 3 has no Korean text; fall back to English like PKHeX does for other invalid languages.
    private static int GetGen3Language(int language)
        => language is >= 1 and <= 7 && language != 6 ? language : (int)LanguageID.English;
}
