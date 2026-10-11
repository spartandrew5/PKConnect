using PKConnect.Core.patching;
using System.Security.Cryptography;
using System.Text.Json;

namespace PKConnect.Core.launch;

public enum ConnectRomFormat
{
    Gba,
    Crystal,
    Yellow,
}

/// <summary>
/// How a base patch is stored on disk and reconstructed. Ips is the default
/// (a handful of small edits, cheap to keep as an explicit offset/data list).
/// Bps is for patches whose vanilla-to-patched diff is a relink-wide address
/// shift (currently Ruby/Sapphire - see BpsPatchService) rather than a small
/// number of edits: the source and target ROMs are byte-diffed directly via
/// copy/delta commands instead of listing every changed range.
/// </summary>
public enum PatchStorageFormat
{
    Ips,
    Bps,
}

/// <summary>
/// One opponent party the base patch has repointed into a reserved buffer.
/// </summary>
public record ConnectLandingZone(
    string Leader,
    // GBA gTrainers[] id (matched against gym-team-pools.json trainerIds). Null on GB.
    int? TrainerId,
    // ROM file offset of the first mon record.
    int PartyOffset,
    // GBA only: the trainer struct's partySize byte.
    int? PartySizeOffset,
    int Capacity,
    int RecordSize,
    // GB only: the byte written right after the last record.
    int? Terminator,
    // Yellow rival variants: later variants are found by walking this party, so
    // it must always hold exactly Capacity mons.
    bool FixedSize
);

public record ConnectBaseManifest(
    string GameId,
    ConnectRomFormat Format,
    int RomSize,
    string PatchedSha1,
    string? VanillaSha1,
    List<ConnectLandingZone> Gyms,
    List<ConnectLandingZone> ChampionSlots,
    // The full manifest (flags, vars, save offsets) for save preparation.
    JsonElement Raw
);

/// <summary>
/// <paramref name="Edits"/> is empty for Bps-stored patches - their diff isn't
/// tracked as discrete ranges, so <see cref="ApplyToVanilla"/> reconstructs
/// the patched ROM directly from <paramref name="BpsBytes"/> instead, and
/// nothing needs adding to a launch's combined edit list for it (the final
/// per-launch patch is re-diffed against the fully patched ROM regardless -
/// see LaunchPatchService).
/// </summary>
public record ConnectBasePatch(
    ConnectBaseManifest Manifest,
    PatchStorageFormat StorageFormat,
    List<(int Offset, byte[] Data)> Edits,
    byte[]? BpsBytes = null);

/// <summary>
/// Loads the static, decomp-built Connect base patches (connect-data/base-patches:
/// <c>&lt;decomp&gt;.ips</c> + <c>&lt;decomp&gt;_connect.json</c>, from one build) and applies
/// them to the user's vanilla ROM in memory. The base patch turns a game into a
/// boss rush for a *visiting* trainer; a trainer's home-region games launch
/// vanilla. Nothing here is hardcoded: every landing-zone offset comes from the
/// manifest, and a patch is only trusted if applying it reproduces the exact
/// build the manifest describes.
/// </summary>
public static class BasePatchService
{
    // Most games only circulate as one revision in practice, so a single stem
    // is enough; Ruby/Sapphire genuinely have multiple dumped revisions
    // (software version byte at ROM offset 0xBC) with different code layout,
    // so they list one stem per revision this decomp has been built for -
    // Resolve() below picks whichever one's vanillaSha1 actually matches.
    private static readonly Dictionary<string, (string[] Stems, ConnectRomFormat Format, PatchStorageFormat Storage)> Games = new()
    {
        ["emerald"] = (["pokeemerald"], ConnectRomFormat.Gba, PatchStorageFormat.Ips),
        ["firered"] = (["pokefirered"], ConnectRomFormat.Gba, PatchStorageFormat.Ips),
        ["leafgreen"] = (["pokeleafgreen"], ConnectRomFormat.Gba, PatchStorageFormat.Ips),
        ["crystal"] = (["pokecrystal"], ConnectRomFormat.Crystal, PatchStorageFormat.Ips),
        ["yellow"] = (["pokeyellow"], ConnectRomFormat.Yellow, PatchStorageFormat.Ips),
        // Ruby and Sapphire are not here yet. Their base patches shift most absolute-address
        // literals in the ROM (the decomps' linker scripts have no safe "extra" section), which is
        // what the Bps storage format and the per-revision stems are for; the patches themselves
        // still need finishing before they are added back.
    };

