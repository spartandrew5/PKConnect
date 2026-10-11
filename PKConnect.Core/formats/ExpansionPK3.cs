using System;
using System.Numerics;
using PKHeX.Core;
using static System.Buffers.Binary.BinaryPrimitives;

namespace PKConnect.Core.formats;

/// <summary>
/// Generation 3 entity format for pokeemerald-expansion ROM hack saves.
/// </summary>
/// <remarks>
/// Vanilla Gen3's outer save container, sector/checksum layout, and the substruct
/// encryption/shuffle algorithm are all byte-identical and reused as-is (via
/// <see cref="PokeCrypto.DecryptIfEncrypted3"/>/<see cref="PokeCrypto.Encrypt3"/> - same as
/// upstream PKHeX's sealed <see cref="PK3"/>, which is why this class derives from <see cref="G3PKM"/>
/// directly instead: PK3 is sealed and can't be subclassed).
///
/// What changed is the BIT LAYOUT within substructs 0/1/3 (still 12 bytes each, same physical
/// offsets 0x20/0x2C/0x38/0x44 after decrypt+unshuffle) to make room for Tera type, Dynamax level,
/// Gigantamax factor, a Shadow flag, a widened 2-bit ability index (regular1/regular2/hidden), and
/// a 12th nickname character. Verified directly against the decomp's own
/// <c>include/pokemon.h</c> struct PokemonSubstruct0/1/3 definitions (2026-09-23) - see
/// pokeemerald-expansion-support memory for the full bit tables. Substruct2 (EVs/contest stats,
/// 0x38-0x43) is untouched.
///
/// Species IDs in this hack ARE National Dex numbers directly (no Gen3 internal remapping table
/// like vanilla PK3 uses via SpeciesConverter) - up to 1572, which is why base stats/types/abilities/
/// gender/growth-rate can't come from PKHeX's own Gen3 <see cref="PersonalTable"/> (real Gen3 species
/// only, ~411 entries) and instead come from <see cref="ExpansionSpeciesTable"/>.
///
/// Known limitations (read-only viewer, no legality checking, no cross-game transfer):
/// - <see cref="Gender"/> and <see cref="Ability"/> are sealed by <see cref="G3PKM"/> to PKHeX's own
///   species tables; for species beyond PKHeX's own known range they gracefully fall back
///   (Genderless / ability index 0) rather than throwing, but won't be accurate. Use
///   <see cref="HiddenAbilityIndex"/> for the real widened ability index this format actually stores.
/// - <see cref="NicknameTrash"/>/<see cref="TrashCharCountNickname"/> still model the vanilla 10-byte
///   outer trash region; characters 11-12 live bit-packed inside substruct0 and are handled by the
///   <see cref="Nickname"/> override directly, not through the generic trash-span path.
/// - <see cref="HeldItem"/> is the raw 10-bit in-ROM item id (no ItemConverter translation applied).
/// </remarks>
public sealed class ExpansionPK3 : G3PKM, ISanityChecksum, ITeraTypeReadOnly, IDynamaxLevelReadOnly, IGigantamaxReadOnly, IShadowCapture
{
    public override ReadOnlySpan<ushort> ExtraBytes => [0x2A, 0x2B];

    // Gen3 PKM stored/party sizes have been fixed since the format's inception; hardcoded here rather
    // than referencing PokeCrypto's constant (whose exact name varies across PKHeX.Core builds).
    // Internal (not private) so SaveWrapper can slice raw save bytes at these lengths without an instance.
    internal const int Size3Stored = 80;
    internal const int Size3Party = 100;

    public override int SIZE_PARTY => Size3Party;
    public override int SIZE_STORED => Size3Stored;
    public override EntityContext Context => EntityContext.Gen3;
    // Keyed by the raw expansion id so regional forms/Megas get their own stats and types.
    public override PersonalInfo3 PersonalInfo => ExpansionSpeciesTable.GetPersonalInfo(SpeciesInternal);

    public ExpansionPK3() : base(Size3Party) { }
    public ExpansionPK3(Memory<byte> data) : base(DecryptParty(data)) { }

    protected override void EncryptStored(Span<byte> stored) => PokeCrypto.Encrypt3(stored);
    protected override void EncryptParty(Span<byte> party) { }

    private static Memory<byte> DecryptParty(Memory<byte> data)
    {
        PokeCrypto.DecryptIfEncrypted3(data.Span);
        if (data.Length >= Size3Party)
            return data;

        var result = new byte[Size3Party];
        data.Span.CopyTo(result);
        return result;
    }

    public override ExpansionPK3 Clone()
    {
        ExpansionPK3 pk = new();
        Data.CopyTo(pk.Data);
        return pk;
    }

