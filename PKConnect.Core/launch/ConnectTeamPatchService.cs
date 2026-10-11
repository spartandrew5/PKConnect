namespace PKConnect.Core.launch;

/// <summary>
/// Writes opponent teams into the landing zones a Connect base patch reserves
/// (see <see cref="BasePatchService"/>): gym leaders from the configured team
/// pools. The base
/// patch already points each trainer at its zone, so a team is only the zone's
/// records plus the GBA partySize byte or the GB terminator — no free space, no
/// pointer rewrites.
/// </summary>
public static class ConnectTeamPatchService
{
    public record ZoneResult(string Zone, int TeamSize, bool Applied, string? Reason);

    public readonly record struct ZoneMon(int Level, int Species, int Item, int[] Moves);

    private readonly record struct PoolMon(int Species, int Item, int[] Moves);

    private readonly record struct GymPool(List<PoolMon> Pool, int TeamSize, double LevelVariance);

    private const double GbLevelVariance = 2;

    /// <summary>
    /// A freshly randomized team per gym leader, leveled around
    /// <paramref name="averageLevel"/>. Leaders without a configured pool keep
    /// the base patch's default team.
    /// </summary>
    public static (List<(int Offset, byte[] Data)> Edits, List<ZoneResult> Results) BuildGymEdits(
        ConnectBasePatch patch, double averageLevel, int? randomSeed = null)
    {
        var manifest = patch.Manifest;
        var random = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
        var edits = new List<(int Offset, byte[] Data)>();
        var results = new List<ZoneResult>();

        foreach (var zone in manifest.Gyms)
        {
            if (FindPool(manifest, zone) is not { } pool)
            {
                results.Add(new(zone.Leader, 0, false, "No gym team pool configured; the base patch's default team is kept"));
                continue;
            }

            var teamSize = Math.Min(Math.Max(pool.TeamSize, 1), Math.Min(pool.Pool.Count, zone.Capacity));
            var team = pool.Pool
                .OrderBy(_ => random.Next())
                .Take(teamSize)
                .Select(m => new ZoneMon(ComputeLevel(averageLevel, pool.LevelVariance, random), m.Species, m.Item, m.Moves))
                .ToList();

            edits.AddRange(WriteZone(manifest, zone, team));
            results.Add(new(zone.Leader, team.Count, true, null));
        }

        return (edits, results);
    }

    public static List<(int Offset, byte[] Data)> WriteZone(
        ConnectBaseManifest manifest, ConnectLandingZone zone, IReadOnlyList<ZoneMon> team)
    {
        if (team.Count == 0 || team.Count > zone.Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(team),
                $"{zone.Leader}: team size {team.Count} is outside 1..{zone.Capacity}");
        }

        var data = new byte[team.Count * zone.RecordSize + (zone.Terminator.HasValue ? 1 : 0)];

        for (var i = 0; i < team.Count; i++)
        {
            var o = i * zone.RecordSize;
            var mon = team[i];

            switch (manifest.Format)
            {
                case ConnectRomFormat.Gba:
                    // TrainerMonItemCustomMoves: iv:u16, lvl:u8, pad:u8, species:u16, heldItem:u16, moves[4]:u16
                    WriteU16(data, o, 255);
                    data[o + 2] = (byte)mon.Level;
                    WriteU16(data, o + 4, (ushort)mon.Species);
                    WriteU16(data, o + 6, (ushort)mon.Item);
                    for (var m = 0; m < 4; m++)
                    {
                        WriteU16(data, o + 8 + m * 2, (ushort)MoveAt(mon, m));
                    }
                    break;

                case ConnectRomFormat.Crystal:
                    // TRAINERTYPE_ITEM_MOVES: level, species, item, move1..move4
                    data[o] = (byte)mon.Level;
                    data[o + 1] = (byte)mon.Species;
                    data[o + 2] = (byte)mon.Item;
                    for (var m = 0; m < 4; m++)
                    {
                        data[o + 3 + m] = (byte)MoveAt(mon, m);
                    }
                    break;

                case ConnectRomFormat.Yellow:
                    // Gen 1 per-level party: level, species (after the zone's 0xFF marker)
                    data[o] = (byte)mon.Level;
                    data[o + 1] = (byte)mon.Species;
                    break;
            }
        }

        if (zone.Terminator is { } terminator)
        {
            data[^1] = (byte)terminator;
        }

        var edits = new List<(int Offset, byte[] Data)> { (zone.PartyOffset, data) };
        if (zone.PartySizeOffset is { } sizeOffset)
        {
            edits.Add((sizeOffset, [(byte)team.Count]));
        }

        return edits;
    }

    private static GymPool? FindPool(ConnectBaseManifest manifest, ConnectLandingZone zone)
    {
        if (manifest.Format == ConnectRomFormat.Gba)
        {
            var config = GymTeamPoolConfigLoader.Load();
            var leader = config.Leaders.FirstOrDefault(l =>
                zone.TrainerId is { } trainerId
                && l.TrainerIds.TryGetValue(manifest.GameId, out var leaderTrainerId)
                && leaderTrainerId == trainerId);

            return leader is null || leader.Pool.Count == 0
                ? null
                : new GymPool([.. leader.Pool.Select(m => new PoolMon(m.Species, m.Item, m.Moves))], leader.TeamSize, config.DefaultLevelVariance);
        }

        if (!GbGymTeamPoolConfigLoader.Load().Games.TryGetValue(manifest.GameId, out var game))
        {
            return null;
        }

        var key = NormalizeName(zone.Leader);
        var gbLeader = game.Leaders.FirstOrDefault(l => NormalizeName(l.Id) == key);

        return gbLeader is null || gbLeader.Pool.Count == 0
            ? null
            : new GymPool([.. gbLeader.Pool.Select(m => new PoolMon(m.Species, 0, m.Moves ?? []))], gbLeader.TeamSize, GbLevelVariance);
    }

    private static string NormalizeName(string name) =>
        new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    private static int MoveAt(ZoneMon mon, int index) => index < mon.Moves.Length ? mon.Moves[index] : 0;

    private static int ComputeLevel(double averageLevel, double variance, Random random)
    {
        var delta = ((random.NextDouble() * 2) - 1) * variance;
        return Math.Clamp((int)Math.Round(averageLevel + delta), 1, 100);
    }

    private static void WriteU16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }
}
