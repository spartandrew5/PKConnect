using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PKHeX.Core;
using PKConnect.Core.formats;
using PKConnect.Core.champions;
using PKConnect.Core.nds;
using PKConnect.Core.patching;
using Serilog;

namespace PKConnect.Core.launch;

/// <summary>
/// Pokemon Connect for the Gen 4 decomp builds (NDS): Platinum (pret/pokeplatinum) and HeartGold /
/// SoulSilver (pret/pokeheartgold, one source for both), each with runtime home-region branching (see the decomp's
/// include/constants/connect.h): one patched ROM serves home and visiting trainers alike. Each ships
/// as a vanilla -> Connect .bps plus a manifest (launcher/data/base-patches/&lt;game&gt;_connect.json)
/// naming where the landing zones live.
///
/// At launch the base patch is applied in memory, then every per-launch change is a same-size
/// overwrite inside NitroFS archives (located through <see cref="NitroFs"/>/<see cref="NarcArchive"/>):
///  - gym leaders (visiting launches): the Connect gym trainers' fixed 6-mon, moves + items
///    parties (trpoke) and party sizes (trdata);
///  - champion (every launch): the League champion's own full parties and name (NPC trainer names bank).
/// The result is folded back into one patch against vanilla by <see cref="BpsPatchService.OverlayEdits"/>,
/// so no 128MB diff ever runs at launch.
/// </summary>
public static class Gen4ConnectPatchService
{
    /// <summary>The Connect builds: game id -> PKHeX version (item names) and the game's last move id.</summary>
    private static readonly Dictionary<string, (GameVersion Version, int MaxMove)> Games = new()
    {
        ["platinum"] = (GameVersion.Pt, Gen4AddedSpecies.MaxMove),
        ["heartgold"] = (GameVersion.HG, Gen4AddedSpecies.MaxMove),
        ["soulsilver"] = (GameVersion.SS, Gen4AddedSpecies.MaxMove),
    };

    public static bool IsConnectGame(string gameId) => Games.ContainsKey(gameId);