    private const string EggNameJapanese = "タマゴ";

    /// <summary>The expansion's POKEMON_NAME_LENGTH (G3PKM seals MaxStringLengthNickname to vanilla's 10).</summary>
    public const int NicknameMaxLength = 12;

    // Trash Bytes - only the first 10 chars; chars 11-12 are bit-packed in substruct0 (see Nickname).
    public override Span<byte> NicknameTrash => Data.Slice(0x08, 10);
    public override Span<byte> OriginalTrainerTrash => Data.Slice(0x14, 7);
    public override int TrashCharCountTrainer => 7;
    public override int TrashCharCountNickname => 10;

    // 0x20 Intro (unchanged from vanilla)
    public override uint PID { get => ReadUInt32LittleEndian(Data); set => WriteUInt32LittleEndian(Data, value); }
    public override uint ID32 { get => ReadUInt32LittleEndian(Data[0x04..]); set => WriteUInt32LittleEndian(Data[0x04..], value); }
    public override ushort TID16 { get => ReadUInt16LittleEndian(Data[0x04..]); set => WriteUInt16LittleEndian(Data[0x04..], value); }
    public override ushort SID16 { get => ReadUInt16LittleEndian(Data[0x06..]); set => WriteUInt16LittleEndian(Data[0x06..], value); }

    /// <summary>12-char nickname: chars 1-10 in the standard outer trash region, chars 11-12
    /// bit-packed into substruct0 (nickname11 in the experience u32, nickname12 in the pokeball u16).</summary>
    public override string Nickname
    {
        get
        {
            var baseName = StringConverter3.GetString(NicknameTrash, Language);
            // Chars 11-12 only exist if the base 10-char region was entirely used (no terminator
            // hit before offset 10) - otherwise the name already ended and the two packed bytes are
            // leftover/uninitialized, not real characters.
            if (IsEgg || baseName.Length < 10)
                return baseName;
            // Mirrors the decomp's GetBoxMonData(MON_DATA_NICKNAME): both bytes 0 means a vanilla-origin
            // mon (no extra chars); otherwise read until the first EOS - bytes after it are leftover garbage.
            const byte EOS = 0xFF;
            var c11 = Nickname11Raw;
            var c12 = Nickname12Raw;
            if ((c11 == 0 && c12 == 0) || c11 == EOS)
                return baseName;
            var extra = c12 == EOS ? new[] { c11 } : new[] { c11, c12 };
            return baseName + StringConverter3.GetString(extra, Language);
        }
        set
        {
            // Encode all 12 chars (EOS-padded) then split: chars 1-10 into the trash region, 11-12 into
            // substruct0 - mirrors the decomp's SetBoxMonData(MON_DATA_NICKNAME).
            var toSet = IsEgg ? EggNameJapanese : value;
            Span<byte> encoded = stackalloc byte[NicknameMaxLength];
            encoded.Fill(StringConverter3.TerminatorByte);
            StringConverter3.SetString(encoded, toSet, NicknameMaxLength, Language, StringConverterOption.None);
            encoded[..10].CopyTo(NicknameTrash);
            Nickname11Raw = encoded[10];
            Nickname12Raw = encoded[11];
        }
    }

    // substruct0 @ Data[0x20..0x2C): species:11+teraType:5 | heldItem:10+unused:6 | experience:21+nickname11:8+unused:3 | ppBonuses | friendship | pokeball:6+nickname12:8+unused:2
    private ushort SpeciesTeraWord { get => ReadUInt16LittleEndian(Data[0x20..]); set => WriteUInt16LittleEndian(Data[0x20..], value); }
    public override ushort SpeciesInternal { get => (ushort)(SpeciesTeraWord & 0x7FF); set => SpeciesTeraWord = (ushort)((SpeciesTeraWord & ~0x7FF) | (value & 0x7FF)); }
    /// <summary>National Dex number. The raw expansion id (<see cref="SpeciesInternal"/>) diverges
    /// from it once forms are interleaved, so forms resolve to their base species here.</summary>
    public override ushort Species
    {
        get => ExpansionSpeciesTable.GetNatDex(SpeciesInternal);
        set => SpeciesInternal = ExpansionSpeciesTable.GetRawSpecies(value);
    }

