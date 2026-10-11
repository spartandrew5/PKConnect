using System.Text.Json;
using PKHeX.Core;
using PKConnect.Core.patching;
using PKVault.Core;
using Serilog;

namespace PKConnect.Core.formats;

/// <summary>
/// pokeemerald-expansion's Pokédex flags: two flat bit arrays in SaveBlock1,
/// <c>u8 dexSeen[NUM_DEX_FLAG_BYTES]; u8 dexCaught[NUM_DEX_FLAG_BYTES];</c>,
/// bit <c>natDex - 1</c> (<c>GetSetPokedexFlag</c>). Vanilla's SaveBlock2
/// Pokédex struct - what PKHeX's SAV3 dex reads - is dead in the expansion.
///
/// Where the arrays sit depends on the build's config, so the save alone
/// doesn't say. In order of trust:
///  1. <see cref="RecordBuild"/>: offsets read from the build's own .elf
///     (<c>GetSetPokedexFlag</c>'s literal pool holds both SaveBlock1 offsets),
///     captured whenever the launch patch runs against the ROM and persisted.
///     Exact - the only source flags are ever written through.
///  2. Otherwise, read-only: the one window of SaveBlock1 consistent with the
///     Pokémon the save owns (every owned species caught, caught a subset of
///     seen, no bits past #1025), else the current build's default layout if it
///     looks plausible.
/// </summary>
public sealed class ExpansionDexFlags
{
    /// <summary>Species PKVault shows for an expansion save: through Gen 5, the newest generation that can travel in.</summary>
    public const ushort MaxDexSpecies = ExpansionConverter.MaxOfficialSpecies;

    private const int NationalDexCount = 1025;

    // Layout of the user's current build (NUM_DEX_FLAG_BYTES = 129).
    private const int DefaultSeenOffset = 0x3598;
    private const int DefaultFlagBytes = 129;

    private readonly SAV3 save;
    private readonly int seenOffset;
    private readonly int caughtOffset;
    private readonly int flagBytes;

    /// <summary>True when the offsets came from the build's symbols; only then may flags be written.</summary>
    public bool IsExact { get; }

    private ExpansionDexFlags(SAV3 save, int seenOffset, int caughtOffset, int flagBytes, bool isExact)
    {
        this.save = save;
        this.seenOffset = seenOffset;
        this.caughtOffset = caughtOffset;
        this.flagBytes = flagBytes;
        IsExact = isExact;
    }

    public bool GetSeen(ushort natDex) => GetBit(seenOffset, natDex);
    public bool GetCaught(ushort natDex) => GetBit(caughtOffset, natDex);

    public void SetSeen(ushort natDex) => SetBit(seenOffset, natDex);
    public void SetCaught(ushort natDex) => SetBit(caughtOffset, natDex);

    public int CountSeen() => Count(seenOffset);
    public int CountCaught() => Count(caughtOffset);

    private bool GetBit(int offset, ushort natDex)
    {
        var bit = natDex - 1;
        return natDex > 0 && bit / 8 < flagBytes && (save.Large[offset + bit / 8] & (1 << (bit % 8))) != 0;
    }

    private void SetBit(int offset, ushort natDex)
    {
        var bit = natDex - 1;
        if (!IsExact || natDex == 0 || bit / 8 >= flagBytes)
        {
            return;
        }
        save.Large[offset + bit / 8] |= (byte)(1 << (bit % 8));
    }

    private int Count(int offset)
    {
        var count = 0;
        for (ushort n = 1; n <= MaxDexSpecies; n++)
        {
            if (GetBit(offset, n)) count++;
        }
        return count;
    }

    /// <param name="pokemon">The save's Pokémon, in the expansion's own format.</param>
    public static ExpansionDexFlags? For(SAV3 sav3, string gameId, IEnumerable<PKM> pokemon)
    {
        var large = sav3.Large;

        if (LoadRecordedBuild(gameId) is { } build
            && build.CaughtOffset + build.FlagBytes <= large.Length
            && IsPlausible(large, build.SeenOffset, build.CaughtOffset, build.FlagBytes))
        {
            return new(sav3, build.SeenOffset, build.CaughtOffset, build.FlagBytes, isExact: true);
        }

        var owned = pokemon
            .Where(pk => !pk.IsEgg && pk.Species is > 0 and <= NationalDexCount)
            .Select(pk => pk.Species)
            .ToHashSet();

        if (owned.Count > 0 && Search(large, owned) is { } found)
        {
            return new(sav3, found.Seen, found.Seen + found.Bytes, found.Bytes, isExact: false);
        }

        // Owned Pokémon aren't required to be flagged here: ones given through the
        // debug menu or traveled in by PKVault never set their dex bits. The
        // default is the Emerald build's layout only.
        return gameId == ExpansionSaveDetector.GameId
            && DefaultSeenOffset + 2 * DefaultFlagBytes <= large.Length
            && IsPlausible(large, DefaultSeenOffset, DefaultSeenOffset + DefaultFlagBytes, DefaultFlagBytes)
            ? new(sav3, DefaultSeenOffset, DefaultSeenOffset + DefaultFlagBytes, DefaultFlagBytes, isExact: false)
            : null;
    }

    private static bool HasBit(ReadOnlySpan<byte> large, int offset, ushort natDex) =>
        (large[offset + (natDex - 1) / 8] & (1 << ((natDex - 1) % 8))) != 0;

