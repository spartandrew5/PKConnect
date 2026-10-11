using System.Security.Cryptography;
using System.Text.Json;
using PKConnect.Core.launch;
using PKConnect.Core.patching;

namespace PKConnect.Core.champions;

/// <summary>
/// Loads and applies the always-on GB/GBC "champion placeholder" micro-patch
/// (pokecrystal_home.ips/.json, pokeyellow_home.ips/.json) — separate from the
/// Connect base patch, and applied on EVERY launch of that game, including a
/// pure home-region one that never touches the Connect patch at all.
///
/// The Connect base patch's own placeholder NPC (<see cref="GbChampionGraphicsPatchService"/>'s
/// "championPlaceholder") only exists once that patch is applied, which never
/// happens on a home-region launch — so a home-region champion battle used to
/// mean touching the real character's own shared resources. This micro-patch
/// gives every launch its own dedicated, non-shared overworld sprite +
/// front-pic slot for the champion, so a home-region launch gets the same
/// isolation the Connect patch's placeholder gives a visiting one. As of the
/// pokecrystal team unifying both patches' champion party zone onto the same
/// address/format, team + name writing needs no special-casing either -
/// <see cref="ConnectTeamPatchService.BuildChampionEdits"/> handles both
/// contexts identically (see PatchRoute's home-region GB branch).
/// </summary>
public static class GbHomeChampionPatchService
{
    public record GbHomePatch(JsonElement Raw, List<(int Offset, byte[] Data)> Edits, string? VanillaSha1, string PatchedSha1);

    private static readonly Dictionary<string, string> Stems = new()
    {
        ["crystal"] = "pokecrystal_home",
        ["yellow"] = "pokeyellow_home",
    };

    private static readonly Dictionary<string, GbHomePatch> Cache = [];
    private static readonly Lock Gate = new();

    public static bool IsSupportedGame(string gameId) => Stems.ContainsKey(gameId);

    public static GbHomePatch Load(string gameId)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(gameId, out var cached))
            {
                return cached;
            }

            var stem = Stems[gameId];
            var ips = File.ReadAllBytes(Path.Combine(BasePatchService.DataDir, $"{stem}.ips"));
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(BasePatchService.DataDir, $"{stem}.json")));
            var root = doc.RootElement.Clone();
            var rom = root.GetProperty("rom");

            var patch = new GbHomePatch(
                root,
                IpsPatchService.Parse(ips),
                rom.TryGetProperty("vanillaSha1", out var v1) ? v1.GetString()
                    : rom.TryGetProperty("baseSha1", out var v2) ? v2.GetString() : null,
                rom.GetProperty("sha1").GetString()!);
            Cache[gameId] = patch;
            return patch;
        }
    }

    /// <summary>
    /// Returns a patched copy of <paramref name="vanillaRom"/>. Throws
    /// <see cref="InvalidOperationException"/> when the ROM isn't the vanilla
    /// revision this micro-patch was built against.
    /// </summary>
    public static byte[] ApplyToVanilla(string gameId, GbHomePatch patch, byte[] vanillaRom)
    {
        if (patch.VanillaSha1 is { } expectedVanilla && !Sha1(vanillaRom).Equals(expectedVanilla, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The {gameId} ROM isn't the vanilla revision the champion-placeholder home patch was built against (sha1 {Sha1(vanillaRom)}, expected {expectedVanilla}).");
        }

        var patched = (byte[])vanillaRom.Clone();
        IpsPatchService.ApplyInPlace(patched, patch.Edits);

        var actual = Sha1(patched);
        if (!actual.Equals(patch.PatchedSha1, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Applying the {gameId} champion-placeholder home patch produced sha1 {actual}, expected {patch.PatchedSha1}. The ROM isn't the vanilla revision the patch was built against.");
        }

        return patched;
    }

    private static string Sha1(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(data));
}