    /// <summary>Elemental type the Pokémon's Tera Type is (this hack's own addition, packed into substruct0).</summary>
    /// <remarks>Stored as the decomp's own enum Type (TYPE_NONE=0, NORMAL=1..STEEL=9, MYSTERY=10,
    /// FIRE=11..FAIRY=19, STELLAR=20), remapped to PKHeX's MoveType (Normal=0, no Mystery gap).</remarks>
    public MoveType TeraType
    {
        get => ((SpeciesTeraWord >> 11) & 0x1F) switch
        {
            >= 1 and <= 9 and var t => (MoveType)(t - 1),
            >= 11 and <= 19 and var t => (MoveType)(t - 2),
            20 => (MoveType)TeraTypeUtil.Stellar,
            _ => MoveType.Normal,
        };
        set
        {
            var t = (int)value;
            var raw = t == TeraTypeUtil.Stellar ? 20 : t <= 8 ? t + 1 : t + 2;
            SpeciesTeraWord = (ushort)((SpeciesTeraWord & 0x7FF) | ((raw & 0x1F) << 11));
        }
    }

    private ushort HeldItemWord { get => ReadUInt16LittleEndian(Data[0x22..]); set => WriteUInt16LittleEndian(Data[0x22..], value); }
    /// <summary>Raw item id in the expansion's own item numbering.</summary>
    public ushort HeldItemRaw { get => (ushort)(HeldItemWord & 0x3FF); set => HeldItemWord = (ushort)((HeldItemWord & ~0x3FF) | (value & 0x3FF)); }

    /// <summary>Held item as a modern (Gen 4+) item id; 0 if the expansion item has no official equivalent.</summary>
    public ushort HeldItemModern
    {
        get => ExpansionItemTable.ToModern(HeldItemRaw);
        set { if (value != HeldItemModern) HeldItemRaw = ExpansionItemTable.ToExpansion(value); }
    }

    /// <summary>Gen 3 item id, since <see cref="Context"/> is Gen3 and PKHeX/PKVault convert items by context.
    /// Expansion-only items read as 0; the setter leaves the raw item untouched when the value is
    /// unchanged, so re-writing a Pokémon never drops an item that simply has no Gen 3 id.</summary>
    public override int HeldItem
    {
        get => ItemConverter.GetItemOld3(HeldItemModern);
        set
        {
            if (value == HeldItem)
                return;
            var modern = ItemConverter.GetItemFuture3((ushort)value);
            HeldItemModern = ItemConverter.IsItemTransferable34(modern) ? modern : (ushort)0;
        }
    }
    public override int SpriteItem => HeldItemModern;

    // Gen3's PP table has no entries for later moves; the expansion uses modern PP values.
    public override int GetMovePP(ushort move, int ppUpCount)
    {
        var basePP = MoveInfo.GetPP(EntityContext.Gen9, move);
        if (basePP == 0)
            basePP = MoveInfo.GetPP(EntityContext.Gen8, move);
        if (basePP == 0)
            basePP = MoveInfo.GetPP(EntityContext.Gen7, move);
        return Math.Min(0x7F, basePP * (5 + ppUpCount) / 5);
    }

    private uint ExpNicknameWord { get => ReadUInt32LittleEndian(Data[0x24..]); set => WriteUInt32LittleEndian(Data[0x24..], value); }
    public override uint EXP { get => ExpNicknameWord & 0x1FFFFF; set => ExpNicknameWord = (ExpNicknameWord & ~0x1FFFFFu) | (value & 0x1FFFFF); }
    private byte Nickname11Raw { get => (byte)((ExpNicknameWord >> 21) & 0xFF); set => ExpNicknameWord = (ExpNicknameWord & ~(0xFFu << 21)) | ((uint)value << 21); }

    private byte PPUps { get => Data[0x28]; set => Data[0x28] = value; }
    public override int Move1_PPUps { get => (PPUps >> 0) & 3; set => PPUps = (byte)((PPUps & ~(3 << 0)) | (value << 0)); }
    public override int Move2_PPUps { get => (PPUps >> 2) & 3; set => PPUps = (byte)((PPUps & ~(3 << 2)) | (value << 2)); }
    public override int Move3_PPUps { get => (PPUps >> 4) & 3; set => PPUps = (byte)((PPUps & ~(3 << 4)) | (value << 4)); }
    public override int Move4_PPUps { get => (PPUps >> 6) & 3; set => PPUps = (byte)((PPUps & ~(3 << 6)) | (value << 6)); }
    public override byte OriginalTrainerFriendship { get => Data[0x29]; set => Data[0x29] = value; }

