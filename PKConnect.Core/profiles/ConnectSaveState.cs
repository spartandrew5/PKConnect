using PKConnect.Core.regions;
using PKHeX.Core;

namespace PKConnect.Core.profiles;

/// <summary>
/// What a Connect base patch reads from the save to know who is playing: the trainer's home
/// region, how many regions they have become champion of, and one flag per region conquered.
/// Gym leaders' greetings and the visitor's story depend on it.
///
/// The numbers come from each base patch's manifest (connect-data/base-patches/*_connect.json,
/// "flags" / "vars" / "flagFields"). The GBA games keep the two values in script variables; the
/// Game Boy games have no spare variables, so there they are 4-bit fields made of event flags.
/// </summary>
public static class ConnectSaveState
{
    /// <summary>The region numbers every base patch uses.</summary>
    private static readonly Dictionary<string, int> RegionIds = new()
    {
        ["kanto"] = 1, ["johto"] = 2, ["hoenn"] = 3, ["sinnoh"] = 4, ["unova"] = 5,
    };

    /// <summary>Script variables are numbered from here in the GBA games; the save holds them from index 0.</summary>
    private const int Gen3VarBase = 0x4000;
    private const int FieldBits = 4;

    /// <summary>
    /// Where a game keeps the state. <paramref name="HomeRegion"/> and <paramref name="RegionsCleared"/>
    /// are variable numbers (GBA) or the first flag of a 4-bit field, lowest bit first (Game Boy).
    /// </summary>
    private sealed record Layout(bool Variables, Dictionary<string, int> ChampionFlags, int HomeRegion, int RegionsCleared);

    private static readonly Layout Emerald = new(true, new() { ["kanto"] = 2304, ["johto"] = 2305, ["hoenn"] = 2306 }, 16632, 16631);
    private static readonly Layout FireRedLeafGreen = new(true, new() { ["kanto"] = 2243, ["johto"] = 2244, ["hoenn"] = 2245 }, 16632, 16631);
    private static readonly Layout Crystal = new(false, new() { ["kanto"] = 512, ["johto"] = 513, ["hoenn"] = 514 }, 525, 521);
    private static readonly Layout Yellow = new(false, new() { ["kanto"] = 448, ["johto"] = 449, ["hoenn"] = 450 }, 476, 472);

    private static Layout? LayoutFor(SaveFile save) => save switch
    {
        SAV3E => Emerald,
        SAV3FRLG => FireRedLeafGreen,
        SAV2 { Version: GameVersion.C } => Crystal,
        SAV1 { Version: GameVersion.YW } => Yellow,
        _ => null,
    };

    /// <summary>Whether the save's game keeps Connect state at all.</summary>
    public static bool IsSupported(SaveFile save) => LayoutFor(save) is not null;

    /// <summary>
    /// Writes the state into <paramref name="save"/> (in memory). Conquered regions are only ever
    /// added: a flag already set stays set. Returns whether anything changed.
    /// </summary>
    public static bool Apply(SaveFile save, string? homeRegion, IEnumerable<string> conqueredRegions)
    {
        if (LayoutFor(save) is not { } layout || save is not IEventFlagArray flags)
            return false;

        var conquered = conqueredRegions.Select(ConnectRegions.Normalize).OfType<string>().Distinct().ToList();
        var changed = false;

        foreach (var region in conquered)
        {
            if (layout.ChampionFlags.TryGetValue(region, out var flag) && flag < flags.EventFlagCount && !flags.GetEventFlag(flag))
            {
                flags.SetEventFlag(flag, true);
                changed = true;
            }
        }

        var home = ConnectRegions.Normalize(homeRegion) is { } slug && RegionIds.TryGetValue(slug, out var id) ? id : 0;
        var cleared = Math.Min(conquered.Count, (1 << FieldBits) - 1);

        if (layout.Variables)
        {
            if (save is not IEventWorkArray<ushort> work)
                return changed;
            changed |= SetWork(work, layout.HomeRegion - Gen3VarBase, home);
            changed |= SetWork(work, layout.RegionsCleared - Gen3VarBase, cleared);
        }
        else
        {
            changed |= SetField(flags, layout.HomeRegion, home);
            changed |= SetField(flags, layout.RegionsCleared, cleared);
        }
        return changed;
    }

    /// <summary>The home region and regions-cleared count as the save has them; null for a game without Connect state.</summary>
    public static (int HomeRegionId, int RegionsCleared)? Read(SaveFile save)
    {
        if (LayoutFor(save) is not { } layout || save is not IEventFlagArray flags)
            return null;
        if (layout.Variables)
            return save is IEventWorkArray<ushort> work
                ? (work.GetWork(layout.HomeRegion - Gen3VarBase), work.GetWork(layout.RegionsCleared - Gen3VarBase))
                : null;
        return (GetField(flags, layout.HomeRegion), GetField(flags, layout.RegionsCleared));
    }

    private static bool SetWork(IEventWorkArray<ushort> work, int index, int value)
    {
        if (index < 0 || index >= work.EventWorkCount || work.GetWork(index) == value)
            return false;
        work.SetWork(index, (ushort)value);
        return true;
    }

    private static int GetField(IEventFlagArray flags, int firstFlag)
    {
        var value = 0;
        for (var bit = 0; bit < FieldBits; bit++)
            if (firstFlag + bit < flags.EventFlagCount && flags.GetEventFlag(firstFlag + bit))
                value |= 1 << bit;
        return value;
    }

    private static bool SetField(IEventFlagArray flags, int firstFlag, int value)
    {
        if (GetField(flags, firstFlag) == value)
            return false;
        for (var bit = 0; bit < FieldBits; bit++)
            if (firstFlag + bit < flags.EventFlagCount)
                flags.SetEventFlag(firstFlag + bit, ((value >> bit) & 1) == 1);
        return true;
    }
}