using PKHeX.Core;

namespace PKConnect.Core.champions;

/// <summary>
/// Fits a stored champion team into a game whose dex/move range is smaller than the one the
/// title was won in (e.g. a pokeemerald-expansion champion with Electivire and Close Combat,
/// challenged in vanilla Emerald). The stored team is untouched - this only shapes the party
/// written into that one game's ROM. Each out-of-range member is replaced, in order, by:
///
///  1. its own evolution line: the nearest in-range pre-evolution, evolved forward as far as
///     the game allows (Gallade -> Kirlia -> Gardevoir, Electivire -> Electabuzz); babies
///     go forward instead (Munchlax -> Snorlax);
///  2. the region's evolved starters and box legend that share a type with it;
///  3. strong regional-dex Pokemon that share a type with it;
///  4. any remaining starter/legend, then any remaining strong regional Pokemon.
///
/// No species appears twice. Moves the game lacks (or that a replacement species can't learn
/// by level-up) are dropped and the empty slots filled with its latest level-up moves.
/// </summary>
public static class ChampionTeamSubstitution
{
    private sealed record Target(
        int MaxSpecies,
        int MaxMove,
        Func<ushort, Learnset?> Learnset,
        ushort[] Headliners,
        ushort[] Regional
    );

    // Evolved starters, then the box legend.
    private static readonly ushort[] HoennStarters = [254, 257, 260];
    private static readonly ushort[] KantoStarters = [3, 6, 9];
    private static readonly ushort[] JohtoStarters = [154, 157, 160];

    // Strongest first; broad type coverage so a type match usually exists.
    private static readonly ushort[] HoennStrong =
    [
        373, 376, 350, 282, 330, 306, 365, 359, 272, 286, 310, 342, 344, 348, 346, 334,
        275, 289, 297, 323, 319, 277, 324, 356, 354, 362, 357, 308, 227, 169, 317, 291,
    ];
    private static readonly ushort[] KantoStrong =
    [
        149, 94, 65, 59, 130, 143, 131, 68, 142, 135, 103, 112, 76, 121, 34, 31,
        71, 123, 127, 128, 91, 99, 124, 82, 51, 18, 45, 73, 49, 78,
    ];
    private static readonly ushort[] SinnohStarters = [389, 392, 395];
    private static readonly ushort[] SinnohStrong =
    [
        445, 448, 468, 442, 407, 130, 398, 405, 461, 473, 475, 282, 462, 464, 466, 467,
        478, 477, 430, 429, 437, 452, 450, 465, 469, 470, 471, 472, 474, 460, 454, 419,
    ];
    private static readonly ushort[] JohtoStrong =
    [
        248, 149, 214, 227, 230, 212, 197, 196, 229, 181, 208, 169, 94, 200, 221, 171,
        232, 217, 130, 68, 59, 241, 186, 205, 226, 215, 178, 195, 189, 182,
    ];

    // HeartGold/SoulSilver's Johto dex, with its Gen 4 evolutions.
    private static readonly ushort[] JohtoStrongGen4 =
    [
        248, 149, 445, 214, 461, 227, 230, 212, 197, 196, 229, 468, 181, 208, 473, 169,
        429, 430, 94, 462, 474, 472, 466, 467, 171, 232, 217, 130, 68, 59, 241, 186,
    ];

    private static readonly ushort[] UnovaStarters = [497, 500, 503];
    private static readonly ushort[] UnovaStrong =
    [
        635, 612, 637, 609, 530, 598, 553, 625, 534, 579, 576, 560, 604, 621, 584, 596,
        589, 617, 623, 628, 630, 526, 545, 549, 555, 563, 571, 593, 601, 614, 620, 632,
    ];

    // The Gen 4 Connect builds have Gen 5's species and moves too (see Gen4AddedSpecies): a champion's
    // Gen 5 Pokémon goes in as it is, and its moves are judged by Gen 5's own learnset.
    private const int Gen4ConnectMaxSpecies = 649;
    private const int Gen4ConnectMaxMove = 559; // the last Gen 5 move

    private static Learnset? Gen4PtLearnset(ushort s)
        => s > 493 ? LearnSource5B2W2.Instance.GetLearnset(s, 0) : LearnSource4Pt.Instance.GetLearnset(s, 0);
    private static Learnset? Gen4HgSsLearnset(ushort s)
        => s > 493 ? LearnSource5B2W2.Instance.GetLearnset(s, 0) : LearnSource4HGSS.Instance.GetLearnset(s, 0);
    private static Learnset? Gen5BwLearnset(ushort s) => LearnSource5BW.Instance.GetLearnset(s, 0);
    private static Learnset? Gen3Learnset(ushort s) => LearnSource3E.Instance.GetLearnset(s, 0);
    private static Learnset? Gen2Learnset(ushort s) => LearnSource2C.Instance.GetLearnset(s, 0);
    private static Learnset? Gen1Learnset(ushort s) => LearnSource1YW.Instance.GetLearnset(s, 0);