    private ushort PokeballNicknameWord { get => ReadUInt16LittleEndian(Data[0x2A..]); set => WriteUInt16LittleEndian(Data[0x2A..], value); }
    // Expansion's enum PokeBall (include/constants/pokeball.h) indexed -> PKHeX Ball. Park Ball has no PKHeX equivalent.
    private static ReadOnlySpan<byte> ExpansionToPKHeXBall =>
    [
        (byte)PKHeX.Core.Ball.Strange, (byte)PKHeX.Core.Ball.Poke, (byte)PKHeX.Core.Ball.Great, (byte)PKHeX.Core.Ball.Ultra,
        (byte)PKHeX.Core.Ball.Master, (byte)PKHeX.Core.Ball.Premier, (byte)PKHeX.Core.Ball.Heal, (byte)PKHeX.Core.Ball.Net,
        (byte)PKHeX.Core.Ball.Nest, (byte)PKHeX.Core.Ball.Dive, (byte)PKHeX.Core.Ball.Dusk, (byte)PKHeX.Core.Ball.Timer,
        (byte)PKHeX.Core.Ball.Quick, (byte)PKHeX.Core.Ball.Repeat, (byte)PKHeX.Core.Ball.Luxury, (byte)PKHeX.Core.Ball.Level,
        (byte)PKHeX.Core.Ball.Lure, (byte)PKHeX.Core.Ball.Moon, (byte)PKHeX.Core.Ball.Friend, (byte)PKHeX.Core.Ball.Love,
        (byte)PKHeX.Core.Ball.Fast, (byte)PKHeX.Core.Ball.Heavy, (byte)PKHeX.Core.Ball.Dream, (byte)PKHeX.Core.Ball.Safari,
        (byte)PKHeX.Core.Ball.Sport, (byte)PKHeX.Core.Ball.Poke, (byte)PKHeX.Core.Ball.Beast, (byte)PKHeX.Core.Ball.Cherish,
    ];

    private byte BallRaw { get => (byte)(PokeballNicknameWord & 0x3F); set => PokeballNicknameWord = (ushort)((PokeballNicknameWord & ~0x3F) | (value & 0x3F)); }
    public override byte Ball
    {
        get => BallRaw < ExpansionToPKHeXBall.Length ? ExpansionToPKHeXBall[BallRaw] : (byte)PKHeX.Core.Ball.Poke;
        set
        {
            var index = ExpansionToPKHeXBall.IndexOf(value);
            BallRaw = (byte)(index < 0 ? 1 : index);
        }
    }
    private byte Nickname12Raw { get => (byte)((PokeballNicknameWord >> 6) & 0xFF); set => PokeballNicknameWord = (ushort)((PokeballNicknameWord & ~(0xFF << 6)) | (value << 6)); }

    // substruct1 @ Data[0x2C..0x38): 4x (move:11 + extra:5), then 4x (pp:7 + hyperTrained:1)
    private ushort MoveWord(int offset) => ReadUInt16LittleEndian(Data[offset..]);
    private void SetMoveWord(int offset, ushort value) => WriteUInt16LittleEndian(Data[offset..], value);

    public override ushort Move1 { get => (ushort)(MoveWord(0x2C) & 0x7FF); set => SetMoveWord(0x2C, (ushort)((MoveWord(0x2C) & ~0x7FF) | (value & 0x7FF))); }
    public override ushort Move2 { get => (ushort)(MoveWord(0x2E) & 0x7FF); set => SetMoveWord(0x2E, (ushort)((MoveWord(0x2E) & ~0x7FF) | (value & 0x7FF))); }
    public override ushort Move3 { get => (ushort)(MoveWord(0x30) & 0x7FF); set => SetMoveWord(0x30, (ushort)((MoveWord(0x30) & ~0x7FF) | (value & 0x7FF))); }
    public override ushort Move4 { get => (ushort)(MoveWord(0x32) & 0x7FF); set => SetMoveWord(0x32, (ushort)((MoveWord(0x32) & ~0x7FF) | (value & 0x7FF))); }

    /// <summary>5-bit evolution-tracker value stored alongside Move1/Move2 (this hack's own addition; no vanilla PKHeX analog).</summary>
    public int EvolutionTracker1 { get => (MoveWord(0x2C) >> 11) & 0x1F; set => SetMoveWord(0x2C, (ushort)((MoveWord(0x2C) & 0x7FF) | ((value & 0x1F) << 11))); }
    public int EvolutionTracker2 { get => (MoveWord(0x2E) >> 11) & 0x1F; set => SetMoveWord(0x2E, (ushort)((MoveWord(0x2E) & 0x7FF) | ((value & 0x1F) << 11))); }

    private bool HyperTrainedHP { get => (MoveWord(0x32) & (1 << 14)) != 0; set => SetMoveWord(0x32, (ushort)((MoveWord(0x32) & ~(1 << 14)) | (value ? 1 << 14 : 0))); }
    private bool HyperTrainedAttack { get => (MoveWord(0x32) & (1 << 15)) != 0; set => SetMoveWord(0x32, (ushort)((MoveWord(0x32) & ~(1 << 15)) | (value ? 1 << 15 : 0))); }