    /// <summary>Caught ⊆ seen, and nothing flagged past the National Dex (rules out 0xFF filler).</summary>
    private static bool IsPlausible(ReadOnlySpan<byte> large, int seen, int caught, int bytes)
    {
        for (var i = 0; i < bytes; i++)
        {
            var s = large[seen + i];
            var c = large[caught + i];
            if ((c & ~s) != 0)
            {
                return false;
            }
            for (var b = 0; b < 8; b++)
            {
                if (i * 8 + b >= NationalDexCount && ((s >> b) & 1) != 0)
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>The single SaveBlock1 window (adjacent seen/caught arrays) consistent with the owned species.</summary>
    private static (int Seen, int Bytes)? Search(ReadOnlySpan<byte> large, HashSet<ushort> owned)
    {
        var minBytes = (owned.Max() + 7) / 8;
        (int Seen, int Bytes)? match = null;
        for (var bytes = Math.Max(minBytes, 32); bytes <= 160; bytes++)
        {
            for (var seen = 0; seen + 2 * bytes <= large.Length; seen++)
            {
                var caught = seen + bytes;
                var ok = true;
                foreach (var n in owned)
                {
                    if (!HasBit(large, caught, n) || !HasBit(large, seen, n))
                    {
                        ok = false;
                        break;
                    }
                }
                if (!ok || !IsPlausible(large, seen, caught, bytes))
                {
                    continue;
                }
                if (match is not null)
                {
                    return null; // ambiguous - don't guess
                }
                match = (seen, bytes);
            }
        }
        return match;
    }

    // ---- build info recorded from the ROM's .elf ----

    private sealed record BuildInfo(string ElfPath, int SeenOffset, int CaughtOffset, int FlagBytes);

    private static readonly string BuildInfoPath =
        Path.Combine(SettingsService.AppDirectory, "config", "expansion-build.json");

    /// <summary>Recorded builds by expansion game id - each build (Emerald / FireRed / LeafGreen) has its own layout.</summary>
    private static Dictionary<string, BuildInfo>? cachedBuilds;
    private static readonly Lock buildLock = new();

    private static Dictionary<string, BuildInfo> LoadRecordedBuilds()
    {
        if (cachedBuilds is not null)
        {
            return cachedBuilds;
        }
        cachedBuilds = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(BuildInfoPath))
            {
                var json = File.ReadAllText(BuildInfoPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty(nameof(BuildInfo.ElfPath), out _))
                {
                    // First version of this file held a single (Emerald) build.
                    if (JsonSerializer.Deserialize<BuildInfo>(json) is { } single)
                        cachedBuilds[ExpansionSaveDetector.GameId] = single;
                }
                else if (JsonSerializer.Deserialize<Dictionary<string, BuildInfo>>(json) is { } builds)
                {
                    foreach (var (id, info) in builds)
                        cachedBuilds[id] = info;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[expansion-dex] couldn't read {Path}", BuildInfoPath);
        }
        return cachedBuilds;
    }

    private static BuildInfo? LoadRecordedBuild(string gameId)
    {
        lock (buildLock)
        {
            return LoadRecordedBuilds().GetValueOrDefault(gameId);
        }
    }

    /// <summary>
    /// Reads the dex flag offsets out of the build's <c>GetSetPokedexFlag</c>:
    /// its literal pool holds exactly two small SaveBlock1 offsets (dexSeen,
    /// dexCaught - adjacent, so their difference is NUM_DEX_FLAG_BYTES) beside
    /// the <c>gSaveBlock1Ptr</c> address. Persisted for every expansion save.
    /// </summary>
    public static void RecordBuild(string gameId, ElfSymbolTable elf, byte[] rom)
    {
        var fn = elf.Get("GetSetPokedexFlag");
        if (fn is null || fn.Address < Gba.RomBase)
        {
            Log.Warning("[expansion-dex] GetSetPokedexFlag not in {Elf}", elf.Path);
            return;
        }
        var start = (int)(fn.Address - Gba.RomBase);
        var end = Math.Min(rom.Length, start + (int)fn.Size);
        var offsets = new SortedSet<int>();
        for (var i = (start + 3) & ~3; i + 4 <= end; i += 4)
        {
            var v = Gba.ReadU32(rom, i);
            if (v is >= 0x100 and < 0x3E00)
            {
                offsets.Add((int)v);
            }
        }
        if (offsets.Count != 2 || offsets.Max - offsets.Min is < 32 or > 160)
        {
            Log.Warning("[expansion-dex] unexpected GetSetPokedexFlag literals: {Values}", string.Join(",", offsets.Select(o => o.ToString("X"))));
            return;
        }

        var info = new BuildInfo(elf.Path, offsets.Min, offsets.Max, offsets.Max - offsets.Min);
        lock (buildLock)
        {
            var builds = LoadRecordedBuilds();
            if (builds.GetValueOrDefault(gameId) == info)
            {
                return;
            }
            builds[gameId] = info;
            Directory.CreateDirectory(Path.GetDirectoryName(BuildInfoPath)!);
            File.WriteAllText(BuildInfoPath, JsonSerializer.Serialize(builds));
        }
        Log.Information("[expansion-dex] {Game}: recorded dexSeen=0x{Seen:X} dexCaught=0x{Caught:X} ({Bytes} bytes) from {Elf}",
            gameId, info.SeenOffset, info.CaughtOffset, info.FlagBytes, elf.Path);
    }
}
