namespace PKConnect.Core.launch;

/// <summary>
/// ROM layout facts for the vanilla `struct Trainer` / gTrainers[] table shared
/// by the pokeemerald/pokefirered decompilations. Only the home-region launch
/// path (vanilla ROM, no Connect base patch) uses these, to swap the current
/// champion in; visiting launches take every offset from the base patch
/// manifest (see <see cref="BasePatchService"/>).
///
/// struct Trainer
/// {
///     /*0x00*/ u8 partyFlags;
///     /*0x01*/ u8 trainerClass;
///     /*0x02*/ u8 encounterMusic_gender;
///     /*0x03*/ u8 trainerPic;
///     /*0x04*/ u8 trainerName[12];
///     /*0x10*/ u16 items[4];
///     /*0x18*/ bool8 doubleBattle;
///     /*0x1C*/ u32 aiFlags;
///     /*0x20*/ u8 partySize;
///     /*0x24*/ union TrainerMonPtr party;
/// }; // sizeof == 0x28
///
/// gTrainers[] base ROM offsets below were read directly from each vanilla
/// game's compiled .sym symbol table (`gTrainers` address minus 0x08000000).
/// </summary>
public static class GymTrainerTableData
{
    public const int TrainerStructSize = 0x28;
    public const int PartyFlagsOffset = 0x00;
    public const int TrainerNameOffset = 0x04;
    public const int TrainerNameLength = 12;
    public const int PartySizeOffset = 0x20;
    public const int PartyPointerOffset = 0x24;

    // F_TRAINER_PARTY_CUSTOM_MOVESET (1 << 0) | F_TRAINER_PARTY_HELD_ITEM (1 << 1):
    // generated teams always use the 16-byte "TrainerMonItemCustomMoves" layout
    // (iv:u16, lvl:u8, +1 pad, species:u16, heldItem:u16, moves[4]:u16).
    public const byte GeneratedPartyFlags = 0x03;
    public const int TrainerMonSize = 16;

    private static readonly Dictionary<string, int> GTrainersOffsetByGame = new()
    {
        ["emerald"] = 0x310030,
        ["firered"] = 0x23EAC8,
        ["leafgreen"] = 0x23EAA4,
        // Vanilla (unpatched) ROM addresses for revision 0 - used only by the
        // home-region launch path, which never applies the Connect base patch.
        // Verified against a clean pokeruby/pokesapphire decomp build's own
        // .sym (gTrainers minus 0x08000000); do NOT reuse the base patch
        // manifest's gTrainers.offset here, that reflects the base-patched
        // build's relinked address, which differs from vanilla by a few bytes.
        ["ruby"] = 0x1F04FC,
        ["sapphire"] = 0x1F048C,
    };

    // Ruby/Sapphire circulate as multiple dumped revisions (software version
    // byte at ROM offset 0xBC) with genuinely different code layout - unlike
    // Emerald/FireRed/LeafGreen, which only have one revision in the wild.
    // Verified the same way as GTrainersOffsetByGame, against a REVISION=2 build.
    private static readonly Dictionary<string, int> GTrainersOffsetByGameRev2 = new()
    {
        ["ruby"] = 0x1F0514,
        ["sapphire"] = 0x1F04A4,
    };

    public static bool IsSupportedGame(string gameId) => GTrainersOffsetByGame.ContainsKey(gameId);

    private static int GTrainersOffset(string gameId, byte[]? rom)
    {
        if (rom is { Length: > 0xBC } && rom[0xBC] == 2
            && GTrainersOffsetByGameRev2.TryGetValue(gameId, out var rev2Offset))
        {
            return rev2Offset;
        }
        return GTrainersOffsetByGame[gameId];
    }

    public static int GetTrainerStructOffset(string gameId, int trainerId, byte[]? rom = null) =>
        GTrainersOffset(gameId, rom) + trainerId * TrainerStructSize;
}
