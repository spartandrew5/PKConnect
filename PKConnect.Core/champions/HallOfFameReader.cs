using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using PKConnect.Core.formats;
using PKHeX.Core;
using PKVault.Core;
using Serilog;

namespace PKConnect.Core.champions;

/// <summary>One Pokémon of a Hall of Fame team. The games record little more than this.</summary>
public record HallOfFamePokemonDTO(
    int Species,
    int Level,
    string? Nickname = null,
    List<int>? Types = null
);

/// <summary>A team entered into a save's Hall of Fame; <paramref name="Id"/> counts from 1, oldest first.</summary>
public record HallOfFameTeamDTO(
    int Id,
    List<HallOfFamePokemonDTO> Pokemon
);

/// <summary>
/// Reads the Hall of Fame out of a save, oldest team first. PKHeX has a reader for some
/// generations only, so most of these read the save's own structures. A team entered there is
/// what makes a trainer a region's champion.
/// </summary>
public static class HallOfFameReader
{
    public static List<HallOfFameTeamDTO> Read(SaveFile save) => save switch
    {
        SAV1 sav1 => ExtractHallOfFameSav1(sav1),
        SAV3 sav3 when SaveExtension.For(save) is ExpansionSaveExtension => ExtractHallOfFameExpansion(sav3),
        SAV3 sav3 => ExtractHallOfFameSav3(sav3),
        SAV2 sav2 => ExtractHallOfFameSav2(sav2),
        SAV4 sav4 => ExtractHallOfFameSav4(sav4),
        SAV5 sav5 => ExtractHallOfFameSav5(sav5),
        _ => ExtractHallOfFameGeneric(save),
    };
    private static List<HallOfFameTeamDTO> ExtractHallOfFameSav1(SAV1 sav1)
    {
        var countFromSave = Math.Max(0, Math.Min((int)sav1.HallOfFameCount, 30));
        var maxTeams = countFromSave > 0 ? countFromSave : 30;

        var teams = ExtractHallTeamsFromSav1Reader(sav1, maxTeams);
        if (teams.Count == 0)
        {
            teams = ExtractHallTeamsFromObjectGraph(sav1.HallOfFame, maxDepth: 4);
        }

        return NormalizeHallTeams(teams, maxTeams);
    }

    private static List<HallOfFameTeamDTO> ExtractHallTeamsFromSav1Reader(SAV1 sav1, int maxTeams)
    {
        var result = new List<HallOfFameTeamDTO>();

        var teamsToRead = Math.Max(1, maxTeams);
        for (var team = 0; team < teamsToRead; team++)
        {
            int memberCount;
            try
            {
                memberCount = sav1.HallOfFame.GetTeamMemberCount(team);
            }
            catch
            {
                continue;
            }

            if (memberCount <= 0)
                continue;

            var mons = new List<HallOfFamePokemonDTO>();
            for (var slot = 0; slot < Math.Min(6, memberCount); slot++)
            {
                if (!TryGetHallPokemonFromSav1(sav1, team, slot, out var mon))
                    continue;

                mons.Add(mon);
            }

            if (mons.Count > 0)
                result.Add(new HallOfFameTeamDTO(result.Count + 1, mons));
        }

        return result;
    }

    private static bool TryGetHallPokemonFromSav1(
        SAV1 sav1,
        int team,
        int slotZeroBased,
        out HallOfFamePokemonDTO pokemon)
    {
        pokemon = new HallOfFamePokemonDTO(0, 1);

        // Try both 0-based and 1-based slot indexing defensively.
        foreach (var slot in new[] { slotZeroBased, slotZeroBased + 1 })
        {
            try
            {
                var entity = sav1.HallOfFame.GetEntity(team, slot);
                if (entity.IsEmpty)
                    continue;

                var species = (int)entity.Species;
                if (species <= 0)
                    continue;

                var level = (int)entity.Level;
                if (level <= 0)
                    level = 1;

                var nickname = entity.Nickname;
                if (string.IsNullOrWhiteSpace(nickname))
                    nickname = null;

                pokemon = new HallOfFamePokemonDTO(species, level, nickname, GetHofTypes(sav1, species));
                return true;
            }
            catch
            {
                // try next indexing mode
            }
        }

        return false;
    }

