using PKConnect.Core.formats;
using PKHeX.Core;
using PKVault.Core;

namespace PKConnect.Core.profiles;

/// <summary>
/// Gym badges, by region. A trainer's badges belong to the region, not to one save: two Kanto
/// badges earned in Yellow are two Kanto badges in the same trainer's FireRed. This knows where
/// each game keeps the eight badges of each region it covers.
///
///  - Game Boy games: one "beat the leader" flag per leader - at once the badge record and the
///    story flag - plus the badge bits.
///  - GBA games: a "badge get" flag and a separate "leader defeated" story flag. Both are kept in
///    step; the engine's own rematch flag for the trainer never is, so a region a trainer
///    travels to still plays as a run of gym battles.
///  - DS games: read only. Their flags haven't been worked out, so their badges are shown and
///    counted but never written from another save.
/// </summary>
public static class RegionBadges
{
    public const int Count = 8;

    /// <summary>Where one region's badges are in one kind of save. Flags are null for a read-only layout.</summary>
    private sealed record Layout(string Region, int[]? BadgeFlags, int[]? StoryFlags, int BitBase);

    private static int[] Run(int first) => [.. Enumerable.Range(first, Count)];

    // pokered: EVENT_BEAT_BROCK ... EVENT_BEAT_VIRIDIAN_GYM_GIOVANNI, in badge order.
    private static readonly Layout Gen1Kanto = new("kanto", [119, 191, 359, 425, 601, 865, 665, 81], null, 0);
    // pokecrystal: EVENT_BEAT_FALKNER ... (Chuck's flag comes after Jasmine's), then Kanto's leaders.
    private static readonly Layout Gen2Johto = new("johto", [1213, 1214, 1215, 1216, 1218, 1217, 1219, 1220], null, 0);
    private static readonly Layout Gen2Kanto = new("kanto", Run(1221), null, 8);
    // FLAG_BADGE01_GET... and FLAG_DEFEATED_*.
    private static readonly Layout FireRedLeafGreen = new("kanto", Run(2080), Run(1200), 0);
    private static readonly Layout Emerald = new("hoenn", Run(2151), Run(1264), 0);

    private static readonly Layout Sinnoh = new("sinnoh", null, null, 0);
    private static readonly Layout HgssJohto = new("johto", null, null, 0);
    private static readonly Layout HgssKanto = new("kanto", null, null, 8);
    private static readonly Layout Unova = new("unova", null, null, 0);

    private static bool IsExpansionFrlg(SaveFile save) =>
        SaveExtension.For(save) is ExpansionSaveExtension extension && ExpansionSaveLayout.IsFrlg(extension.GameId);

    // The region the game is set in comes first.
    private static Layout[] LayoutsFor(SaveFile save) => save switch
    {
        SAV1 => [Gen1Kanto],
        SAV2 => [Gen2Johto, Gen2Kanto],
        SAV3FRLG => [FireRedLeafGreen],
        SAV3E when IsExpansionFrlg(save) => [FireRedLeafGreen],
        SAV3E => [Emerald],
        SAV4Pt => [Sinnoh],
        SAV4HGSS => [HgssJohto, HgssKanto],
        SAV5 => [Unova],
        _ => [],
    };

    /// <summary>The regions whose badges this save holds, the game's own region first.</summary>
    public static IReadOnlyList<string> RegionsFor(SaveFile save) => [.. LayoutsFor(save).Select(layout => layout.Region)];

    /// <summary>Whether a region's badges can be written into this save from elsewhere.</summary>
    public static bool CanWrite(SaveFile save, string region) =>
        LayoutsFor(save).FirstOrDefault(layout => layout.Region == region) is { BadgeFlags: not null };

    /// <summary>The region's eight badges as the save has them; null when the save's game doesn't cover the region.</summary>
    public static bool[]? Read(SaveFile save, string region)
    {
        if (LayoutsFor(save).FirstOrDefault(l => l.Region == region) is not { } layout)
            return null;

        if (layout.BadgeFlags is { } badgeFlags && save is IEventFlagArray flags)
            return [.. badgeFlags.Select(flag => flag < flags.EventFlagCount && flags.GetEventFlag(flag))];

        var bits = GetBits(save);
        return [.. Enumerable.Range(0, Count).Select(i => ((bits >> (layout.BitBase + i)) & 1) == 1)];
    }

