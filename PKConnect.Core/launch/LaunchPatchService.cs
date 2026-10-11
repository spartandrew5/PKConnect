using PKConnect.Core.champions;
using PKConnect.Core.patching;
using PKConnect.Core.regions;
using Serilog;

namespace PKConnect.Core.launch;

/// <summary>
/// Builds the patch a game is launched with. The emulator loads a patch file that sits next to
/// the ROM under the ROM's name, so the ROM itself is never changed.
///
/// A visiting trainer gets the game's Connect base patch (see <see cref="BasePatchService"/>)
/// and, when their party's average level is known, a fresh team for each gym leader. A trainer
/// at home plays the game as it is. Either way the League's champion is the region's current
/// champion, when there is one. The patch is written even when it is empty, to replace whatever
/// an earlier launch left behind.
/// </summary>
public static class LaunchPatchService
{
    /// <summary>The patch file's content and extension, and what went into it.</summary>
    public sealed record LaunchPatch(byte[] Bytes, string Extension, Summary Summary);

    public sealed record Summary(
        bool IsVisiting, bool BasePatchApplied, int EditsApplied,
        int TeamsGenerated, int TeamsSkipped, int ChampionApplied, int ChampionSkipped, List<string> Warnings);

    public static bool IsSupportedGame(string gameId) => BasePatchService.IsSupportedGame(gameId);

    /// <summary>
    /// The patch for launching <paramref name="gameId"/> from <paramref name="vanillaRom"/>.
    /// <paramref name="champion"/> is the current champion of the game's region, if any.
    /// Throws <see cref="ArgumentException"/> for a game Connect doesn't patch, and
    /// <see cref="InvalidOperationException"/> (with a message for the player) when the ROM isn't
    /// the revision a patch was made for.
    /// </summary>
    public static LaunchPatch Build(
        string gameId, byte[] vanillaRom, double? averageLevel, string? homeRegion,
        ChampionTeamDTO? champion = null, int? randomSeed = null)
    {
        if (!IsSupportedGame(gameId))
            throw new ArgumentException($"Unsupported game for launch patching: {gameId}");

        var isVisiting = ConnectRegions.IsVisiting(gameId, homeRegion);
        var workRom = (byte[])vanillaRom.Clone();
        var edits = new List<(int Offset, byte[] Data)>();
        var warnings = new List<string>();
        int teamsGenerated = 0, teamsSkipped = 0, championApplied = 0, championSkipped = 0;

        void Commit(List<(int Offset, byte[] Data)> more)
        {
            IpsPatchService.ApplyInPlace(workRom, more);
            edits.AddRange(more);
        }

        void CommitChampion(List<(int Offset, byte[] Data)> more, IEnumerable<(string Zone, bool Applied, string? Reason)> results)
        {
            Commit(more);
            foreach (var (zone, applied, reason) in results)
            {
                if (applied) championApplied++;
                else { championSkipped++; warnings.Add($"[champion:{zone}] {reason}"); }
            }
        }

        var championLevelBonus = ChampionBattleConfigLoader.Load().ChampionLevelBonus;

        if (isVisiting)
        {
            var basePatch = BasePatchService.Resolve(gameId, vanillaRom);
            workRom = BasePatchService.ApplyToVanilla(basePatch, vanillaRom);
            edits.AddRange(basePatch.Edits);

            if (averageLevel.HasValue)
            {
                var (gymEdits, gymResults) = ConnectTeamPatchService.BuildGymEdits(basePatch, averageLevel.Value, randomSeed);
                Commit(gymEdits);
                teamsGenerated = gymResults.Count(r => r.Applied);
                teamsSkipped = gymResults.Count - teamsGenerated;
                warnings.AddRange(gymResults.Where(r => !r.Applied).Select(r => $"[gym:{r.Zone}] {r.Reason}"));
            }

            if (champion is not null)
            {
                // The base patch already points the champion's battles at its landing zones.
                var (champEdits, champResults) = ConnectTeamPatchService.BuildChampionEdits(basePatch, champion, averageLevel, championLevelBonus);
                CommitChampion(champEdits, champResults.Select(r => (r.Zone, r.Applied, r.Reason)));
            }
        }
        else if (champion is not null)
        {
            if (ChampionTeamPatchService.IsSupportedGame(gameId))
            {
                // GBA, at home: the team goes into free space of the vanilla ROM and the champion's
                // trainer is pointed at it.
                var (champEdits, champResults) = ChampionTeamPatchService.BuildChampionEdits(
                    gameId, workRom, averageLevel, champion, new RomFreeSpaceAllocator(workRom));
                CommitChampion(champEdits, champResults.Select(r => ($"trainer {r.TrainerId}", r.Applied, r.Reason)));
            }
            else
            {
                // Game Boy, at home: a small patch of its own gives the champion a party zone at
                // the same place the Connect base patch has it, so the same writer serves.
                if (GbHomeChampionPatchService.IsSupportedGame(gameId))
                {
                    var homePatch = GbHomeChampionPatchService.Load(gameId);
                    workRom = GbHomeChampionPatchService.ApplyToVanilla(gameId, homePatch, vanillaRom);
                    edits.AddRange(homePatch.Edits);
                }
                var (champEdits, champResults) = ConnectTeamPatchService.BuildChampionEdits(
                    BasePatchService.Load(gameId), champion, averageLevel, championLevelBonus);
                CommitChampion(champEdits, champResults.Select(r => (r.Zone, r.Applied, r.Reason)));
            }
        }

        var (bytes, extension) = BasePatchService.OutputFormat(gameId) == PatchStorageFormat.Bps
            ? (BpsPatchService.Encode(vanillaRom, workRom), ".bps")
            : (IpsPatchService.BuildIps(edits, workRom), ".ips");

        return new(bytes, extension, new(
            isVisiting, isVisiting, edits.Count, teamsGenerated, teamsSkipped, championApplied, championSkipped, warnings));
    }