    private byte PPByte(int offset) => Data[offset];
    private void SetPPByte(int offset, byte value) => Data[offset] = value;

    public override int Move1_PP { get => PPByte(0x34) & 0x7F; set => SetPPByte(0x34, (byte)((PPByte(0x34) & 0x80) | (value & 0x7F))); }
    public override int Move2_PP { get => PPByte(0x35) & 0x7F; set => SetPPByte(0x35, (byte)((PPByte(0x35) & 0x80) | (value & 0x7F))); }
    public override int Move3_PP { get => PPByte(0x36) & 0x7F; set => SetPPByte(0x36, (byte)((PPByte(0x36) & 0x80) | (value & 0x7F))); }
    public override int Move4_PP { get => PPByte(0x37) & 0x7F; set => SetPPByte(0x37, (byte)((PPByte(0x37) & 0x80) | (value & 0x7F))); }

    private bool HyperTrainedDefense { get => (PPByte(0x34) & 0x80) != 0; set => SetPPByte(0x34, (byte)((PPByte(0x34) & 0x7F) | (value ? 0x80 : 0))); }
    private bool HyperTrainedSpeed { get => (PPByte(0x35) & 0x80) != 0; set => SetPPByte(0x35, (byte)((PPByte(0x35) & 0x7F) | (value ? 0x80 : 0))); }
    private bool HyperTrainedSpAttack { get => (PPByte(0x36) & 0x80) != 0; set => SetPPByte(0x36, (byte)((PPByte(0x36) & 0x7F) | (value ? 0x80 : 0))); }
    private bool HyperTrainedSpDefense { get => (PPByte(0x37) & 0x80) != 0; set => SetPPByte(0x37, (byte)((PPByte(0x37) & 0x7F) | (value ? 0x80 : 0))); }

    // substruct2 @ Data[0x38..0x44) - unchanged from vanilla
    public override int EV_HP { get => Data[0x38]; set => Data[0x38] = (byte)value; }
    public override int EV_ATK { get => Data[0x39]; set => Data[0x39] = (byte)value; }
    public override int EV_DEF { get => Data[0x3A]; set => Data[0x3A] = (byte)value; }
    public override int EV_SPE { get => Data[0x3B]; set => Data[0x3B] = (byte)value; }
    public override int EV_SPA { get => Data[0x3C]; set => Data[0x3C] = (byte)value; }
    public override int EV_SPD { get => Data[0x3D]; set => Data[0x3D] = (byte)value; }
    public override byte ContestCool { get => Data[0x3E]; set => Data[0x3E] = value; }
    public override byte ContestBeauty { get => Data[0x3F]; set => Data[0x3F] = value; }
    public override byte ContestCute { get => Data[0x40]; set => Data[0x40] = value; }
    public override byte ContestSmart { get => Data[0x41]; set => Data[0x41] = value; }
    public override byte ContestTough { get => Data[0x42]; set => Data[0x42] = value; }
    public override byte ContestSheen { get => Data[0x43]; set => Data[0x43] = value; }

    // substruct3 @ Data[0x44..0x50)
    public byte PokerusState { get => Data[0x44]; set => Data[0x44] = value; }
    public override int PokerusDays { get => PokerusState & 0xF; set => PokerusState = (byte)((PokerusState & ~0xF) | value); }
    public override int PokerusStrain { get => PokerusState >> 4; set => PokerusState = (byte)((PokerusState & 0xF) | (value << 4)); }
    public override ushort MetLocation { get => Data[0x45]; set => Data[0x45] = (byte)value; }

    private ushort Origins { get => ReadUInt16LittleEndian(Data[0x46..]); set => WriteUInt16LittleEndian(Data[0x46..], value); }
    public override byte MetLevel { get => (byte)(Origins & 0x7F); set => Origins = (ushort)((Origins & ~0x7F) | value); }
    public override GameVersion Version { get => (GameVersion)((Origins >> 7) & 0xF); set => Origins = (ushort)((Origins & ~0x780) | (((byte)value & 0xF) << 7)); }
    /// <summary>Dynamax Level (this hack's own addition; occupies the same bits vanilla used for Ball, which moved to substruct0 here).</summary>
    public byte DynamaxLevel { get => (byte)((Origins >> 11) & 0xF); set => Origins = (ushort)((Origins & ~0x7800) | ((value & 0xF) << 11)); }
    public override byte OriginalTrainerGender { get => (byte)((Origins >> 15) & 1); set => Origins = (ushort)((Origins & ~(1 << 15)) | ((value & 1) << 15)); }

