using PKConnect.Core.champions;
using PKConnect.Core.formats;
using PKConnect.Core.nds;
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
///
/// That is the Game Boy and GBA games. The others differ in how they are patched, not in what
/// the player gets:
///  - Platinum, HeartGold, SoulSilver: one Connect build serves home and visiting trainers (it
///    reads the home region from the save), so its base patch is always applied;
///  - Black, White, Black 2, White 2: no base patch, the retail ROM's own data is edited;
///  - pokeemerald-expansion builds: the ROM is the player's own build, found through its .elf.
/// </summary>
public static class LaunchPatchService
{
    /// <summary>The patch file's content and extension, and what went into it.</summary>
    public sealed record LaunchPatch(byte[] Bytes, string Extension, Summary Summary);

    public sealed record Summary(
        bool IsVisiting, bool BasePatchApplied, int EditsApplied,
        int TeamsGenerated, int TeamsSkipped, int ChampionApplied, int ChampionSkipped, List<string> Warnings);

    public static bool IsSupportedGame(string gameId) =>
        BasePatchService.IsSupportedGame(gameId)
        || Gen4ConnectPatchService.IsConnectGame(gameId)
        || Gen5ConnectPatchService.IsConnectGame(gameId)
        || ExpansionSaveDetector.IsExpansionGameId(gameId);