    public sealed record PatchEntry(
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("vanillaSha1")] string VanillaSha1,
        [property: JsonPropertyName("targetSha1")] string? TargetSha1);

    public sealed record Manifest(
        [property: JsonPropertyName("patches")] List<PatchEntry> Patches,
        [property: JsonPropertyName("trdataPath")] string TrdataPath,
        [property: JsonPropertyName("trpokePath")] string TrpokePath,
        [property: JsonPropertyName("messagePath")] string MessagePath,
        [property: JsonPropertyName("trainerNamesBank")] int TrainerNamesBank,
        [property: JsonPropertyName("gymTrainerIds")] int[] GymTrainerIds,
        [property: JsonPropertyName("championTrainerIds")] int[] ChampionTrainerIds,
        [property: JsonPropertyName("partyCapacity")] int PartyCapacity);

    public sealed record BasePatch(string GameId, Manifest Manifest, byte[] Patch, byte[] Patched);

    public record ZoneResult(string Zone, int TeamSize, bool Applied, string? Reason);

    private const int MonSize = 18; // TrainerMonWithMovesAndItem / TRPOKE_ITEM_CUSTMOVES
    private const int TrdataPartySize = 3;
    private const int TrdataMonDataType = 0;
    private const int MonDataTypeMovesAndItem = 3;
    private const int MaxDv = 255;

    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "connect-data", "base-patches");

    public static Manifest? LoadManifest(string gameId)
    {
        var path = Path.Combine(DataDir, $"{gameId}_connect.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Log.Warning("[gen4-connect] bad manifest {Path}: {Error}", path, ex.Message);
            return null;
        }
    }

    /// <summary>The game's Connect base patch for this exact vanilla ROM applied in memory, or null.</summary>
    public static BasePatch? Resolve(string gameId, byte[] vanillaRom)
    {
        if (!IsConnectGame(gameId) || LoadManifest(gameId) is not { } manifest) return null;
        var sha1 = Convert.ToHexString(SHA1.HashData(vanillaRom)).ToLowerInvariant();
        var entry = manifest.Patches.FirstOrDefault(p => p.VanillaSha1.Equals(sha1, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;

        var patchPath = Path.Combine(DataDir, entry.File);
        if (!File.Exists(patchPath)) return null;
        var patch = File.ReadAllBytes(patchPath);
        return new BasePatch(gameId, manifest, patch, BpsPatchService.Decode(vanillaRom, patch));
    }

    /// <summary>A freshly randomized team per gym leader, level-scaled on the traveling party.</summary>
    public static (List<(int Offset, byte[] Data)> Edits, List<ZoneResult> Results) BuildGymEdits(
        BasePatch basePatch, double averageLevel, int? randomSeed = null)
    {
        var edits = new List<(int Offset, byte[] Data)>();
        var results = new List<ZoneResult>();
        var m = basePatch.Manifest;
        var random = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
        var config = GymTeamPoolConfigLoader.Load();
        var variance = (int)Math.Round(config.DefaultLevelVariance);

        for (var gym = 0; gym < m.GymTrainerIds.Length; gym++)
        {
            var trainerId = m.GymTrainerIds[gym];
            var leader = config.Leaders.FirstOrDefault(l => l.TrainerIds.TryGetValue(basePatch.GameId, out var id) && id == trainerId);
            if (leader is null || leader.Pool.Count == 0)
            {
                results.Add(new($"gym {gym + 1}", 0, false, "No gym team pool for this leader; the vanilla team is kept"));
                continue;
            }

            var size = Math.Clamp(leader.TeamSize, 1, Math.Min(leader.Pool.Count, m.PartyCapacity));
            var mons = leader.Pool.OrderBy(_ => random.Next()).Take(size)
                .Select(p => new PartyMon(p.Species, Level(averageLevel, variance, random), p.Item, p.Moves))
                .ToList();

            var reason = WriteParty(basePatch, trainerId, mons, edits);
            results.Add(new(leader.DisplayName, reason is null ? mons.Count : 0, reason is null, reason));
        }
        return (edits, results);
    }

    /// <summary>
    /// The region's current champion into the League champion's battles (first and rematch) and name
    /// slot, fitted to the game (dex #493 / move #467) by <see cref="ChampionTeamSubstitution"/>.
    /// </summary>
    public static (List<(int Offset, byte[] Data)> Edits, List<ZoneResult> Results) BuildChampionEdits(
        BasePatch basePatch, ChampionTeamDTO champion, double? averageLevel, int championLevelBonus)
    {
        var edits = new List<(int Offset, byte[] Data)>();
        var results = new List<ZoneResult>();
        var m = basePatch.Manifest;
        var (version, maxMove) = Games[basePatch.GameId];

        var strings = GameInfo.GetStrings("en");
        var itemTable = strings.GetItemStrings(EntityContext.Gen4, version);
        var team = ChampionTeamSubstitution.Adapt(basePatch.GameId, champion.Team).Take(m.PartyCapacity).ToList();
        if (team.Count == 0)
        {
            results.AddRange(m.ChampionTrainerIds.Select(id => new ZoneResult($"champion {id}", 0, false, "No champion Pokemon exist in this game")));
            return (edits, results);
        }

        var teamAvg = team.Average(t => t.Level);
        var shift = averageLevel.HasValue ? averageLevel.Value + championLevelBonus - teamAvg : 0;
        var mons = team.Select(t => new PartyMon(
                t.Species,
                Math.Clamp((int)Math.Round(t.Level + shift), 2, 100),
                IndexOf(itemTable, t.HeldItem),
                (t.Moves ?? []).Select(n => IndexOf(strings.Move, n)).Where(id => id > 0 && id <= maxMove).Take(4).ToArray()))
            .ToList();

        foreach (var trainerId in m.ChampionTrainerIds)
        {
            var reason = WriteParty(basePatch, trainerId, mons, edits);
            if (reason is null && !string.IsNullOrWhiteSpace(champion.ChampionName))
            {
                reason = WriteTrainerName(basePatch, trainerId, champion.ChampionName, edits);
            }
            results.Add(new($"champion {trainerId}", reason is null ? mons.Count : 0, reason is null, reason));
        }
        return (edits, results);
    }

    private sealed record PartyMon(int Species, int Level, int Item, IReadOnlyList<int> Moves);

    /// <summary>Writes a party over the trainer's trpoke member (in place) and its trdata party size.</summary>
    private static string? WriteParty(BasePatch basePatch, int trainerId, List<PartyMon> mons, List<(int Offset, byte[] Data)> edits)
    {
        var rom = basePatch.Patched;
        var m = basePatch.Manifest;
        if (Member(rom, m.TrdataPath, trainerId) is not { } trdata || Member(rom, m.TrpokePath, trainerId) is not { } trpoke)
            return "trainer archives not found in the patched ROM";
        if (rom[trdata.Offset + TrdataMonDataType] != MonDataTypeMovesAndItem || trpoke.Length < m.PartyCapacity * MonSize)
            return "trainer isn't a 6-mon moves + items landing zone in this build";

        var party = new byte[m.PartyCapacity * MonSize];
        for (var i = 0; i < mons.Count && i < m.PartyCapacity; i++)
        {
            var mon = mons[i];
            var o = i * MonSize;
            WriteU16(party, o + 0, MaxDv);
            WriteU16(party, o + 2, (ushort)mon.Level);
            WriteU16(party, o + 4, (ushort)mon.Species);
            WriteU16(party, o + 6, (ushort)mon.Item);
            for (var k = 0; k < 4; k++)
                WriteU16(party, o + 8 + k * 2, (ushort)(k < mon.Moves.Count ? mon.Moves[k] : 0));
            WriteU16(party, o + 16, 0); // ball seal / capsule
        }
        edits.Add((trpoke.Offset, party));
        edits.Add((trdata.Offset + TrdataPartySize, [(byte)Math.Min(mons.Count, m.PartyCapacity)]));
        return null;
    }

    private static string? WriteTrainerName(BasePatch basePatch, int trainerId, string name, List<(int Offset, byte[] Data)> edits)
    {
        var rom = basePatch.Patched;
        if (Member(rom, basePatch.Manifest.MessagePath, basePatch.Manifest.TrainerNamesBank) is not { } bank)
            return "trainer names bank not found";
        var bankBytes = rom.AsSpan(bank.Offset, bank.Length);
        if (Gen4MessageBank.EncodeReplacement(bankBytes, trainerId, name) is not { } replacement)
            return "trainer name slot not found";
        edits.Add((bank.Offset + replacement.OffsetInBank, replacement.Data));
        return null;
    }

    /// <summary>Absolute ROM range of member <paramref name="index"/> of the NARC at <paramref name="narcPath"/>.</summary>
    private static (int Offset, int Length)? Member(byte[] rom, string narcPath, int index)
    {
        if (NitroFs.ResolveFile(rom, narcPath) is not { } file) return null;
        var narc = rom.AsSpan(file.Offset, file.Length).ToArray();
        try
        {
            var (offset, length) = NarcArchive.GetMemberRange(narc, index);
            return (file.Offset + offset, length);
        }
        catch
        {
            return null;
        }
    }

    private static int Level(double average, int variance, Random random) =>
        Math.Clamp((int)Math.Round(average + random.Next(-variance, variance + 1)), 2, 100);

    private static int IndexOf(IReadOnlyList<string> table, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        for (var i = 1; i < table.Count; i++)
            if (string.Equals(table[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    private static void WriteU16(byte[] b, int o, ushort v)
    {
        b[o] = (byte)v;
        b[o + 1] = (byte)(v >> 8);
    }
}