    public uint IV32 { get => ReadUInt32LittleEndian(Data[0x48..]); set => WriteUInt32LittleEndian(Data[0x48..], value); }
    public override int IV_HP { get => (int)(IV32 >> 00) & 0x1F; set => IV32 = (IV32 & ~(0x1Fu << 00)) | ((value > 31 ? 31u : (uint)value) << 00); }
    public override int IV_ATK { get => (int)(IV32 >> 05) & 0x1F; set => IV32 = (IV32 & ~(0x1Fu << 05)) | ((value > 31 ? 31u : (uint)value) << 05); }
    public override int IV_DEF { get => (int)(IV32 >> 10) & 0x1F; set => IV32 = (IV32 & ~(0x1Fu << 10)) | ((value > 31 ? 31u : (uint)value) << 10); }
    public override int IV_SPE { get => (int)(IV32 >> 15) & 0x1F; set => IV32 = (IV32 & ~(0x1Fu << 15)) | ((value > 31 ? 31u : (uint)value) << 15); }
    public override int IV_SPA { get => (int)(IV32 >> 20) & 0x1F; set => IV32 = (IV32 & ~(0x1Fu << 20)) | ((value > 31 ? 31u : (uint)value) << 20); }
    public override int IV_SPD { get => (int)(IV32 >> 25) & 0x1F; set => IV32 = (IV32 & ~(0x1Fu << 25)) | ((value > 31 ? 31u : (uint)value) << 25); }

    public override bool IsEgg
    {
        get => ((IV32 >> 30) & 1) == 1;
        set
        {
            IV32 = (IV32 & ~0x40000000u) | (value ? 0x40000000u : 0);
            FlagIsEgg = value;
            if (value)
            {
                Nickname = EggNameJapanese;
                Language = (int)LanguageID.Japanese;
            }
        }
    }

    /// <summary>Indicates if the Pokémon is capable of Gigantamax as opposed to regular Dynamax (this hack's own addition).</summary>
    public bool CanGigantamax { get => ((IV32 >> 31) & 1) == 1; set => IV32 = (IV32 & ~0x80000000u) | (value ? 0x80000000u : 0); }

    public bool FlagIsBadEgg { get => (Data[0x13] & 1) != 0; set => Data[0x13] = (byte)((Data[0x13] & ~1) | (value ? 1 : 0)); }
    public bool FlagHasSpecies { get => (Data[0x13] & 2) != 0; set => Data[0x13] = (byte)((Data[0x13] & ~2) | (value ? 2 : 0)); }
    public bool FlagIsEgg { get => (Data[0x13] & 4) != 0; set => Data[0x13] = (byte)((Data[0x13] & ~4) | (value ? 4 : 0)); }
    // 0x12 packs language:3 + hiddenNatureModifier:5 (mints: effective nature = PID nature ^ modifier).
    public override int Language { get => Data[0x12] & 7; set => Data[0x12] = (byte)((Data[0x12] & ~7) | (value & 7)); }
    public int HiddenNatureModifier { get => Data[0x12] >> 3; set => Data[0x12] = (byte)((Data[0x12] & 7) | ((value & 0x1F) << 3)); }

    /// <summary>
    /// The nature the game computes stats with: the PID's nature ^ the mint modifier (the decomp's
    /// MON_DATA_HIDDEN_NATURE). For a Pokémon from Gen 5+, whose nature isn't tied to its PID, this is
    /// its real nature - <see cref="G3PKM.Nature"/> stays the PID's, which is what the in-game summary names.
    /// </summary>
    public override Nature StatAlignment
    {
        get
        {
            var pidNature = (int)(PID % 25);
            var effective = pidNature ^ HiddenNatureModifier;
            return (Nature)(effective < 25 ? effective : pidNature);
        }
        set => HiddenNatureModifier = (int)(PID % 25) ^ ((int)value % 25);
    }
    public override string OriginalTrainerName
    {
        get => StringConverter3.GetString(OriginalTrainerTrash, Language);
        set => StringConverter3.SetString(OriginalTrainerTrash, value, 7, Language, StringConverterOption.None);
    }
    // 0x1B packs markings:4 + compressedStatus:4; only touch the marking bits.
    public override byte MarkingValue
    {
        get => (byte)(SwapBits(Data[0x1B], 1, 2) & 0xF);
        set => Data[0x1B] = (byte)((Data[0x1B] & 0xF0) | (SwapBits(value, 1, 2) & 0xF));
    }
    public ushort Checksum { get => ReadUInt16LittleEndian(Data[0x1C..]); set => WriteUInt16LittleEndian(Data[0x1C..], value); }
    public ushort Sanity { get => ReadUInt16LittleEndian(Data[0x1E..]); set => WriteUInt16LittleEndian(Data[0x1E..], value); }