    private static Target? GetTarget(string gameId) => gameId switch
    {
        "platinum" => new(Gen4ConnectMaxSpecies, Gen4ConnectMaxMove, Gen4PtLearnset, [.. SinnohStarters, 487], SinnohStrong),
        "heartgold" => new(Gen4ConnectMaxSpecies, Gen4ConnectMaxMove, Gen4HgSsLearnset, [.. JohtoStarters, 250], JohtoStrongGen4),
        "soulsilver" => new(Gen4ConnectMaxSpecies, Gen4ConnectMaxMove, Gen4HgSsLearnset, [.. JohtoStarters, 249], JohtoStrongGen4),
        "black" => new(649, 559, Gen5BwLearnset, [.. UnovaStarters, 643], UnovaStrong),
        "white" => new(649, 559, Gen5BwLearnset, [.. UnovaStarters, 644], UnovaStrong),
        "black2" or "white2" => new(649, 559, s => LearnSource5B2W2.Instance.GetLearnset(s, 0), [.. UnovaStarters, 646], UnovaStrong),
        "emerald" => new(386, 354, Gen3Learnset, [.. HoennStarters, 384], HoennStrong),
        "ruby" => new(386, 354, Gen3Learnset, [.. HoennStarters, 383], HoennStrong),
        "sapphire" => new(386, 354, Gen3Learnset, [.. HoennStarters, 382], HoennStrong),
        "firered" or "leafgreen" => new(386, 354, Gen3Learnset, [.. KantoStarters, 150], KantoStrong),
        "crystal" => new(251, 251, Gen2Learnset, [.. JohtoStarters, 245], JohtoStrong),
        "gold" => new(251, 251, Gen2Learnset, [.. JohtoStarters, 250], JohtoStrong),
        "silver" => new(251, 251, Gen2Learnset, [.. JohtoStarters, 249], JohtoStrong),
        "red" or "blue" or "yellow" => new(151, 165, Gen1Learnset, [.. KantoStarters, 150], KantoStrong),
        _ => null,
    };

    public static List<ChampionMonDTO> Adapt(string gameId, IReadOnlyList<ChampionMonDTO>? team)
    {
        var mons = (team ?? []).Where(m => m.Species > 0).ToList();
        var target = GetTarget(gameId);
        if (target == null || mons.Count == 0)
        {
            return mons;
        }

        var result = new ChampionMonDTO?[mons.Count];
        var used = new HashSet<ushort>();

        // In-range members keep their slot first, so a substitute never takes their species.
        for (var i = 0; i < mons.Count; i++)
        {
            var species = (ushort)mons[i].Species;
            if (species <= target.MaxSpecies && used.Add(species))
            {
                result[i] = WithMoves(mons[i], species, target, speciesChanged: false);
            }
        }

        for (var i = 0; i < mons.Count; i++)
        {
            if (result[i] != null)
            {
                continue;
            }
            var original = (ushort)mons[i].Species;
            var originalTypes = GetModernTypes(original);

            var replacement = FromEvolutionLine(original, originalTypes, target, used)
                ?? FromPool(target.Headliners, originalTypes, used, requireTypeMatch: true)
                ?? FromPool(target.Regional, originalTypes, used, requireTypeMatch: true)
                ?? FromPool(target.Headliners, originalTypes, used, requireTypeMatch: false)
                ?? FromPool(target.Regional, originalTypes, used, requireTypeMatch: false);

            if (replacement is not { } species)
            {
                continue;
            }
            used.Add(species);
            result[i] = WithMoves(mons[i] with { Species = species, Nickname = null }, species, target, speciesChanged: true);
        }

        return result.OfType<ChampionMonDTO>().ToList();
    }

    private static ushort? FromEvolutionLine(ushort original, (byte, byte) originalTypes, Target target, HashSet<ushort> used)
    {
        // Nearest in-range ancestor = the one with the most ancestors of its own.
        var ancestor = GetPreEvolutions(original)
            .Where(s => s <= target.MaxSpecies)
            .OrderByDescending(s => GetPreEvolutions(s).Count)
            .Cast<ushort?>()
            .FirstOrDefault();

        List<ushort> candidates;
        if (ancestor is { } start)
        {
            candidates = FinalInRangeForms(start, target.MaxSpecies);
        }
        else
        {
            // A newer baby (Munchlax, Bonsly, Happiny...) - its line continues forward.
            candidates = FinalInRangeForms(original, target.MaxSpecies).Where(s => s != original).ToList();
        }

        return candidates
            .Where(s => !used.Contains(s))
            .OrderByDescending(s => TypeOverlap(originalTypes, GetGen3Types(s)))
            .Cast<ushort?>()
            .FirstOrDefault();
    }