    private static readonly Dictionary<string, List<ConnectBasePatch>> Cache = [];
    private static readonly Lock Gate = new();

    public static string DataDir => Path.Combine(AppContext.BaseDirectory, "connect-data", "base-patches");

    public static IReadOnlyCollection<string> SupportedGames => Games.Keys;

    public static bool IsSupportedGame(string gameId) => Games.ContainsKey(gameId);

    /// <summary>How the final per-launch patch mGBA auto-loads should be written for this game.</summary>
    public static PatchStorageFormat OutputFormat(string gameId) =>
        Games.TryGetValue(gameId, out var game) ? game.Storage : PatchStorageFormat.Ips;

    /// <summary>All revision variants this game has a base patch built for.</summary>
    public static List<ConnectBasePatch> LoadAll(string gameId)
    {
        if (!Games.TryGetValue(gameId, out var game))
        {
            throw new ArgumentException($"No Connect base patch for game '{gameId}'");
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(gameId, out var cached))
            {
                return cached;
            }

            var patches = game.Stems.Select(stem =>
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataDir, $"{stem}_connect.json")));
                var parsedManifest = ParseManifest(gameId, game.Format, manifest.RootElement);

                return game.Storage switch
                {
                    PatchStorageFormat.Bps => new ConnectBasePatch(
                        parsedManifest,
                        PatchStorageFormat.Bps,
                        [],
                        File.ReadAllBytes(Path.Combine(DataDir, $"{stem}.bps"))),
                    _ => new ConnectBasePatch(
                        parsedManifest,
                        PatchStorageFormat.Ips,
                        IpsPatchService.Parse(File.ReadAllBytes(Path.Combine(DataDir, $"{stem}.ips")))),
                };
            }).ToList();

            Cache[gameId] = patches;
            return patches;
        }
    }

    /// <summary>
    /// A single-revision convenience wrapper for callers that don't need to
    /// disambiguate (games with exactly one stem).
    /// </summary>
    public static ConnectBasePatch Load(string gameId) => LoadAll(gameId)[0];

    /// <summary>
    /// Picks whichever revision variant's vanillaSha1 matches
    /// <paramref name="vanillaRom"/>. Throws <see cref="InvalidOperationException"/>
    /// listing every revision this game supports when none match.
    /// </summary>
    public static ConnectBasePatch Resolve(string gameId, byte[] vanillaRom)
    {
        var candidates = LoadAll(gameId);
        var actualSha1 = Sha1(vanillaRom);

        var match = candidates.FirstOrDefault(p =>
            p.Manifest.VanillaSha1 is { } expected && expected.Equals(actualSha1, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        var known = string.Join(", ", candidates.Select(p => p.Manifest.VanillaSha1 ?? "?"));
        throw new InvalidOperationException(
            $"The {gameId} ROM (sha1 {actualSha1}) doesn't match any vanilla revision the Connect base patch was built against (expected one of: {known}). Use an unmodified US ROM.");
    }

    /// <summary>
    /// Returns a patched copy of <paramref name="vanillaRom"/>. Throws
    /// <see cref="InvalidOperationException"/> with a user-facing message when
    /// the ROM isn't the vanilla revision the patch was built against.
    /// </summary>
    public static byte[] ApplyToVanilla(ConnectBasePatch patch, byte[] vanillaRom)
    {
        var manifest = patch.Manifest;

        if (vanillaRom.Length != manifest.RomSize)
        {
            throw new InvalidOperationException(
                $"The {manifest.GameId} ROM is {vanillaRom.Length} bytes, but the Connect base patch expects {manifest.RomSize}. Use an unmodified US ROM.");
        }

        if (manifest.VanillaSha1 is { } expectedVanilla
            && !Sha1(vanillaRom).Equals(expectedVanilla, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The {manifest.GameId} ROM isn't the vanilla revision the Connect base patch was built against (sha1 {Sha1(vanillaRom)}, expected {expectedVanilla}). Use BasePatchService.Resolve() to pick the matching revision first.");
        }

        byte[] patched;
        if (patch.StorageFormat == PatchStorageFormat.Bps)
        {
            patched = BpsPatchService.Decode(vanillaRom, patch.BpsBytes!);
        }
        else
        {
            patched = (byte[])vanillaRom.Clone();
            IpsPatchService.ApplyInPlace(patched, patch.Edits);
        }

        var actual = Sha1(patched);
        if (!actual.Equals(manifest.PatchedSha1, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Applying the {manifest.GameId} Connect base patch produced sha1 {actual}, expected {manifest.PatchedSha1}. The ROM isn't the vanilla revision the patch was built against.");
        }

        return patched;
    }

    private static string Sha1(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(data));

    private static ConnectBaseManifest ParseManifest(string gameId, ConnectRomFormat format, JsonElement root)
    {
        var rom = root.GetProperty("rom");
        var romSize = rom.GetProperty("size").GetInt32();
        var champion = root.GetProperty("champion");
        var championName = champion.GetProperty("name").GetString() ?? "CHAMPION";

        List<ConnectLandingZone> gyms = [];
        List<ConnectLandingZone> championSlots = [];

        switch (format)
        {
            case ConnectRomFormat.Gba:
                {
                    var layout = root.GetProperty("layout");
                    var capacity = layout.GetProperty("partyCapacity").GetInt32();
                    var recordSize = layout.GetProperty("trainerMonSize").GetInt32();

                    ConnectLandingZone GbaZone(string name, JsonElement e) => new(
                        name,
                        e.GetProperty("trainerId").GetInt32(),
                        e.GetProperty("partyOffset").GetInt32(),
                        e.GetProperty("partySizeOffset").GetInt32(),
                        capacity,
                        recordSize,
                        Terminator: null,
                        FixedSize: false);

                    gyms.AddRange(root.GetProperty("gyms").EnumerateArray()
                        .Select(g => GbaZone(g.GetProperty("leader").GetString()!, g)));

                    if (champion.TryGetProperty("variants", out var variants))
                    {
                        // FireRed: one champion battle per rival starter.
                        championSlots.AddRange(variants.EnumerateArray()
                            .Select(v => GbaZone($"{championName} ({v.GetProperty("starter").GetString()})", v)));
                    }
                    else
                    {
                        championSlots.Add(GbaZone(championName, champion));
                    }
                    break;
                }

            case ConnectRomFormat.Crystal:
                gyms.AddRange(root.GetProperty("gyms").EnumerateArray()
                    .Select(g => GbZone(g.GetProperty("leader").GetString()!, g, fixedSize: false)));
                championSlots.Add(GbZone(championName, champion, fixedSize: false));
                break;

            case ConnectRomFormat.Yellow:
                gyms.AddRange(root.GetProperty("gyms").EnumerateArray()
                    .Select(g => GbZone(g.GetProperty("leader").GetString()!, g, fixedSize: false)));
                championSlots.AddRange(champion.GetProperty("variants").EnumerateArray()
                    .Select(v => GbZone(
                        $"{championName} (starter {v.GetProperty("rivalStarter").GetInt32()})",
                        v,
                        fixedSize: v.TryGetProperty("laterVariantsDependOnSize", out var dependent) && dependent.GetBoolean())));
                break;
        }

        foreach (var zone in gyms.Concat(championSlots))
        {
            var end = zone.PartyOffset + zone.Capacity * zone.RecordSize + (zone.Terminator.HasValue ? 1 : 0);
            if (zone.PartyOffset <= 0 || zone.Capacity <= 0 || end > romSize
                || zone.PartySizeOffset is { } sizeOffset && (sizeOffset <= 0 || sizeOffset >= romSize))
            {
                throw new InvalidDataException($"{gameId} manifest: landing zone for {zone.Leader} is outside the ROM");
            }
        }

        return new ConnectBaseManifest(
            gameId,
            format,
            romSize,
            rom.GetProperty("sha1").GetString()!,
            rom.TryGetProperty("vanillaSha1", out var vanilla) ? vanilla.GetString() : null,
            gyms,
            championSlots,
            root.Clone());
    }

    private static ConnectLandingZone GbZone(string name, JsonElement e, bool fixedSize) => new(
        name,
        TrainerId: null,
        e.GetProperty("zoneOffset").GetInt32(),
        PartySizeOffset: null,
        e.GetProperty("capacity").GetInt32(),
        e.GetProperty("recordSize").GetInt32(),
        e.GetProperty("terminator").GetInt32(),
        fixedSize);
}