    // 0x1E packs hpLost:14 + shinyModifier:1 - the modifier flips PID-derived shininess.
    public bool ShinyModifier { get => (Sanity & (1 << 14)) != 0; set => Sanity = (ushort)((Sanity & ~(1 << 14)) | (value ? 1 << 14 : 0)); }
    public override bool IsShiny => base.IsShiny ^ ShinyModifier;

    // Ribbons/abilityNum/isShadow/modernFatefulEncounter @ Data[0x4C..0x50) - contest ribbon counts and the
    // 12 single-bit ribbons keep vanilla's exact bit positions; only the former Unused1-4 slots (bits 27-30)
    // were repurposed by this hack for isShadow/unused/abilityNum(2 bits).
    private uint RIB0 { get => ReadUInt32LittleEndian(Data[0x4C..]); set => WriteUInt32LittleEndian(Data[0x4C..], value); }
    public override byte RibbonCountG3Cool { get => (byte)((RIB0 >> 00) & 7); set => RIB0 = (RIB0 & ~(7u << 00)) | ((uint)(value & 7) << 00); }
    public override byte RibbonCountG3Beauty { get => (byte)((RIB0 >> 03) & 7); set => RIB0 = (RIB0 & ~(7u << 03)) | ((uint)(value & 7) << 03); }
    public override byte RibbonCountG3Cute { get => (byte)((RIB0 >> 06) & 7); set => RIB0 = (RIB0 & ~(7u << 06)) | ((uint)(value & 7) << 06); }
    public override byte RibbonCountG3Smart { get => (byte)((RIB0 >> 09) & 7); set => RIB0 = (RIB0 & ~(7u << 09)) | ((uint)(value & 7) << 09); }
    public override byte RibbonCountG3Tough { get => (byte)((RIB0 >> 12) & 7); set => RIB0 = (RIB0 & ~(7u << 12)) | ((uint)(value & 7) << 12); }
    public override bool RibbonChampionG3 { get => (RIB0 & (1 << 15)) != 0; set => RIB0 = (RIB0 & ~(1u << 15)) | (value ? 1u << 15 : 0u); }
    public override bool RibbonWinning { get => (RIB0 & (1 << 16)) != 0; set => RIB0 = (RIB0 & ~(1u << 16)) | (value ? 1u << 16 : 0u); }
    public override bool RibbonVictory { get => (RIB0 & (1 << 17)) != 0; set => RIB0 = (RIB0 & ~(1u << 17)) | (value ? 1u << 17 : 0u); }
    public override bool RibbonArtist { get => (RIB0 & (1 << 18)) != 0; set => RIB0 = (RIB0 & ~(1u << 18)) | (value ? 1u << 18 : 0u); }
    public override bool RibbonEffort { get => (RIB0 & (1 << 19)) != 0; set => RIB0 = (RIB0 & ~(1u << 19)) | (value ? 1u << 19 : 0u); }
    // Bits 20-22 are Marine/Land/Sky ribbons in this format (never distributed in vanilla either way);
    // G3PKM has no property for those specifically, so map them onto its Champion*/Battle/Regional/National
    // slots at the same bit positions, same as upstream PK3 does for these otherwise-unused bits.
    public override bool RibbonChampionBattle { get => (RIB0 & (1 << 20)) != 0; set => RIB0 = (RIB0 & ~(1u << 20)) | (value ? 1u << 20 : 0u); }
    public override bool RibbonChampionRegional { get => (RIB0 & (1 << 21)) != 0; set => RIB0 = (RIB0 & ~(1u << 21)) | (value ? 1u << 21 : 0u); }
    public override bool RibbonChampionNational { get => (RIB0 & (1 << 22)) != 0; set => RIB0 = (RIB0 & ~(1u << 22)) | (value ? 1u << 22 : 0u); }
    public override bool RibbonCountry { get => (RIB0 & (1 << 23)) != 0; set => RIB0 = (RIB0 & ~(1u << 23)) | (value ? 1u << 23 : 0u); }
    public override bool RibbonNational { get => (RIB0 & (1 << 24)) != 0; set => RIB0 = (RIB0 & ~(1u << 24)) | (value ? 1u << 24 : 0u); }
    public override bool RibbonEarth { get => (RIB0 & (1 << 25)) != 0; set => RIB0 = (RIB0 & ~(1u << 25)) | (value ? 1u << 25 : 0u); }
    public override bool RibbonWorld { get => (RIB0 & (1 << 26)) != 0; set => RIB0 = (RIB0 & ~(1u << 26)) | (value ? 1u << 26 : 0u); }

