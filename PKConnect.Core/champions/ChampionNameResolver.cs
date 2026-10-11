using PKHeX.Core;

namespace PKConnect.Core.champions;

// The champion config stores English move/item names, so resolve against the
// English tables regardless of the app's display language.

/// <summary>
/// Turns a stored champion mon (National Dex species id + move/item NAMES, the
/// canonical shape written by ChampionsService / the Hall-of-Fame sync) into the
/// raw ids a ROM trainer-party struct needs, using PKHeX's string tables.
///
///  - GBA (Gen 3): internal species id (Gen 3 reorders 252+), move ids (shared
///    with Gens 1-2 for old moves), Gen 3 item ids.
///  - GB  (Gen 1): scrambled internal species order, no move ids written.
///  - GBC (Gen 2): internal species order == National Dex, GSC move ids.
/// </summary>
public static class ChampionNameResolver
{
    public readonly record struct ResolvedMon(
        int Species,
        int Level,
        int Item,
        int[] Moves
    );

    private static string Norm(string s)
    {
        Span<char> buffer = stackalloc char[s.Length];
        var n = 0;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[n++] = char.ToLowerInvariant(c);
            }
        }
        return new string(buffer[..n]);
    }

    private static int IndexOfName(IReadOnlyList<string> table, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return 0;
        }
        var target = Norm(name);
        for (var i = 1; i < table.Count; i++)
        {
            if (Norm(table[i]) == target)
            {
                return i;
            }
        }
        return 0;
    }

    // Move ids are shared across generations; a move newer than the target
    // game (e.g. from a later-gen party) is dropped rather than written.
    private static int[] ResolveMoves(string[]? moveNames, int maxMoveId)
    {
        var moves = new int[4];
        if (moveNames == null)
        {
            return moves;
        }
        var moveTable = GameInfo.GetStrings("en").Move;
        for (var i = 0; i < 4 && i < moveNames.Length; i++)
        {
            var id = IndexOfName(moveTable, moveNames[i]);
            moves[i] = id <= maxMoveId ? id : 0;
        }
        return moves;
    }

    /// <summary>Gen 3 (GBA): internal species, Gen 3 item id, move ids.</summary>
    public static ResolvedMon ResolveGen3(ChampionMonDTO mon)
    {
        // A species the game doesn't have resolves to 0 (dropped), never clamped
        // onto a different Pokemon.
        if (mon.Species is <= 0 or > 386)
        {
            return new ResolvedMon(0, mon.Level, 0, new int[4]);
        }
        var internalSpecies = SpeciesConverter.GetInternal3((ushort)mon.Species);

        var itemTable = GameInfo.GetStrings("en").GetItemStrings(EntityContext.Gen3, GameVersion.E);
        var itemId = IndexOfName(itemTable, mon.HeldItem);

        return new ResolvedMon(internalSpecies, mon.Level, itemId, ResolveMoves(mon.Moves, 354));
    }

    /// <summary>
    /// pokeemerald-expansion: the build's own species id (National Dex through #1025, forms
    /// elsewhere), its own item id (mapped from the modern one by name), and modern move ids, which
    /// the expansion uses unchanged - so later-gen champions keep their moves.
    /// </summary>
    public static ResolvedMon ResolveExpansion(ChampionMonDTO mon)
    {
        if (mon.Species is <= 0 or > 1025)
        {
            return new ResolvedMon(0, mon.Level, 0, new int[4]);
        }
        var rawSpecies = formats.ExpansionSpeciesTable.GetRawSpecies((ushort)mon.Species);

        var modernItem = IndexOfName(GameInfo.GetStrings("en").Item, mon.HeldItem);
        var itemId = formats.ExpansionItemTable.ToExpansion((ushort)modernItem);

        return new ResolvedMon(rawSpecies, mon.Level, itemId, ResolveMoves(mon.Moves, GameInfo.GetStrings("en").Move.Count - 1));
    }

    /// <summary>Gen 1 (Yellow): scrambled internal species; moves not written.</summary>
    public static ResolvedMon ResolveGen1(ChampionMonDTO mon)
    {
        if (mon.Species is <= 0 or > 151)
        {
            return new ResolvedMon(0, mon.Level, 0, new int[4]);
        }
        var internalSpecies = SpeciesConverter.GetInternal1((ushort)mon.Species);
        return new ResolvedMon(internalSpecies, mon.Level, 0, new int[4]);
    }

    /// <summary>Gen 2 (Crystal): internal species == National Dex; GSC item and move ids.</summary>
    public static ResolvedMon ResolveGen2(ChampionMonDTO mon)
    {
        if (mon.Species is <= 0 or > 251)
        {
            return new ResolvedMon(0, mon.Level, 0, new int[4]);
        }

        var itemTable = GameInfo.GetStrings("en").GetItemStrings(EntityContext.Gen2, GameVersion.C);
        var itemId = IndexOfName(itemTable, mon.HeldItem);

        return new ResolvedMon(mon.Species, mon.Level, itemId, ResolveMoves(mon.Moves, 251));
    }

    /// <summary>
    /// Encodes a champion's display name into a Gen 3 trainer struct's fixed
    /// 12-byte trainerName field (struct Trainer, GymTrainerTableData) — 7
    /// characters max (silently truncated), 0xFF-terminated/padded, matching
    /// the game's own OT-name convention. Several battle-intro scripts pull
    /// the speaker's name from this same field (rather than a hardcoded
    /// string), so writing it is what turns "STEVEN: Welcome, RED." into the
    /// actual reigning champion's name.
    /// </summary>
    public static byte[] EncodeGen3TrainerName(string name)
    {
        var buffer = new byte[12];
        Array.Fill(buffer, (byte)0xFF);
        var truncated = name.Length > 7 ? name[..7] : name;
        StringConverter3.SetString(buffer, truncated, 7, (int)LanguageID.English, StringConverterOption.None);
        return buffer;
    }

    /// <summary>
    /// Encodes a champion's display name into pokecrystal's fixed 8-byte
    /// champion-group name slot (see ConnectTeamPatchService.BuildChampionEdits
    /// and pokecrystal_connect.json's championPlaceholder.party): up to 7
    /// characters, any unused bytes before the fixed terminator padded with
    /// 0x25 (the invisible &lt;WBR&gt; control code - NOT 0x50, which the
    /// party-group reader treats as the end of the name and would misread the
    /// trailing bytes as trainerType/party data), then 0x50 fixed at byte 7
    /// regardless of name length.
    /// </summary>
    public static byte[] EncodeGen2TrainerName(string name)
    {
        var truncated = name.Length > 7 ? name[..7] : name;
        var encoded = new byte[8];
        StringConverter1.SetString(encoded, truncated, truncated.Length, false, StringConverterOption.None);

        var buffer = new byte[8];
        Array.Copy(encoded, buffer, truncated.Length);
        for (var i = truncated.Length; i < 7; i++)
        {
            buffer[i] = 0x25;
        }
        buffer[7] = 0x50;
        return buffer;
    }

    /// <summary>
    /// Encodes a champion's display name for Yellow's champion-name-override
    /// buffer (see ConnectTeamPatchService.BuildChampionEdits and
    /// pokeyellow_connect.json's championPlaceholder.name): up to 7
    /// characters, followed by a single 0x50 terminator. Unlike Crystal's
    /// fixed 8-byte party-group slot, nothing meaningful follows this
    /// dedicated buffer (5 reserved bytes exist only so the engine's fixed
    /// 13-byte copy stays in-block), so a shorter name needs no padding -
    /// the reader stops at the first 0x50 regardless.
    /// </summary>
    public static byte[] EncodeGen1TrainerName(string name)
    {
        var truncated = name.Length > 7 ? name[..7] : name;
        var chars = new byte[truncated.Length];
        StringConverter1.SetString(chars, truncated, truncated.Length, false, StringConverterOption.None);

        var buffer = new byte[chars.Length + 1];
        chars.CopyTo(buffer, 0);
        buffer[^1] = 0x50;
        return buffer;
    }
}
