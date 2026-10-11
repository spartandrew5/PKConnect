using PKConnect.Core.launch;
using PKConnect.Core.patching;

namespace PKConnect.Core.champions;

/// <summary>
/// Home-region launch path (vanilla ROM, no Connect base patch): patches the
/// region's current champion team into the vanilla Pokemon League Champion
/// battle for Emerald / FireRed. Visiting launches use the base patch's landing
/// zones instead (see <see cref="ConnectTeamPatchService"/>).
///
/// The resolved team is
/// written into freshly-found 0xFF free space as a 16-byte-per-mon
/// "TrainerMonItemCustomMoves" party, and the champion trainer's struct in
/// gTrainers[] is repointed at it (partyFlags = custom moveset + held item,
/// partySize, party pointer). No existing ROM address is shifted.
///
/// The stored team's levels are re-centered on the traveling trainer's party
/// average plus <c>championLevelBonus</c>, preserving the team's relative level
/// curve (the ace still outlevels the rest).
/// </summary>
public static class ChampionTeamPatchService
{
    public record ChampionEditResult(string GameId, int TrainerId, int TeamSize, bool Applied, string? Reason);

    public static bool IsSupportedGame(string gameId) =>
        GymTrainerTableData.IsSupportedGame(gameId)
        && ChampionBattleConfigLoader.Load().GbaChampionTrainerIds.ContainsKey(gameId);

    public static (List<(int Offset, byte[] Data)> Edits, List<ChampionEditResult> Results) BuildChampionEdits(
        string gameId,
        byte[] rom,
        double? averageLevel,
        ChampionTeamDTO champion,
        RomFreeSpaceAllocator? allocator = null)
    {
        var edits = new List<(int Offset, byte[] Data)>();
        var results = new List<ChampionEditResult>();

        var config = ChampionBattleConfigLoader.Load();
        if (!config.GbaChampionTrainerIds.TryGetValue(gameId, out var trainerIds)
            || trainerIds.Length == 0
            || !GymTrainerTableData.IsSupportedGame(gameId))
        {
            return (edits, results);
        }

        var mons = ChampionTeamSubstitution.Adapt(gameId, champion.Team)
            .Select(ChampionNameResolver.ResolveGen3)
            .Where(m => m.Species > 0)
            .Take(6)
            .ToList();

        if (mons.Count == 0)
        {
            foreach (var tid in trainerIds)
            {
                results.Add(new(gameId, tid, 0, false, "No resolvable champion Pokemon"));
            }
            return (edits, results);
        }

        // Re-center the stored team's level curve on the traveling trainer's
        // party average + champion bump; keep stored levels when no average is
        // available (patch generated before a party is known).
        var teamAvg = mons.Average(m => m.Level);
        var shift = averageLevel.HasValue
            ? averageLevel.Value + config.ChampionLevelBonus - teamAvg
            : 0;
        int ScaledLevel(int level) => Math.Clamp((int)Math.Round(level + shift), 2, 100);

        allocator ??= new RomFreeSpaceAllocator(rom);

        foreach (var trainerId in trainerIds)
        {
            var partyBytes = new byte[mons.Count * GymTrainerTableData.TrainerMonSize];
            for (var i = 0; i < mons.Count; i++)
            {
                WriteTrainerMon(partyBytes, i * GymTrainerTableData.TrainerMonSize, mons[i], ScaledLevel(mons[i].Level));
            }

            var freeOffset = allocator.Allocate(partyBytes.Length);
            if (freeOffset == null)
            {
                results.Add(new(gameId, trainerId, mons.Count, false, "No free ROM space found for party data"));
                continue;
            }

            var structOffset = GymTrainerTableData.GetTrainerStructOffset(gameId, trainerId, rom);
            if (structOffset < 0 || structOffset + GymTrainerTableData.TrainerStructSize > rom.Length)
            {
                results.Add(new(gameId, trainerId, mons.Count, false, "Trainer struct offset is out of range for this ROM file"));
                continue;
            }

            edits.Add((freeOffset.Value, partyBytes));
            edits.Add((structOffset + GymTrainerTableData.PartyFlagsOffset, [GymTrainerTableData.GeneratedPartyFlags]));
            edits.Add((structOffset + GymTrainerTableData.PartySizeOffset, [(byte)mons.Count]));

            // Several battle-intro scripts greet you with the trainer's own
            // struct name ("STEVEN: Welcome, RED.") rather than a hardcoded
            // string — write the reigning champion's name there too.
            if (!string.IsNullOrWhiteSpace(champion.ChampionName))
            {
                edits.Add((structOffset + GymTrainerTableData.TrainerNameOffset,
                    ChampionNameResolver.EncodeGen3TrainerName(champion.ChampionName)));
            }

            var partyPointer = 0x08000000 + freeOffset.Value;
            edits.Add((structOffset + GymTrainerTableData.PartyPointerOffset,
            [
                (byte)(partyPointer & 0xFF),
                (byte)((partyPointer >> 8) & 0xFF),
                (byte)((partyPointer >> 16) & 0xFF),
                (byte)((partyPointer >> 24) & 0xFF),
            ]));

            results.Add(new(gameId, trainerId, mons.Count, true, null));
        }

        return (edits, results);
    }

    private static void WriteTrainerMon(byte[] buffer, int offset, ChampionNameResolver.ResolvedMon mon, int level)
    {
        const ushort perfectIv = 255;

        WriteU16(buffer, offset + 0, perfectIv);
        buffer[offset + 2] = (byte)level;
        // offset + 3 is compiler alignment padding, left as 0.
        WriteU16(buffer, offset + 4, (ushort)mon.Species);
        WriteU16(buffer, offset + 6, (ushort)mon.Item);

        for (var m = 0; m < 4; m++)
        {
            var moveId = m < mon.Moves.Length ? mon.Moves[m] : 0;
            WriteU16(buffer, offset + 8 + (m * 2), (ushort)moveId);
        }
    }

    private static void WriteU16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }
}
