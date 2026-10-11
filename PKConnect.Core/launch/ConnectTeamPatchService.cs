using System.Text.Json;
using PKConnect.Core.champions;

namespace PKConnect.Core.launch;

/// <summary>
/// Writes opponent teams into the landing zones a Connect base patch reserves
/// (see <see cref="BasePatchService"/>): gym leaders from the configured team
/// pools, and every champion slot from the region's current champion. The base
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

    /// <summary>
    /// The region's current champion team into every champion slot, with its
    /// level curve re-centered on the traveling trainer's party average plus
    /// <paramref name="championLevelBonus"/> (stored levels when no average).
    /// </summary>
    public static (List<(int Offset, byte[] Data)> Edits, List<ZoneResult> Results) BuildChampionEdits(
        ConnectBasePatch patch, ChampionTeamDTO champion, double? averageLevel, int championLevelBonus)
    {
        var manifest = patch.Manifest;
        var edits = new List<(int Offset, byte[] Data)>();
        var results = new List<ZoneResult>();

        Func<ChampionMonDTO, ChampionNameResolver.ResolvedMon> resolve = manifest.Format switch
        {
            ConnectRomFormat.Gba => ChampionNameResolver.ResolveGen3,
            ConnectRomFormat.Crystal => ChampionNameResolver.ResolveGen2,
            _ => ChampionNameResolver.ResolveGen1,
        };

        var resolved = ChampionTeamSubstitution.Adapt(manifest.GameId, champion.Team)
            .Select(resolve)
            .Where(m => m.Species > 0)
            .ToList();

        if (resolved.Count == 0)
        {
            results.AddRange(manifest.ChampionSlots.Select(slot =>
                new ZoneResult(slot.Leader, 0, false, "No champion Pokemon exist in this game")));
            return (edits, results);
        }

        var teamAvg = resolved.Average(m => m.Level);
        var shift = averageLevel.HasValue ? averageLevel.Value + championLevelBonus - teamAvg : 0;

        foreach (var slot in manifest.ChampionSlots)
        {
            var team = resolved
                .Take(slot.Capacity)
                .Select(m => new ZoneMon(Math.Clamp((int)Math.Round(m.Level + shift), 2, 100), m.Species, m.Item, m.Moves))
                .ToList();

            // Pad with the ace when later parties are located by walking this one.
            while (slot.FixedSize && team.Count < slot.Capacity)
            {
                team.Add(team[^1]);
            }

            edits.AddRange(WriteZone(manifest, slot, team));

            // Several battle-intro scripts greet you with the trainer's own
            // struct name ("STEVEN: Welcome, RED.") rather than a hardcoded
            // string. GB's name lives inline in the party buffer itself
            // (see WriteZone's Crystal/Yellow branches); GBA keeps it in a
            // fixed struct field alongside partySize.
            if (manifest.Format == ConnectRomFormat.Gba
                && !string.IsNullOrWhiteSpace(champion.ChampionName)
                && slot.PartySizeOffset is { } partySizeOffset)
            {
                var structOffset = partySizeOffset - GymTrainerTableData.PartySizeOffset;
                edits.Add((structOffset + GymTrainerTableData.TrainerNameOffset,
                    ChampionNameResolver.EncodeGen3TrainerName(champion.ChampionName)));
            }

            results.Add(new(slot.Leader, team.Count, true, null));
        }

        // Crystal's champion name lives inline in the party group itself (the
        // same physical bytes for a home-region or a visiting launch - the
        // base patch doesn't relocate this table), unlike GBA's fixed struct
        // field above.
        if (manifest.Format == ConnectRomFormat.Crystal
            && !string.IsNullOrWhiteSpace(champion.ChampionName)
            && TryGetCrystalNameOffset(manifest.Raw, out var nameOffset))
        {
            edits.Add((nameOffset, ChampionNameResolver.EncodeGen2TrainerName(champion.ChampionName)));
        }

        // Yellow's champion is the same character as the save's rival, so the
        // battle engine normally just prints wRivalName - correct by default
        // (no custom champion) with no edit needed. When a custom champion
        // exists, a dedicated flag+buffer added specifically for this override
        // (championPlaceholder.name) redirects GetTrainerName_ to it instead;
        // leaving the flag at 0 (never touched otherwise) keeps every other
        // rival battle and every <RIVAL>-code map text reading the save as before.
        if (manifest.Format == ConnectRomFormat.Yellow
            && !string.IsNullOrWhiteSpace(champion.ChampionName)
            && TryGetYellowNameOffsets(manifest.Raw, out var flagOffset, out var nameBufferOffset))
        {
            edits.Add((flagOffset, [1]));
            edits.Add((nameBufferOffset, ChampionNameResolver.EncodeGen1TrainerName(champion.ChampionName)));
        }

        // Yellow's rival battles carry hardcoded "SpecialTrainerMoves" that
        // stamp fixed moves onto specific party slots regardless of what
        // species occupies them - active in the vanilla ROM (a visiting launch
        // already has these disabled by the base patch's own IPS edits). Write
        // $FF at each entry's trainerNumberOffset to disable it, same as the
        // base patch does, so the champion's real moveset isn't overwritten.
        // Harmless (no-op) to repeat on an already-patched ROM.
        if (manifest.Format == ConnectRomFormat.Yellow)
        {
            foreach (var offset in GetYellowSpecialTrainerMoveOffsets(manifest.Raw))
            {
                edits.Add((offset, [0xFF]));
            }
        }

        return (edits, results);
    }

    private static bool TryGetCrystalNameOffset(JsonElement raw, out int offset)
    {
        offset = 0;
        if (raw.TryGetProperty("championPlaceholder", out var placeholder)
            && placeholder.TryGetProperty("party", out var party)
            && party.TryGetProperty("nameOffset", out var nameOffsetEl))
        {
            offset = nameOffsetEl.GetInt32();
            return true;
        }
        return false;
    }

    private static bool TryGetYellowNameOffsets(JsonElement raw, out int flagOffset, out int nameBufferOffset)
    {
        flagOffset = 0;
        nameBufferOffset = 0;
        if (raw.TryGetProperty("championPlaceholder", out var placeholder)
            && placeholder.TryGetProperty("name", out var name)
            && name.TryGetProperty("flagOffset", out var flagOffsetEl)
            && name.TryGetProperty("nameBufferOffset", out var nameBufferOffsetEl))
        {
            flagOffset = flagOffsetEl.GetInt32();
            nameBufferOffset = nameBufferOffsetEl.GetInt32();
            return true;
        }
        return false;
    }

    private static IEnumerable<int> GetYellowSpecialTrainerMoveOffsets(JsonElement raw)
    {
        if (!raw.TryGetProperty("homeRegionChampion", out var home)
            || !home.TryGetProperty("specialTrainerMoves", out var entries))
        {
            yield break;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.TryGetProperty("trainerNumberOffset", out var off))
            {
                yield return off.GetInt32();
            }
        }
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