    /// <summary>
    /// Builds the patch and writes it beside the ROM, removing a patch of the other format that an
    /// earlier version may have left there (the emulator would load whichever it finds).
    /// </summary>
    public static PatchResultDTO WriteFor(
        string gameId, string romPath, double? averageLevel, string? homeRegion, ChampionTeamDTO? champion = null)
    {
        if (!File.Exists(romPath))
            throw new ArgumentException($"ROM not found: {romPath}");

        var patch = Build(gameId, File.ReadAllBytes(romPath), averageLevel, homeRegion, champion);
        var patchPath = Path.ChangeExtension(romPath, patch.Extension);
        var stale = Path.ChangeExtension(romPath, patch.Extension == ".ips" ? ".bps" : ".ips");
        if (File.Exists(stale))
            File.Delete(stale);
        File.WriteAllBytes(patchPath, patch.Bytes);

        var s = patch.Summary;
        Log.Information(
            "[launch-patch] {Game} visiting={Visiting}: gyms {Teams} applied/{TeamsSkipped} skipped, champion {Champ}/{ChampSkipped} -> {Path} ({Bytes} B)",
            gameId, s.IsVisiting, s.TeamsGenerated, s.TeamsSkipped, s.ChampionApplied, s.ChampionSkipped, patchPath, patch.Bytes.Length);

        return new(
            Success: true,
            EditsApplied: s.EditsApplied,
            PatchPath: patchPath,
            Warnings: s.Warnings,
            TeamsGenerated: s.TeamsGenerated,
            TeamsSkipped: s.TeamsSkipped,
            ChampionApplied: s.ChampionApplied,
            ChampionSkipped: s.ChampionSkipped,
            BasePatchApplied: s.BasePatchApplied,
            HomeRegionLaunch: !s.IsVisiting);
    }
}