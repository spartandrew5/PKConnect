using PKConnect.Core.launch;
using PKConnect.Core.patching;

namespace PKConnect.Core.champions;

/// <summary>
/// Swaps the region's current champion team into a pokeemerald-expansion build's
/// Pokemon League champion battle (Wallace / FRLG's first-champion rival), the
/// expansion counterpart of <see cref="ChampionTeamPatchService"/>.
///
/// Nothing is at a fixed address, so the champion is found through the build's
/// .elf: <c>gTrainers[DIFFICULTY_COUNT][TRAINERS_COUNT]</c> of 52-byte
/// <c>struct Trainer</c>. The expansion's structs aren't retail's, and the .elf has
/// no debug info to describe them, so only fields verified against real builds
/// are touched, and every one is sanity-checked before writing:
///  - <c>Trainer</c>: party pointer at +8; partySize in bits 3-5 of byte +30 (after
///    trainerClass and the first bitfield byte); trainerName at +31, or +32 on
///    builds that put a trainerPic byte at +31.
///  - <c>TrainerMon</c> (36 bytes): nickname/ev pointers, iv (u32), moves[4],
///    species, heldItem, ability (u16 each), then level; the remaining bytes
///    (ball, friendship, nature/gender/tera bitfields, tags) are copied from the
///    trainer's own existing party so they stay valid for the build.
///
/// The new party goes to free space and the trainer is repointed; the vanilla
/// party stays untouched in the ROM.
/// </summary>
public static class ExpansionChampionPatchService
{
    public record ChampionEditResult(int TrainerId, int TeamSize, bool Applied, string? Reason);

    private const int TrainerSize = 52;
    private const int PartyPointerOffset = 8;
    private const int PartySizeByte = 30;
    private const int PartySizeShift = 3;
    private const int DifficultyCount = 3;

    private const int MonSize = 36;
    private const int MonIv = 8;
    private const int MonMoves = 12;
    private const int MonSpecies = 20;
    private const int MonItem = 22;
    private const int MonAbility = 24;
    private const int MonLevel = 26;
    private const uint AllIvs31 = 0x3FFFFFFF;

    private const int NameLength = 11; // TRAINER_NAME_LENGTH + 1
    private const int SpeciesCeiling = 2048;

    public static bool IsSupportedGame(string gameId) =>
        ChampionBattleConfigLoader.Load().ExpansionChampionTrainerIds.ContainsKey(gameId);

