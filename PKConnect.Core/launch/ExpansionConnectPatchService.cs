using PKHeX.Core;
using PKConnect.Core.patching;
using PKConnect.Core.formats;

namespace PKConnect.Core.launch;

/// <summary>
/// Pokemon Connect for pokeemerald-expansion builds that carry the Connect port
/// (src/connect.c in the decomp). Unlike the retail base patches, the build
/// always contains Connect and switches at runtime on the save's
/// VAR_CONNECT_HOME_REGION; PKVault only fills the build's landing zones.
///
/// The build describes itself through the .elf symbol <c>gConnectInfo</c>
/// (struct ConnectInfo in include/connect.h): struct sizes and field offsets are
/// computed by the compiler, so nothing here assumes the expansion's
/// struct TrainerMon layout.
/// </summary>
public static class ExpansionConnectPatchService
{
    private const uint Magic = 0x31584E43; // 'CNX1'
    private const int SupportedVersion = 1;
    private const uint AllIvs31 = 0x3FFFFFFF;

    public sealed record ConnectInfo(
        int Offset, int Version, int ThisRegion, int NumGyms, int PartyCapacity, int TrainerMonSize,
        int MonIv, int MonMoves, int MonSpecies, int MonHeldItem, int MonAbility, int MonLevel,
        int SpeciesSize, int ItemSize, int MoveSize, int AbilitySize,
        int[] ChampionTrainerIds, int[] GymTrainerIds,
        int GymParties, int GymPartySizes, int ChampionParty, int ChampionPartySize);

    public record GymResult(int Gym, int TeamSize, bool Applied, string? Reason);

    /// <summary>The build's Connect descriptor, or null for builds without the Connect port.</summary>
    public static ConnectInfo? ReadInfo(ElfSymbolTable elf, byte[] rom)
    {
        if (elf.RomOffset("gConnectInfo") is not { } o || o + 64 > rom.Length || Gba.ReadU32(rom, o) != Magic)
        {
            return null;
        }
        int U16(int at) => rom[o + at] | (rom[o + at + 1] << 8);
        var version = U16(4);
        if (version != SupportedVersion)
        {
            return null;
        }
        return new(
            Offset: o, Version: version, ThisRegion: rom[o + 6], NumGyms: rom[o + 7], PartyCapacity: rom[o + 8],
            TrainerMonSize: rom[o + 9], MonIv: rom[o + 10], MonMoves: rom[o + 11], MonSpecies: rom[o + 12],
            MonHeldItem: rom[o + 13], MonAbility: rom[o + 14], MonLevel: rom[o + 15],
            SpeciesSize: rom[o + 16], ItemSize: rom[o + 17], MoveSize: rom[o + 18], AbilitySize: rom[o + 19],
            ChampionTrainerIds: [U16(26), U16(28), U16(30)],
            GymTrainerIds: [.. Enumerable.Range(0, 8).Select(i => U16(32 + i * 2))],
            GymParties: Gba.ReadPointer(rom, o + 48),
            GymPartySizes: Gba.ReadPointer(rom, o + 52),
            ChampionParty: Gba.ReadPointer(rom, o + 56),
            ChampionPartySize: Gba.ReadPointer(rom, o + 60));
    }

    /// <summary>
    /// Visiting launch: a freshly randomized team per gym leader from the same
    /// pools the retail Connect patches use (gym-team-pools.json, keyed by the
    /// base game), leveled around <paramref name="averageLevel"/>.
    /// </summary>
    public static (List<(int Offset, byte[] Data)> Edits, List<GymResult> Results) BuildGymEdits(
        string gameId, byte[] rom, ConnectInfo info, double averageLevel, int? randomSeed = null)
    {
        var edits = new List<(int Offset, byte[] Data)>();
        var results = new List<GymResult>();
        var random = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();

        if (info.GymParties < 0 || info.GymPartySizes < 0 || info.TrainerMonSize == 0)
        {
            return (edits, results);
        }

        var poolGame = ExpansionSaveDetector.BaseVersion(gameId) switch
        {
            GameVersion.FR => "firered",
            GameVersion.LG => "leafgreen",
            _ => "emerald",
        };
        var config = GymTeamPoolConfigLoader.Load();
        // Pools for this base game, in file order = badge order = gConnectInfo's gym order.
        var leaders = config.Leaders.Where(l => l.TrainerIds.ContainsKey(poolGame)).ToList();

        for (var gym = 0; gym < info.NumGyms; gym++)
        {
            var leader = gym < leaders.Count ? leaders[gym] : null;
            var pool = leader?.Pool
                .Select(ToExpansionMon)
                .Where(m => m.Species > 0)
                .ToList() ?? [];
            if (pool.Count == 0)
            {
                results.Add(new(gym, 0, false, "No gym team pool for this leader; the vanilla team is kept"));
                continue;
            }

            var teamSize = Math.Clamp(leader!.TeamSize, 1, Math.Min(pool.Count, info.PartyCapacity));
            var team = pool.OrderBy(_ => random.Next()).Take(teamSize).ToList();

            var zone = info.GymParties + gym * info.PartyCapacity * info.TrainerMonSize;
            var data = new byte[team.Count * info.TrainerMonSize];
            for (var i = 0; i < team.Count; i++)
            {
                var o = i * info.TrainerMonSize;
                // The zone's build-time record carries trainerproc's defaults (ball, gender, ...).
                Array.Copy(rom, zone + o, data, o, info.TrainerMonSize);
                WriteField(data, o + info.MonIv, 4, AllIvs31);
                for (var m = 0; m < 4; m++)
                {
                    WriteField(data, o + info.MonMoves + m * info.MoveSize, info.MoveSize,
                        (uint)(m < team[i].Moves.Length ? team[i].Moves[m] : 0));
                }
                WriteField(data, o + info.MonSpecies, info.SpeciesSize, (uint)team[i].Species);
                WriteField(data, o + info.MonHeldItem, info.ItemSize, (uint)team[i].Item);
                WriteField(data, o + info.MonAbility, info.AbilitySize, 0);
                var delta = ((random.NextDouble() * 2) - 1) * config.DefaultLevelVariance;
                data[o + info.MonLevel] = (byte)Math.Clamp((int)Math.Round(averageLevel + delta), 1, 100);
            }

            edits.Add((zone, data));
            edits.Add((info.GymPartySizes + gym, [(byte)team.Count]));
            results.Add(new(gym, team.Count, true, null));
        }

        return (edits, results);
    }

    private readonly record struct ExpansionMon(int Species, int Item, int[] Moves);

    /// <summary>Pool entries are Gen 3 internal ids (retail ROM constants): convert to this build's ids.</summary>
    private static ExpansionMon ToExpansionMon(GymPoolMon mon)
    {
        var national = SpeciesConverter.GetNational3((ushort)mon.Species);
        var species = national is > 0 and <= 1025 ? ExpansionSpeciesTable.GetRawSpecies(national) : (ushort)0;
        var modernItem = mon.Item > 0 ? ItemConverter.GetItemFuture3((ushort)mon.Item) : (ushort)0;
        var item = modernItem is > 0 and < 2000 ? ExpansionItemTable.ToExpansion(modernItem) : (ushort)0;
        return new(species, item, mon.Moves);
    }

    private static void WriteField(byte[] b, int o, int size, uint value)
    {
        for (var i = 0; i < size; i++)
        {
            b[o + i] = (byte)(value >> (8 * i));
        }
    }
}
