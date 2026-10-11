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
/// at home gets an empty patch - which still has to be written, to replace whatever a visit
/// left behind.
/// </summary>
public static class LaunchPatchService
{
    /// <summary>The patch file's content and extension, and what went into it.</summary>
    public sealed record LaunchPatch(byte[] Bytes, string Extension, Summary Summary);

    public sealed record Summary(
        bool IsVisiting, bool BasePatchApplied, int EditsApplied, int TeamsGenerated, int TeamsSkipped, List<string> Warnings);

    public static bool IsSupportedGame(string gameId) => BasePatchService.IsSupportedGame(gameId);

    /// <summary>
    /// The patch for launching <paramref name="gameId"/> from <paramref name="vanillaRom"/>.
    /// Throws <see cref="ArgumentException"/> for a game Connect doesn't patch, and
    /// <see cref="InvalidOperationException"/> (with a message for the player) when the ROM isn't
    /// the revision the base patch was made for.
    /// </summary>
    public static LaunchPatch Build(string gameId, byte[] vanillaRom, double? averageLevel, string? homeRegion, int? randomSeed = null)
    {
        if (!IsSupportedGame(gameId))
            throw new ArgumentException($"Unsupported game for launch patching: {gameId}");

        var isVisiting = ConnectRegions.IsVisiting(gameId, homeRegion);
        var workRom = vanillaRom;
        var edits = new List<(int Offset, byte[] Data)>();
        var warnings = new List<string>();
        int teamsGenerated = 0, teamsSkipped = 0;

        if (isVisiting)
        {
            var basePatch = BasePatchService.Resolve(gameId, vanillaRom);
            workRom = BasePatchService.ApplyToVanilla(basePatch, vanillaRom);
            edits.AddRange(basePatch.Edits);

            if (averageLevel.HasValue)
            {
                var (gymEdits, gymResults) = ConnectTeamPatchService.BuildGymEdits(basePatch, averageLevel.Value, randomSeed);
                IpsPatchService.ApplyInPlace(workRom, gymEdits);
                edits.AddRange(gymEdits);
                teamsGenerated = gymResults.Count(r => r.Applied);
                teamsSkipped = gymResults.Count - teamsGenerated;
                warnings.AddRange(gymResults.Where(r => !r.Applied).Select(r => $"[gym:{r.Zone}] {r.Reason}"));
            }
        }

        var (bytes, extension) = BasePatchService.OutputFormat(gameId) == PatchStorageFormat.Bps
            ? (BpsPatchService.Encode(vanillaRom, workRom), ".bps")
            : (IpsPatchService.BuildIps(edits, workRom), ".ips");

        return new(bytes, extension, new(isVisiting, isVisiting, edits.Count, teamsGenerated, teamsSkipped, warnings));
    }

    /// <summary>
    /// Builds the patch and writes it beside the ROM, removing a patch of the other format that an
    /// earlier version may have left there (the emulator would load whichever it finds).
    /// </summary>
    public static PatchResultDTO WriteFor(string gameId, string romPath, double? averageLevel, string? homeRegion)
    {
        if (!File.Exists(romPath))
            throw new ArgumentException($"ROM not found: {romPath}");

        var patch = Build(gameId, File.ReadAllBytes(romPath), averageLevel, homeRegion);
        var patchPath = Path.ChangeExtension(romPath, patch.Extension);
        var stale = Path.ChangeExtension(romPath, patch.Extension == ".ips" ? ".bps" : ".ips");
        if (File.Exists(stale))
            File.Delete(stale);
        File.WriteAllBytes(patchPath, patch.Bytes);

        var s = patch.Summary;
        Log.Information(
            "[launch-patch] {Game} visiting={Visiting}: gyms {Teams} applied/{TeamsSkipped} skipped -> {Path} ({Bytes} B)",
            gameId, s.IsVisiting, s.TeamsGenerated, s.TeamsSkipped, patchPath, patch.Bytes.Length);

        return new(
            Success: true,
            EditsApplied: s.EditsApplied,
            PatchPath: patchPath,
            Warnings: s.Warnings,
            TeamsGenerated: s.TeamsGenerated,
            TeamsSkipped: s.TeamsSkipped,
            BasePatchApplied: s.BasePatchApplied,
            HomeRegionLaunch: !s.IsVisiting);
    }
}