    /// <summary>
    /// The patch for launching <paramref name="gameId"/> from <paramref name="vanillaRom"/>.
    /// <paramref name="champion"/> is the current champion of the game's region, if any.
    /// Throws <see cref="ArgumentException"/> for a game Connect doesn't patch, and
    /// <see cref="InvalidOperationException"/> (with a message for the player) when the ROM isn't
    /// the revision a patch was made for. <paramref name="romPath"/> is only needed for an
    /// expansion build, whose .elf is looked for beside the ROM.
    /// </summary>
    public static LaunchPatch Build(
        string gameId, byte[] vanillaRom, double? averageLevel, string? homeRegion,
        ChampionTeamDTO? champion = null, int? randomSeed = null, string? romPath = null)
    {
        if (!IsSupportedGame(gameId))
            throw new ArgumentException($"Unsupported game for launch patching: {gameId}");

        var isVisiting = ConnectRegions.IsVisiting(gameId, homeRegion);
        if (Gen4ConnectPatchService.IsConnectGame(gameId))
            return BuildGen4(gameId, vanillaRom, isVisiting, averageLevel, champion, randomSeed);
        if (Gen5ConnectPatchService.IsConnectGame(gameId))
            return BuildGen5(gameId, vanillaRom, isVisiting, averageLevel, champion, randomSeed);
        if (ExpansionSaveDetector.IsExpansionGameId(gameId))
            return BuildExpansion(gameId, vanillaRom, romPath, isVisiting, averageLevel, champion, randomSeed);

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

    private static LaunchPatch BuildGen4(
        string gameId, byte[] vanillaRom, bool isVisiting, double? averageLevel, ChampionTeamDTO? champion, int? randomSeed)
    {
        var basePatch = Gen4ConnectPatchService.Resolve(gameId, vanillaRom)
            ?? throw new InvalidOperationException(
                $"This ROM isn't the {gameId} revision the Connect patch was made for, or the patch is missing.");

        var edits = new List<(int Offset, byte[] Data)>();
        var warnings = new List<string>();
        int teamsGenerated = 0, teamsSkipped = 0, championApplied = 0, championSkipped = 0;

        if (isVisiting && averageLevel.HasValue)
        {
            var (gymEdits, gymResults) = Gen4ConnectPatchService.BuildGymEdits(basePatch, averageLevel.Value, randomSeed);
            edits.AddRange(gymEdits);
            teamsGenerated = gymResults.Count(r => r.Applied);
            teamsSkipped = gymResults.Count - teamsGenerated;
            warnings.AddRange(gymResults.Where(r => !r.Applied).Select(r => $"[gym:{r.Zone}] {r.Reason}"));
        }

        if (champion is not null)
        {
            var bonus = ChampionBattleConfigLoader.Load().ChampionLevelBonus;
            var (champEdits, champResults) = Gen4ConnectPatchService.BuildChampionEdits(basePatch, champion, averageLevel, bonus);
            edits.AddRange(champEdits);
            championApplied = champResults.Count(r => r.Applied);
            championSkipped = champResults.Count - championApplied;
            warnings.AddRange(champResults.Where(r => !r.Applied).Select(r => $"[champion:{r.Zone}] {r.Reason}"));
        }

        // Every edit is a same-size write on the patched image, so they are folded into the base
        // patch rather than diffing two 128MB ROMs.
        var bytes = BpsPatchService.OverlayEdits(vanillaRom, basePatch.Patch, basePatch.Patched, edits);
        return new(bytes, ".bps", new(
            isVisiting, true, edits.Count, teamsGenerated, teamsSkipped, championApplied, championSkipped, warnings));
    }

    private static LaunchPatch BuildGen5(
        string gameId, byte[] vanillaRom, bool isVisiting, double? averageLevel, ChampionTeamDTO? champion, int? randomSeed)
    {
        var bonus = ChampionBattleConfigLoader.Load().ChampionLevelBonus;
        var connect = Gen5ConnectPatchService.Build(
            gameId, vanillaRom, new NdsFilePlacer(vanillaRom), isVisiting, averageLevel, champion, bonus, randomSeed);

        var warnings = new List<string>();
        warnings.AddRange(connect.Gyms.Where(r => !r.Applied).Select(r => $"[gym:{r.Zone}] {r.Reason}"));
        warnings.AddRange(connect.Champion.Where(r => !r.Applied).Select(r => $"[champion:{r.Zone}] {r.Reason}"));
        warnings.AddRange(connect.Warnings);

        // Built from the edit list: the generic encoder would have to match across the whole ROM.
        var bytes = BpsPatchService.EncodeSparse(vanillaRom, connect.Edits);
        var teams = connect.Gyms.Count(r => r.Applied);
        var champs = connect.Champion.Count(r => r.Applied);
        return new(bytes, ".bps", new(
            isVisiting, false, connect.Edits.Count, teams, connect.Gyms.Count - teams, champs, connect.Champion.Count - champs, warnings));
    }

    private static LaunchPatch BuildExpansion(
        string gameId, byte[] vanillaRom, string? romPath, bool isVisiting, double? averageLevel,
        ChampionTeamDTO? champion, int? randomSeed)
    {
        if (romPath is null || ExpansionBuild.FindSymbols(gameId, romPath, vanillaRom) is not { } elf)
            throw new InvalidOperationException(ExpansionBuild.MissingSymbolsMessage);

        // Also remember where this build keeps its Pokédex flags - its saves don't say.
        ExpansionDexFlags.RecordBuild(gameId, elf, vanillaRom);

        var workRom = (byte[])vanillaRom.Clone();
        var warnings = new List<string>();
        int teamsGenerated = 0, teamsSkipped = 0, championApplied = 0, championSkipped = 0;

        // A build with the Connect port decides home or visiting when it runs, from the save; a
        // visiting launch only has the gym leaders' teams to fill in.
        var connectInfo = ExpansionConnectPatchService.ReadInfo(elf, vanillaRom);
        if (connectInfo is not null && isVisiting && averageLevel.HasValue)
        {
            var (gymEdits, gymResults) = ExpansionConnectPatchService.BuildGymEdits(gameId, vanillaRom, connectInfo, averageLevel.Value, randomSeed);
            IpsPatchService.ApplyInPlace(workRom, gymEdits);
            teamsGenerated = gymResults.Count(r => r.Applied);
            teamsSkipped = gymResults.Count - teamsGenerated;
            warnings.AddRange(gymResults.Where(r => !r.Applied).Select(r => $"[gym:{r.Gym}] {r.Reason}"));
        }

        if (champion is not null)
        {
            var allocator = new RomFreeSpaceAllocator(vanillaRom, ExpansionBuild.FreeSpaceStart(elf, vanillaRom));
            var (champEdits, champResults) = ExpansionChampionPatchService.BuildChampionEdits(
                gameId, vanillaRom, elf, averageLevel, champion, allocator);
            IpsPatchService.ApplyInPlace(workRom, champEdits);
            championApplied = champResults.Count(r => r.Applied);
            championSkipped = champResults.Count - championApplied;
            warnings.AddRange(champResults.Where(r => !r.Applied).Select(r => $"[champion:trainer {r.TrainerId}] {r.Reason}"));
        }

        // The sparse encoder needs edits that don't overlap; the difference of the two images is
        // exactly that. A .bps because the build fills the ROM past what an .ips can address.
        var diff = new List<(int Offset, byte[] Data)>();
        for (var i = 0; i < workRom.Length; i++)
        {
            if (workRom[i] == vanillaRom[i])
                continue;
            var start = i;
            while (i < workRom.Length && workRom[i] != vanillaRom[i])
                i++;
            diff.Add((start, workRom[start..i]));
        }

        return new(BpsPatchService.EncodeSparse(vanillaRom, diff), ".bps", new(
            isVisiting, false, diff.Count, teamsGenerated, teamsSkipped, championApplied, championSkipped, warnings));
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

        var patch = Build(gameId, File.ReadAllBytes(romPath), averageLevel, homeRegion, champion, romPath: romPath);
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