    public bool IsShadow { get => (RIB0 & (1 << 27)) != 0; set => RIB0 = (RIB0 & ~(1u << 27)) | (value ? 1u << 27 : 0u); }
    // No ShadowID/Purification concept in this format (Colosseum/XD-only in real games) - IShadowCapture requires them.
    public ushort ShadowID { get; set; }
    public int Purification { get; set; }

    /// <summary>The real widened (regular1/regular2/hidden) ability index this format stores.
    /// G3PKM's sealed <see cref="Ability"/>/<see cref="AbilityBit"/> only model a single bit
    /// (regular1 vs regular2) and cannot represent "hidden" - use this instead for accurate display.</summary>
    public int HiddenAbilityIndex { get => (int)((RIB0 >> 29) & 3); set => RIB0 = (RIB0 & ~(3u << 29)) | ((uint)(value & 3) << 29); }
    // Only rewrite on a real change, so generic code setting AbilityNumber (a single bit on G3PKM)
    // doesn't downgrade a hidden ability (index 2) to slot 1.
    public override bool AbilityBit { get => HiddenAbilityIndex != 0; set { if (value != AbilityBit) HiddenAbilityIndex = value ? 1 : 0; } }

    public override bool FatefulEncounter { get => RIB0 >> 31 == 1; set => RIB0 = (RIB0 & ~(1u << 31)) | (uint)(value ? 1 << 31 : 0); }
    public override int RibbonCount => BitOperations.PopCount(RIB0 & 0b00000111_11111111_11111111_11111111);

    // Unused1-4 have no meaning in this format (repurposed as isShadow/unused/abilityNum above).
    public override bool Unused1 { get => false; set { } }
    public override bool Unused2 { get => false; set { } }
    public override bool Unused3 { get => false; set { } }
    public override bool Unused4 { get => false; set { } }

    #region Battle Stats (unchanged from vanilla)
    public override int Status_Condition { get => ReadInt32LittleEndian(Data[0x50..]); set => WriteInt32LittleEndian(Data[0x50..], value); }
    public override byte Stat_Level { get => Data[0x54]; set => Data[0x54] = value; }
    public sbyte HeldMailID { get => (sbyte)Data[0x55]; set => Data[0x55] = (byte)value; }
    public override int Stat_HPCurrent { get => ReadUInt16LittleEndian(Data[0x56..]); set => WriteUInt16LittleEndian(Data[0x56..], (ushort)value); }
    public override int Stat_HPMax { get => ReadUInt16LittleEndian(Data[0x58..]); set => WriteUInt16LittleEndian(Data[0x58..], (ushort)value); }
    public override int Stat_ATK { get => ReadUInt16LittleEndian(Data[0x5A..]); set => WriteUInt16LittleEndian(Data[0x5A..], (ushort)value); }
    public override int Stat_DEF { get => ReadUInt16LittleEndian(Data[0x5C..]); set => WriteUInt16LittleEndian(Data[0x5C..], (ushort)value); }
    public override int Stat_SPE { get => ReadUInt16LittleEndian(Data[0x5E..]); set => WriteUInt16LittleEndian(Data[0x5E..], (ushort)value); }
    public override int Stat_SPA { get => ReadUInt16LittleEndian(Data[0x60..]); set => WriteUInt16LittleEndian(Data[0x60..], (ushort)value); }
    public override int Stat_SPD { get => ReadUInt16LittleEndian(Data[0x62..]); set => WriteUInt16LittleEndian(Data[0x62..], (ushort)value); }
    #endregion

    private ushort CalculateChecksum() => Checksums.Add16(Data[0x20..Size3Stored]);

    public override void RefreshChecksum()
    {
        FlagIsBadEgg = false;
        Checksum = CalculateChecksum();
    }

    public override bool ChecksumValid => CalculateChecksum() == Checksum;

    public override string GetString(ReadOnlySpan<byte> data) => StringConverter3.GetString(data, Language);
    public override int LoadString(ReadOnlySpan<byte> data, Span<char> destBuffer) => StringConverter3.LoadString(data, destBuffer, Language);
    public override int SetString(Span<byte> destBuffer, ReadOnlySpan<char> value, int maxLength, StringConverterOption option)
        => StringConverter3.SetString(destBuffer, value, maxLength, Language, option);

    public override int GetStringTerminatorIndex(ReadOnlySpan<byte> data) => TrashBytes8.GetTerminatorIndex(data);
    public override int GetStringLength(ReadOnlySpan<byte> data) => TrashBytes8.GetStringLength(data);
    public override int GetBytesPerChar() => 1;

    public override void PrepareNickname() => NicknameTrash.Fill(StringConverter3.TerminatorByte);
}