    /// <summary>
    /// Makes the save's badges of the region exactly <paramref name="target"/>: sets and clears
    /// the badge flags, the story flags where they are separate, and the badge bits. Returns
    /// whether anything changed. Does nothing for a region the save can't be written for.
    /// </summary>
    public static bool Apply(SaveFile save, string region, IReadOnlyList<bool> target)
    {
        if (LayoutsFor(save).FirstOrDefault(l => l.Region == region) is not { BadgeFlags: { } badgeFlags } layout
            || save is not IEventFlagArray flags)
            return false;

        var changed = false;
        var bits = GetBits(save);
        var newBits = bits;
        for (var i = 0; i < Count; i++)
        {
            var value = i < target.Count && target[i];
            changed |= SetFlag(flags, badgeFlags[i], value);
            if (layout.StoryFlags is { } storyFlags)
                changed |= SetFlag(flags, storyFlags[i], value);

            var bit = 1 << (layout.BitBase + i);
            newBits = value ? newBits | bit : newBits & ~bit;
        }

        // The Game Boy games keep the bits apart from the flags; the GBA games read them from the flags.
        if (save is SAV1 or SAV2 && newBits != bits)
        {
            SetBits(save, newBits);
            changed = true;
        }
        return changed;
    }

    private static bool SetFlag(IEventFlagArray flags, int flag, bool value)
    {
        if (flag < 0 || flag >= flags.EventFlagCount || flags.GetEventFlag(flag) == value)
            return false;
        flags.SetEventFlag(flag, value);
        return true;
    }

    private static int GetBits(SaveFile save) => save switch
    {
        SAV1 sav1 => sav1.Badges,
        SAV2 sav2 => sav2.Badges,
        SAV4HGSS hgss => hgss.Badges | (hgss.Badges16 << 8),
        SAV4 sav4 => sav4.Badges,
        SAV5 sav5 => sav5.Misc.Badges,
        _ => 0,
    };

    private static void SetBits(SaveFile save, int bits)
    {
        switch (save)
        {
            case SAV1 sav1: sav1.Badges = (byte)bits; break;
            case SAV2 sav2: sav2.Badges = (ushort)bits; break;
        }
    }

    /// <summary>What reconciling a save with its trainer's badges came to.</summary>
    /// <param name="SaveChanged">The save's own flags were changed (and it has to be written).</param>
    /// <param name="Ledger">The regions whose entry in the trainer's ledger changes, with their new state.</param>
    /// <param name="Snapshot">The regions whose snapshot for this save changes.</param>
    public sealed record Reconciliation(bool SaveChanged, Dictionary<string, bool[]> Ledger, Dictionary<string, bool[]> Snapshot);

    /// <summary>
    /// Brings a save and its trainer's badges into step. The trainer's ledger is what counts:
    ///  - a region the trainer has no entry for yet is taken from this save;
    ///  - a save never reconciled before takes the ledger as it is;
    ///  - otherwise a badge the save has gained since its last snapshot is progress made in the
    ///    game and joins the ledger, and then the save is made to match the ledger exactly - which
    ///    is also how a badge taken away elsewhere goes away here.
    /// A region the save can't be written for only ever adds its badges to the ledger.
    /// </summary>
    public static Reconciliation Reconcile(
        SaveFile save,
        IReadOnlyDictionary<string, bool[]> ledger,
        IReadOnlyDictionary<string, bool[]>? snapshot)
    {
        var saveChanged = false;
        Dictionary<string, bool[]> ledgerUpdate = [];
        Dictionary<string, bool[]> snapshotUpdate = [];

        foreach (var region in RegionsFor(save))
        {
            if (Read(save, region) is not { } own)
                continue;

            ledger.TryGetValue(region, out var ledgerRegion);
            bool[]? snap = null;
            snapshot?.TryGetValue(region, out snap);

            bool[] target;
            if (!CanWrite(save, region))
                target = Merge(ledgerRegion, own);
            else if (ledgerRegion is null)
                target = own;
            else if (snap is null)
                target = Normalize(ledgerRegion);
            else
                target = [.. Enumerable.Range(0, Count).Select(i => At(ledgerRegion, i) || (own[i] && !At(snap, i)))];

            if (CanWrite(save, region) && !target.SequenceEqual(own))
                saveChanged |= Apply(save, region, target);
            if (ledgerRegion is null || !target.SequenceEqual(Normalize(ledgerRegion)))
                ledgerUpdate[region] = target;
            if (snap is null || !target.SequenceEqual(Normalize(snap)))
                snapshotUpdate[region] = target;
        }

        return new(saveChanged, ledgerUpdate, snapshotUpdate);
    }

    private static bool At(bool[] slots, int index) => index < slots.Length && slots[index];

    private static bool[] Normalize(bool[] slots) => [.. Enumerable.Range(0, Count).Select(i => At(slots, i))];

    private static bool[] Merge(bool[]? a, bool[] b) => [.. Enumerable.Range(0, Count).Select(i => (a is not null && At(a, i)) || At(b, i))];
}