    private static int SafeConvertInt(object? value)
    {
        if (value is null)
            return 0;

        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    // PKHeX.Core has no Gen2 Hall of Fame reader (its HallOfFameReader1/
    // HallOfFameEntity1 are Gen1-only; SAV2 exposes nothing but a
    // SecondsToFame counter), so this reads sHallOfFame directly from the raw
    // save bytes. Layout confirmed against pret/pokecrystal's own source
    // (constants/pokemon_data_constants.asm + engine/menus/save.asm) and
    // verified byte-for-byte against a real Crystal save via this project's
    // pokecrystal.sym (sHallOfFame @ bank 01:b2c0):
    //   file offset = bank(1)*0x2000 + (0xb2c0 - 0xa000) = 0x32c0
    // Each of NUM_HOF_TEAMS(30) HOF_LENGTH(0x62)-byte slots holds a 1-byte win
    // count + 6 HOF_MON_LENGTH(0x10)-byte mons (species, OT ID, DVs, level,
    // 10-byte nickname) + a terminator byte. AddHallOfFameEntry shifts every
    // existing slot up by one and writes the newest team into slot 0 (verified:
    // slot 0/1/2 here had win counts 3/2/1) — the opposite of Sav1/Sav3, so the
    // list is reversed before returning to match their oldest-first convention
    // (callers take the *last* team as newest).
    // Only verified for Crystal; Gold/Silver may not share this SRAM layout, so
    // they're left on the "no support" path rather than guessed at.
    private const int Gen2HallOfFameOffset = 0x32C0;
    private const int Gen2HallOfFameSlotLength = 0x62; // HOF_LENGTH
    private const int Gen2HallOfFameMonLength = 0x10; // HOF_MON_LENGTH
    private const int Gen2HallOfFameNumSlots = 30; // NUM_HOF_TEAMS
    private const int Gen2HallOfFamePartySize = 6; // PARTY_LENGTH

    private static bool _loggedNoGen2HallOfFameSupport;

    private static List<HallOfFameTeamDTO> ExtractHallOfFameSav2(SAV2 sav2)
    {
        if (sav2.Version != GameVersion.C)
        {
            if (!_loggedNoGen2HallOfFameSupport)
            {
                _loggedNoGen2HallOfFameSupport = true;
                Log.Warning(
                    "[hall-of-fame] Gen2 Hall of Fame offset is only verified for Crystal; {Save} ({Version}) will show no inductions",
                    sav2.Metadata.FileName, sav2.Version);
            }

            return [];
        }

        try
        {
            var data = sav2.Data;
            var teams = new List<HallOfFameTeamDTO>();

            for (var slot = 0; slot < Gen2HallOfFameNumSlots; slot++)
            {
                var slotOffset = Gen2HallOfFameOffset + slot * Gen2HallOfFameSlotLength;
                if (slotOffset + Gen2HallOfFameSlotLength > data.Length)
                    break;

                var mons = new List<HallOfFamePokemonDTO>();
                for (var mon = 0; mon < Gen2HallOfFamePartySize; mon++)
                {
                    var monOffset = slotOffset + 1 + mon * Gen2HallOfFameMonLength;
                    var species = data[monOffset];
                    if (species <= 0)
                        continue;

                    var level = data[monOffset + 5];
                    var nickname = StringConverter2.GetString(data.Slice(monOffset + 6, 10), 0);
                    mons.Add(new HallOfFamePokemonDTO(
                        species,
                        level > 0 ? level : 1,
                        string.IsNullOrWhiteSpace(nickname) ? null : nickname,
                        GetHofTypes(sav2, species)
                    ));
                }

                // Slots are sequential from index 0 with no gaps — the first
                // fully-empty slot marks the end of the recorded range.
                if (mons.Count == 0)
                    break;

                teams.Add(new HallOfFameTeamDTO(teams.Count + 1, mons));
            }

            // Slot 0 is the newest (see AddHallOfFameEntry note above) — reverse
            // to oldest-first so NormalizeHallTeams/callers' "last = newest" holds.
            teams.Reverse();

            return NormalizeHallTeams(teams, Gen2HallOfFameNumSlots);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[hall-of-fame] Gen2 Hall of Fame extraction failed for {Save}", sav2.Metadata.FileName);
            return [];
        }
    }

    private static List<HallOfFameTeamDTO> ExtractHallOfFameGeneric(SaveFile saveFile)
    {
        var teams = new List<HallOfFameTeamDTO>();

        var members = saveFile.GetType()
            .GetMembers(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name.Contains("Hall", StringComparison.OrdinalIgnoreCase)
                || m.Name.Contains("Fame", StringComparison.OrdinalIgnoreCase)
                || m.Name.Contains("Champion", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var member in members)
        {
            object? value = null;
            try
            {
                value = member switch
                {
                    PropertyInfo p when p.GetIndexParameters().Length == 0 => p.GetValue(saveFile),
                    MethodInfo m when m.GetParameters().Length == 0 => m.Invoke(saveFile, null),
                    _ => null,
                };
            }
            catch
            {
                continue;
            }

            if (value == null || value is string)
                continue;

            teams.AddRange(ExtractHallTeamsFromObjectGraph(value, maxDepth: 3));
        }

        return NormalizeHallTeams(teams, 30);
    }

    // pokeemerald-expansion keeps vanilla's Hall of Fame sectors (PKHeX's GetHallOfFameData) but
    // widens the record (include/hall_of_fame.h), so HallFame3Entry would misread it:
    //   struct HallofFameMon { u32 tid; u32 personality; u16 isShiny:1, species:15; u8 lvl;
    //                          u8 nickname[POKEMON_NAME_LENGTH = 12]; }  -> 24 bytes with padding
    //   struct HallofFameTeam { HallofFameMon mon[6]; }, HALL_OF_FAME_MAX_TEAMS = 30, filled oldest-first.
    // Species are the build's own ids (National Dex through #1025, forms elsewhere).
    private const int ExpansionHofMonSize = 24;
    private const int ExpansionHofTeams = 30;

    private static List<HallOfFameTeamDTO> ExtractHallOfFameExpansion(SAV3 sav3)
    {
        try
        {
            var data = sav3.GetHallOfFameData();
            var teams = new List<HallOfFameTeamDTO>();

            for (var t = 0; t < ExpansionHofTeams; t++)
            {
                var mons = new List<HallOfFamePokemonDTO>();
                for (var m = 0; m < 6; m++)
                {
                    var o = (t * 6 + m) * ExpansionHofMonSize;
                    if (o + ExpansionHofMonSize > data.Length)
                        break;

                    var raw = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o + 8)) >> 1);
                    var species = raw == 0 ? 0 : ExpansionSpeciesTable.GetNatDex(raw);
                    if (species is <= 0 or > 1025)
                        continue;

                    var level = data[o + 10];
                    var nickname = StringConverter3.GetString(data.AsSpan(o + 11, 12), sav3.Japanese);
                    List<int>? types = null;
                    try
                    {
                        var pi = ExpansionSpeciesTable.GetPersonalInfo(raw);
                        types = DexGenService.GetTypes(3, pi).Select(b => (int)b).ToList();
                    }
                    catch
                    {
                        // unknown species data: no types
                    }

                    mons.Add(new HallOfFamePokemonDTO(
                        species,
                        level > 0 ? level : 1,
                        string.IsNullOrWhiteSpace(nickname) ? null : nickname,
                        types
                    ));
                }

                // Teams are packed from the start; the first empty slot ends the list.
                if (mons.Count == 0)
                    break;
                teams.Add(new HallOfFameTeamDTO(teams.Count + 1, mons));
            }

            return NormalizeHallTeams(teams, ExpansionHofTeams);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[hall-of-fame] expansion Hall of Fame extraction failed for {Save}", sav3.Metadata.FileName);
            return [];
        }
    }

    private static List<HallOfFameTeamDTO> ExtractHallOfFameSav3(SAV3 sav3)
    {
        try
        {
            var entries = HallFame3Entry.GetEntries(sav3);
            var teams = new List<HallOfFameTeamDTO>();

            foreach (var entry in entries)
            {
                var mons = new List<HallOfFamePokemonDTO>();
                foreach (var member in entry.Team)
                {
                    var species = member.Species;
                    if (species <= 0)
                        continue;

                    var nickname = member.Nickname;
                    mons.Add(new HallOfFamePokemonDTO(
                        species,
                        member.Level > 0 ? member.Level : 1,
                        string.IsNullOrWhiteSpace(nickname) ? null : nickname,
                        GetHofTypes(sav3, species)
                    ));
                }

                if (mons.Count > 0)
                {
                    teams.Add(new HallOfFameTeamDTO(teams.Count + 1, mons));
                }
            }

            return NormalizeHallTeams(teams, 50);
        }
        catch (Exception ex)
        {
            // Previously swallowed silently, which made a real extraction failure
            // look identical to "this trainer has no Hall of Fame yet" — the
            // caller (syncActiveSaveRegionChampion) just no-ops on an empty list,
            // so a broken champion crowning went unnoticed. Log it instead.
            Log.Warning(ex, "[hall-of-fame] Gen3 Hall of Fame extraction failed for {Save}", sav3.Metadata.FileName);
            return [];
        }
    }

    // PKHeX.Core has no Gen4 Hall of Fame reader at all (it jumps from Gen3's
    // HallFame3Entry straight to Gen6's HallOfFame6 - there's no HallOfFame4
    // type). It DOES privately know where the raw block lives (SAV4's own
    // `ExtraBlocks[0]`, used by nothing public), so this reads that same
    // layout directly from the raw save bytes instead.
    //
    // Struct layout confirmed against pret/pokeheartgold's
    // include/hall_of_fame.h (byte offsets are explicit in that header):
    //   HOFMon (0x3C):  species u16 @0, level u8 @2, form u8 @3,
    //                   personality u32 @4, otid u32 @8, nickname u16[11] @0xC,
    //                   otname u16[8] @0x22, moves u16[4] @0x32, pad u16 @0x3A
    //   HOFTEAM (0x16C): 6x HOFMon, then year u16 + month u8 + day u8
    //   HallOfFame (0x2AB0): 30x HOFTEAM, then next_record u32, num_total u32
    //
    // Block location/redundancy confirmed against both pokeheartgold's
    // src/save.c (ReadExtraSaveChunk: chunk 0 lives at `hdr->sector * 0x1000`
    // with a second copy at `(hdr->sector + 64) * 0x1000`, i.e. offset and
    // offset+0x40000 - the two "General" partitions) AND PKHeX's own private
    // BlockInfo4 offsets in each SAV4 subclass's `ExtraBlocks[0]` (same
    // numbers). Which copy is current is decided by comparing each copy's
    // revision-counter footer, mirroring PKHeX's private
    // SAV4BlockDetection.CompareExtra exactly (re-implemented here since that
    // method isn't accessible from outside PKHeX.Core).
    private static List<HallOfFameTeamDTO> ExtractHallOfFameSav4(SAV4 sav4)
    {
        var (offset, length) = sav4 switch
        {
            SAV4HGSS => (0x23000, 0x2AC0),
            SAV4DP => (0x20000, 0x2AC0),
            SAV4Pt => (0x20000, 0x2AC0),
            _ => (-1, -1),
        };
        if (offset < 0)
        {
            return [];
        }

        try
        {
            var data = sav4.Data;
            const int PartitionSize = 0x40000;
            const int FooterSize = 0x10;
            const int DataSize = 0x2AB0; // length - FooterSize
            var footerOffset = offset + length - FooterSize;

            static uint GetRevision(ReadOnlySpan<byte> data, int partitionBase, int footerOffset) =>
                BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(partitionBase + footerOffset + 4, 4));
            static bool IsSizeValid(ReadOnlySpan<byte> data, int partitionBase, int footerOffset, int length, int footerSize) =>
                BinaryPrimitives.ReadInt32LittleEndian(data.Slice(partitionBase + footerOffset + 8, 4)) + footerSize == length;

            var rev1 = GetRevision(data, 0, footerOffset);
            var rev2 = GetRevision(data, PartitionSize, footerOffset);
            var valid1 = rev1 != 0xFFFFFFFF && IsSizeValid(data, 0, footerOffset, length, FooterSize);
            var valid2 = rev2 != 0xFFFFFFFF && IsSizeValid(data, PartitionSize, footerOffset, length, FooterSize);

            int chosenBase;
            if (valid1 && (rev1 >= rev2 || !valid2))
            {
                chosenBase = 0;
            }
            else if (valid2 && (rev2 > rev1 || !valid1))
            {
                chosenBase = PartitionSize;
            }
            else
            {
                return []; // Uninitialized - this trainer hasn't reached the Hall of Fame yet.
            }

            var hof = data.Slice(chosenBase + offset, DataSize);
            const int TeamSize = 0x16C;
            const int MonSize = 0x3C;
            const int NumRecords = 30;

            // parties[] is a circular buffer written at `next_record`, wrapping
            // once num_total exceeds 30 - start from the oldest surviving slot
            // so the returned list is oldest-first (NormalizeHallTeams/callers
            // take the *last* entry as newest, same convention as Sav1/Sav3).
            var nextRecord = BinaryPrimitives.ReadUInt32LittleEndian(hof.Slice(NumRecords * TeamSize, 4));
            var numTotal = BinaryPrimitives.ReadUInt32LittleEndian(hof.Slice((NumRecords * TeamSize) + 4, 4));
            var start = numTotal > NumRecords ? (int)(nextRecord % NumRecords) : 0;

            var teams = new List<HallOfFameTeamDTO>();
            for (var i = 0; i < NumRecords; i++)
            {
                var slot = (start + i) % NumRecords;
                var teamOffset = slot * TeamSize;
                var mons = new List<HallOfFamePokemonDTO>();
                for (var m = 0; m < 6; m++)
                {
                    var monOffset = teamOffset + (m * MonSize);
                    var species = BinaryPrimitives.ReadUInt16LittleEndian(hof.Slice(monOffset, 2));
                    if (species <= 0)
                    {
                        continue;
                    }

                    var level = hof[monOffset + 2];
                    var nickname = StringConverter4.GetString(hof.Slice(monOffset + 0x0C, 22));
                    mons.Add(new HallOfFamePokemonDTO(
                        species,
                        level > 0 ? level : 1,
                        string.IsNullOrWhiteSpace(nickname) ? null : nickname,
                        GetHofTypes(sav4, species)
                    ));
                }

                if (mons.Count > 0)
                {
                    teams.Add(new HallOfFameTeamDTO(teams.Count + 1, mons));
                }
            }

            return NormalizeHallTeams(teams, NumRecords);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[hall-of-fame] Gen4 Hall of Fame extraction failed for {Save}", sav4.Metadata.FileName);
            return [];
        }
    }

    // Black/White keep the Hall of Fame outside the main save, like Gen 4, and PKHeX has no
    // reader for it either. Layout worked out from a White save with six inductions:
    //   the block is at 0x7C800 with a second copy at 0x7E000;
    //   15 teams of 0x16C: six mons of 0x3C in Gen 4's HOFMon shape (species u16 @0,
    //   level u8 @2, form u8 @3, personality u32 @4, otid u32 @8, nickname u16[11] @0xC,
    //   otname u16[8] @0x22, moves u16[4] @0x32), then year u16 + month u8 + day u8;
    //   then next_record u32 and num_total u32 (a circular buffer, as in Gen 4).
    // An untouched block is all 0xFF. Black 2/White 2 have the same block at 0x74000, with its
    // second copy at 0x75800 (worked out from a White 2 save with one induction).
    internal static List<HallOfFameTeamDTO> ExtractHallOfFameSav5(SAV5 sav5)
    {
        const int TeamSize = 0x16C;
        const int MonSize = 0x3C;
        const int NumRecords = 15;
        const int BlockSize = (NumRecords * TeamSize) + 8;
        try
        {
            var data = sav5.Data;
            foreach (var offset in sav5 is SAV5B2W2 ? new[] { 0x74000, 0x75800 } : new[] { 0x7C800, 0x7E000 })
            {
                if (data.Length < offset + BlockSize) return [];
                var hof = data.Slice(offset, BlockSize);
                var nextRecord = BinaryPrimitives.ReadUInt32LittleEndian(hof.Slice(NumRecords * TeamSize, 4));
                var numTotal = BinaryPrimitives.ReadUInt32LittleEndian(hof.Slice((NumRecords * TeamSize) + 4, 4));
                if (nextRecord >= NumRecords || numTotal == 0xFFFFFFFF) continue; // this copy was never written

                var start = numTotal > NumRecords ? (int)nextRecord : 0;
                var teams = new List<HallOfFameTeamDTO>();
                for (var i = 0; i < NumRecords; i++)
                {
                    var teamOffset = ((start + i) % NumRecords) * TeamSize;
                    var mons = new List<HallOfFamePokemonDTO>();
                    for (var m = 0; m < 6; m++)
                    {
                        var monOffset = teamOffset + (m * MonSize);
                        var species = BinaryPrimitives.ReadUInt16LittleEndian(hof.Slice(monOffset, 2));
                        if (species <= 0 || species > sav5.MaxSpeciesID)
                        {
                            continue;
                        }

                        var level = hof[monOffset + 2];
                        var nickname = StringConverter5.GetString(hof.Slice(monOffset + 0x0C, 22));
                        mons.Add(new HallOfFamePokemonDTO(
                            species,
                            level > 0 ? level : 1,
                            string.IsNullOrWhiteSpace(nickname) ? null : nickname,
                            GetHofTypes(sav5, species)
                        ));
                    }

                    if (mons.Count > 0)
                    {
                        teams.Add(new HallOfFameTeamDTO(teams.Count + 1, mons));
                    }
                }

                return NormalizeHallTeams(teams, NumRecords);
            }
            return [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[hall-of-fame] Gen5 Hall of Fame extraction failed for {Save}", sav5.Metadata.FileName);
            return [];
        }
    }

    // Hall of Fame records only ever store species/level/nickname (no PKM
    // object with its own PersonalInfo), so the frontend's usual "read types
    // off the PKM" path (ImmutablePKM.Types) has nothing to read - it fell
    // back to a generation-only adapter (Gen 1 only) and showed every other
    // generation's species as a single flat/wrong-colored type dot. Every
    // SaveFile exposes a generic Personal table regardless of generation, so
    // compute real types here the same way DexGenService/ImmutablePKM.Types
    // already do for regular box/party mons.
    private static List<int>? GetHofTypes(SaveFile save, int species)
    {
        if (species <= 0)
        {
            return null;
        }

        try
        {
            var pi = save.Personal.GetFormEntry((ushort)species, 0);
            return DexGenService.GetTypes(save.Generation, pi).Select(b => (int)b).ToList();
        }
        catch
        {
            return null;
        }
    }

    private static List<HallOfFameTeamDTO> NormalizeHallTeams(
        List<HallOfFameTeamDTO> teams,
        int maxTeams)
    {
        var valid = teams.Where(t => t.Pokemon.Count > 0).ToList();
        if (valid.Count == 0)
            return [];

        // Some backends expose a flat list of HOF mon entries; regroup as 6-mon inductions.
        var looksFlatMonList = valid.Count >= 6 && valid.All(t => t.Pokemon.Count == 1);
        if (looksFlatMonList)
        {
            var regrouped = new List<HallOfFameTeamDTO>();
            for (var i = 0; i + 5 < valid.Count; i += 6)
            {
                var mons = valid
                    .Skip(i)
                    .Take(6)
                    .SelectMany(t => t.Pokemon)
                    .Where(m => m.Species > 0)
                    .ToList();

                if (mons.Count > 0)
                {
                    regrouped.Add(new HallOfFameTeamDTO(regrouped.Count + 1, mons));
                }
            }

            if (regrouped.Count > 0)
                valid = regrouped;
        }

        return valid
            .TakeLast(Math.Max(0, maxTeams))
            .Select((t, i) => new HallOfFameTeamDTO(i + 1, t.Pokemon))
            .ToList();
    }

    private static List<HallOfFameTeamDTO> ExtractHallTeamsFromObjectGraph(object source, int maxDepth)
    {
        var teams = new List<HallOfFameTeamDTO>();
        var visited = new HashSet<int>();
        var stack = new Stack<(object Obj, int Depth)>();
        stack.Push((source, 0));

        while (stack.Count > 0)
        {
            var (obj, depth) = stack.Pop();
            if (obj is null || obj is string || depth > maxDepth)
                continue;

            var refId = RuntimeHelpers.GetHashCode(obj);
            if (!visited.Add(refId))
                continue;

            if (TryParseHallCollection(obj, out var parsed) && parsed.Count > 0)
            {
                teams.AddRange(parsed);
            }

            if (obj is IEnumerable enumerable && obj is not byte[])
            {
                var i = 0;
                foreach (var entry in enumerable)
                {
                    if (entry != null)
                        stack.Push((entry, depth + 1));

                    i++;
                    if (i >= 128)
                        break;
                }
            }

            var props = obj.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.GetIndexParameters().Length == 0)
                .ToList();

            foreach (var prop in props)
            {
                object? nested;
                try
                {
                    nested = prop.GetValue(obj);
                }
                catch
                {
                    continue;
                }

                if (nested == null || nested is string)
                    continue;

                if (nested.GetType().IsPrimitive || nested.GetType().IsEnum)
                    continue;

                stack.Push((nested, depth + 1));
            }
        }

        return teams;
    }

    private static bool TryParseHallCollection(object source, out List<HallOfFameTeamDTO> teams)
    {
        teams = [];

        if (source is not IEnumerable enumerable || source is string)
            return false;

        var index = 0;
        foreach (var teamCandidate in enumerable)
        {
            if (teamCandidate is null || teamCandidate is string)
                continue;

            var mons = ParseHallTeamPokemon(teamCandidate);
            if (mons.Count == 0)
                continue;

            index++;
            teams.Add(new HallOfFameTeamDTO(index, mons));
        }

        return teams.Count > 0;
    }

    private static List<HallOfFamePokemonDTO> ParseHallTeamPokemon(object teamCandidate)
    {
        if (TryParseHallPokemon(teamCandidate, out var single))
            return [single];

        // Case A: enumerable of pokemon-like entries.
        if (teamCandidate is IEnumerable enumerable && teamCandidate is not string)
        {
            var directMons = new List<HallOfFamePokemonDTO>();
            foreach (var mon in enumerable)
            {
                if (TryParseHallPokemon(mon, out var parsed))
                    directMons.Add(parsed);
            }

            if (directMons.Count > 0)
                return directMons;
        }

        // Case B: packed Species1..Species6 style layout.
        var packed = ParsePackedHallTeam(teamCandidate);
        if (packed.Count > 0)
            return packed;

        // Case C: nested collection property.
        var nestedCollection = teamCandidate.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(p => p.Name.Contains("Pokemon", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Team", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Members", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Party", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Entries", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Mons", StringComparison.OrdinalIgnoreCase));

        if (nestedCollection == null)
            return [];

        object? nestedValue;
        try
        {
            nestedValue = nestedCollection.GetValue(teamCandidate);
        }
        catch
        {
            return [];
        }

        if (nestedValue is not IEnumerable nestedEnumerable || nestedValue is string)
            return [];

        var nestedMons = new List<HallOfFamePokemonDTO>();
        foreach (var mon in nestedEnumerable)
        {
            if (TryParseHallPokemon(mon, out var parsed))
            {
                nestedMons.Add(parsed);
            }
        }

        return nestedMons;
    }

    private static List<HallOfFamePokemonDTO> ParsePackedHallTeam(object teamCandidate)
    {
        var mons = new List<HallOfFamePokemonDTO>();
        var type = teamCandidate.GetType();

        for (var i = 1; i <= 6; i++)
        {
            var species = TryGetInt(
                teamCandidate,
                type,
                $"Species{i}",
                $"Pokemon{i}",
                $"Mon{i}",
                $"Member{i}",
                $"Dex{i}",
                $"Entry{i}"
            );

            if (species <= 0)
                continue;

            var level = TryGetInt(
                teamCandidate,
                type,
                $"Level{i}",
                $"Lv{i}",
                $"CurrentLevel{i}",
                $"Stat_Level{i}",
                $"StatLevel{i}"
            );

            if (level <= 0)
                level = 1;

            mons.Add(new HallOfFamePokemonDTO(species, level));
        }

        return mons;
    }

    private static bool TryParseHallPokemon(object? candidate, out HallOfFamePokemonDTO pokemon)
    {
        pokemon = new HallOfFamePokemonDTO(0, 1);
        if (candidate is null)
            return false;

        var type = candidate.GetType();
        var species = TryGetInt(candidate, type, "Species", "SpeciesID", "SpeciesId", "Dex", "DexId", "Index", "ID", "Id");
        var level = TryGetInt(candidate, type, "Level", "CurrentLevel", "Stat_Level", "StatLevel");
        var nickname = TryGetString(candidate, type, "Nickname", "Name");

        if (species <= 0)
            return false;

        if (level <= 0)
            level = 1;

        pokemon = new HallOfFamePokemonDTO(species, level, nickname);
        return true;
    }

    private static int TryGetInt(object obj, Type type, params string[] names)
    {
        foreach (var name in names)
        {
            object? value = null;

            var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (prop != null && prop.GetIndexParameters().Length == 0)
            {
                try
                {
                    value = prop.GetValue(obj);
                }
                catch
                {
                    value = null;
                }
            }

            if (value is null)
            {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (field != null)
                {
                    try
                    {
                        value = field.GetValue(obj);
                    }
                    catch
                    {
                        value = null;
                    }
                }
            }

            if (value is null)
                continue;

            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                continue;
            }
        }

        return 0;
    }

    private static string? TryGetString(object obj, Type type, params string[] names)
    {
        foreach (var name in names)
        {
            object? value = null;

            var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (prop != null && prop.GetIndexParameters().Length == 0)
            {
                try
                {
                    value = prop.GetValue(obj);
                }
                catch
                {
                    value = null;
                }
            }

            if (value is null)
            {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (field != null)
                {
                    try
                    {
                        value = field.GetValue(obj);
                    }
                    catch
                    {
                        value = null;
                    }
                }
            }

            if (value is string s && !string.IsNullOrWhiteSpace(s))
                return s;
        }

        return null;
    }
}