    private static ushort? FromPool(ushort[] pool, (byte, byte) originalTypes, HashSet<ushort> used, bool requireTypeMatch) =>
        pool
            .Where(s => !used.Contains(s))
            .Select((s, order) => (Species: s, Order: order, Overlap: TypeOverlap(originalTypes, GetGen3Types(s))))
            .Where(c => !requireTypeMatch || c.Overlap > 0)
            .OrderByDescending(c => c.Overlap)
            .ThenBy(c => c.Order)
            .Select(c => (ushort?)c.Species)
            .FirstOrDefault();

    /// <summary>
    /// Evolves <paramref name="start"/> forward through every branch, returning the in-range
    /// members with no further in-range evolution (<paramref name="start"/> itself if none).
    /// </summary>
    private static List<ushort> FinalInRangeForms(ushort start, int maxSpecies)
    {
        var finals = new List<ushort>();
        var queue = new Queue<ushort>([start]);
        var seen = new HashSet<ushort> { start };
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var next = GetEvolutions(current).Where(s => seen.Add(s)).ToList();
            var inRange = next.Where(s => s <= maxSpecies).ToList();
            foreach (var s in next)
            {
                queue.Enqueue(s);
            }
            if (current <= maxSpecies && inRange.Count == 0)
            {
                finals.Add(current);
            }
        }
        return finals;
    }

    // Each generation's tree only lists species present in that game, so the newest tree
    // with an answer wins (Kingambit is Gen 9 only, Wyrdeer Legends: Arceus, Obstagoon Gen 8).
    private static readonly EvolutionTree[] Trees =
    [
        EvolutionTree.Evolves9, EvolutionTree.Evolves8a, EvolutionTree.Evolves8, EvolutionTree.Evolves7,
    ];

    private static List<ushort> GetPreEvolutions(ushort species)
    {
        foreach (var tree in Trees)
        {
            var pre = tree.Reverse.GetPreEvolutions(species, 0).Select(p => p.Item1).Distinct().ToList();
            if (pre.Count > 0)
            {
                return pre;
            }
        }
        return [];
    }

    private static List<ushort> GetEvolutions(ushort species)
    {
        foreach (var tree in Trees)
        {
            var next = tree.Forward.GetEvolutions(species, 0).Select(p => p.Item1).Distinct().ToList();
            if (next.Count > 0)
            {
                return next;
            }
        }
        return [];
    }

    private static int TypeOverlap((byte A, byte B) original, (byte A, byte B) candidate)
    {
        var have = new HashSet<byte> { candidate.A, candidate.B };
        return new HashSet<byte> { original.A, original.B }.Count(have.Contains);
    }

    // Candidates' types as their target game sees them (no Fairy before Gen 6); Gen 4's own
    // species past the Gen 3 table use the modern one (they gained no types since).
    private static (byte, byte) GetGen3Types(ushort species)
    {
        if (species > 386)
            return GetModernTypes(species);
        var pi = PersonalTable.E[species];
        return (pi.Type1, pi.Type2);
    }

    // PKHeX's newest table that carries the species (types are the modern MoveType ids).
    private static (byte, byte) GetModernTypes(ushort species)
    {
        IPersonalInfo pi = species switch
        {
            <= 807 => PersonalTable.USUM[species],
            <= 898 => PersonalTable.SWSH[species],
            <= 905 => PersonalTable.LA[species],
            _ => PersonalTable.SV[species],
        };
        return (pi.Type1, pi.Type2);
    }

    private static ChampionMonDTO WithMoves(ChampionMonDTO mon, ushort species, Target target, bool speciesChanged)
    {
        var moveNames = GameInfo.GetStrings("en").Move;
        var learnset = target.Learnset(species);

        var moves = new List<ushort>();
        foreach (var name in mon.Moves ?? [])
        {
            var id = IndexOfMove(moveNames, name);
            if (id <= 0 || id > target.MaxMove || moves.Contains(id))
            {
                continue;
            }
            // A substitute keeps only what it could have learned itself.
            if (speciesChanged && learnset?.GetIsLearn(id) != true)
            {
                continue;
            }
            moves.Add(id);
        }

        if (moves.Count < 4 && learnset != null)
        {
            var level = (byte)Math.Clamp(mon.Level, 1, 100);
            foreach (var id in learnset.GetBaseEggMoves(level).ToArray().Reverse())
            {
                if (moves.Count >= 4)
                {
                    break;
                }
                if (id > 0 && id <= target.MaxMove && !moves.Contains(id))
                {
                    moves.Add(id);
                }
            }
        }

        return mon with { Moves = moves.Select(id => moveNames[id]).ToArray() };
    }

    private static ushort IndexOfMove(IReadOnlyList<string> table, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return 0;
        }
        for (var i = 1; i < table.Count; i++)
        {
            if (string.Equals(table[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return (ushort)i;
            }
        }
        return 0;
    }
}