    public static (List<(int Offset, byte[] Data)> Edits, List<ChampionEditResult> Results) BuildChampionEdits(
        string gameId, byte[] rom, ElfSymbolTable elf, double? averageLevel, ChampionTeamDTO champion,
        RomFreeSpaceAllocator allocator)
    {
        var edits = new List<(int Offset, byte[] Data)>();
        var results = new List<ChampionEditResult>();
        var config = ChampionBattleConfigLoader.Load();
        if (!config.ExpansionChampionTrainerIds.TryGetValue(gameId, out var trainerIds) || trainerIds.Length == 0)
        {
            return (edits, results);
        }

        var mons = (champion.Team ?? [])
            .Select(ChampionNameResolver.ResolveExpansion)
            .Where(m => m.Species > 0)
            .Take(6)
            .ToList();
        if (mons.Count == 0)
        {
            results.AddRange(trainerIds.Select(id => new ChampionEditResult(id, 0, false, "No resolvable champion Pokemon")));
            return (edits, results);
        }

        var teamAvg = mons.Average(m => m.Level);
        var shift = averageLevel.HasValue ? averageLevel.Value + config.ChampionLevelBonus - teamAvg : 0;
        int ScaledLevel(int level) => Math.Clamp((int)Math.Round(level + shift), 2, 100);

        var table = elf.Get("gTrainers");
        var tableOff = elf.RomOffset("gTrainers");
        if (table is null || tableOff is null || table.Size % (DifficultyCount * TrainerSize) != 0)
        {
            results.AddRange(trainerIds.Select(id => new ChampionEditResult(id, mons.Count, false,
                "gTrainers missing or not a [3][N] array of 52-byte trainers in this build")));
            return (edits, results);
        }
        var rowSize = (int)table.Size / DifficultyCount;
        var trainerCount = rowSize / TrainerSize;
        byte[]? connectParty = null;

        foreach (var trainerId in trainerIds)
        {
            if (trainerId >= trainerCount)
            {
                results.Add(new(trainerId, mons.Count, false, $"trainer id beyond this build's {trainerCount} trainers"));
                continue;
            }

            // Every difficulty row that defines this trainer (unset rows are all zero).
            var patchedRows = 0;
            string? failure = null;
            for (var difficulty = 0; difficulty < DifficultyCount; difficulty++)
            {
                var entry = tableOff.Value + difficulty * rowSize + trainerId * TrainerSize;
                var party = Gba.ReadPointer(rom, entry + PartyPointerOffset);
                if (party < 0)
                {
                    continue;
                }

                var oldSize = (rom[entry + PartySizeByte] >> PartySizeShift) & 7;
                if (oldSize is < 1 or > 6 || !PartyLooksValid(rom, party, oldSize))
                {
                    failure = "trainer struct doesn't match the expected expansion layout";
                    continue;
                }

                var partyBytes = new byte[mons.Count * MonSize];
                for (var i = 0; i < mons.Count; i++)
                {
                    // Template: this trainer's own mon in the same slot (or its last one).
                    var template = party + Math.Min(i, oldSize - 1) * MonSize;
                    var o = i * MonSize;
                    Array.Copy(rom, template, partyBytes, o, MonSize);
                    WriteU32(partyBytes, o + 0, 0); // no nickname
                    WriteU32(partyBytes, o + 4, 0); // default EVs
                    WriteU32(partyBytes, o + MonIv, AllIvs31);
                    for (var m = 0; m < 4; m++)
                    {
                        WriteU16(partyBytes, o + MonMoves + m * 2, (ushort)mons[i].Moves[m]);
                    }
                    WriteU16(partyBytes, o + MonSpecies, (ushort)mons[i].Species);
                    WriteU16(partyBytes, o + MonItem, (ushort)mons[i].Item);
                    WriteU16(partyBytes, o + MonAbility, 0); // species' default ability
                    partyBytes[o + MonLevel] = (byte)ScaledLevel(mons[i].Level);
                }

                var dest = allocator.Allocate(partyBytes.Length);
                if (dest is null)
                {
                    failure = "No free ROM space found for party data";
                    break;
                }
                connectParty ??= partyBytes;
                edits.Add((dest.Value, partyBytes));
                edits.Add((entry + PartyPointerOffset, Gba.PointerBytes(dest.Value)));
                edits.Add((entry + PartySizeByte,
                    [(byte)((rom[entry + PartySizeByte] & ~(7 << PartySizeShift)) | (mons.Count << PartySizeShift))]));

                if (!string.IsNullOrWhiteSpace(champion.ChampionName) && FindNameOffset(rom, entry) is { } nameOff)
                {
                    var encoded = ChampionNameResolver.EncodeGen3TrainerName(champion.ChampionName);
                    edits.Add((nameOff, encoded[..NameLength]));
                }
                patchedRows++;
            }

            results.Add(patchedRows > 0
                ? new(trainerId, mons.Count, true, null)
                : new(trainerId, mons.Count, false, failure ?? "trainer has no party in this build"));
        }

        // Builds with the Connect port also get the team in gConnectInfo's champion landing zone.
        // The battle then fights it through Connect_GetTrainerOverride, and the build knows a
        // patched-in champion is someone else (FRLG prints the save's rival name for the vanilla one).
        if (connectParty != null
            && ExpansionConnectPatchService.ReadInfo(elf, rom) is { } info
            && info.TrainerMonSize == MonSize
            && mons.Count <= info.PartyCapacity
            && info.ChampionParty + info.PartyCapacity * MonSize <= rom.Length
            && info.ChampionPartySize < rom.Length)
        {
            edits.Add((info.ChampionParty, connectParty));
            edits.Add((info.ChampionPartySize, [(byte)mons.Count]));
        }

        return (edits, results);
    }

    private static bool PartyLooksValid(byte[] rom, int party, int size)
    {
        if (party + size * MonSize > rom.Length)
        {
            return false;
        }
        for (var i = 0; i < size; i++)
        {
            var o = party + i * MonSize;
            var species = rom[o + MonSpecies] | (rom[o + MonSpecies + 1] << 8);
            var level = rom[o + MonLevel];
            var iv = Gba.ReadU32(rom, o + MonIv);
            if (species is 0 or >= SpeciesCeiling || level is 0 or > 100 || iv > AllIvs31)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>trainerName starts at +31, or +32 when the build keeps a trainerPic byte at +31.</summary>
    private static int? FindNameOffset(byte[] rom, int entry)
    {
        static bool IsUpper(byte b) => b is >= 0xBB and <= 0xD4;
        foreach (var off in new[] { 31, 32 })
        {
            var at = entry + off;
            if (IsUpper(rom[at]) && !IsUpper(rom[at - 1]))
            {
                return at;
            }
        }
        return null;
    }

    private static void WriteU16(byte[] b, int o, ushort v)
    {
        b[o] = (byte)v;
        b[o + 1] = (byte)(v >> 8);
    }

    private static void WriteU32(byte[] b, int o, uint v)
    {
        b[o] = (byte)v;
        b[o + 1] = (byte)(v >> 8);
        b[o + 2] = (byte)(v >> 16);
        b[o + 3] = (byte)(v >> 24);
    }
}
