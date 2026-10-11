using PKConnect.Core.formats;
using PKConnect.Core.patching;
using PKHeX.Core;

namespace PKConnect.Core.launch;

/// <summary>
/// A pokeemerald-expansion ROM and the build it came from. Addresses move with every build and
/// config change, so nothing in such a ROM can be found without the build's own <c>.elf</c>.
/// </summary>
public static class ExpansionBuild
{
    // Player data whose bytes must be the same in the .elf and the ROM for the .elf to count as this ROM's build.
    private static readonly string[] EmeraldMatchSymbols =
        ["gObjectEventPal_Brendan", "gTrainerFrontPic_Brendan", "sRegionMapPlayerIcon_BrendanPal"];
    private static readonly string[] FrlgMatchSymbols =
        ["gObjectEventPal_PlayerFrlg", "gTrainerFrontPic_Red", "sRegionMapPlayerIcon_RedPal"];

    public const string MissingSymbolsMessage =
        "No matching pokeemerald-expansion build .elf found next to the ROM. Point the ROM path at the "
        + ".gba inside your decomp folder (next to its .elf, e.g. pokeemerald.elf / pokefirered.elf) so "
        + "its addresses can be found.";

    /// <summary>
    /// Finds the build's <c>.elf</c> next to <paramref name="romPath"/> (same name first, then any
    /// other .elf in that folder) whose data matches this ROM. Null when there isn't one.
    /// </summary>
    public static ElfSymbolTable? FindSymbols(string gameId, string romPath, byte[] rom)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(romPath));
        if (dir is null)
            return null;

        var sameName = Path.ChangeExtension(Path.GetFullPath(romPath), ".elf");
        var candidates = new List<string>();
        if (File.Exists(sameName))
            candidates.Add(sameName);
        candidates.AddRange(Directory.EnumerateFiles(dir, "*.elf")
            .Where(p => !string.Equals(p, sameName, StringComparison.OrdinalIgnoreCase)));

        var matchSymbols = ExpansionSaveDetector.BaseVersion(gameId) is GameVersion.FR or GameVersion.LG
            ? FrlgMatchSymbols
            : EmeraldMatchSymbols;
        foreach (var candidate in candidates)
        {
            if (ElfSymbolTable.TryLoad(candidate) is { } elf && MatchesRom(elf, rom, matchSymbols))
                return elf;
        }
        return null;
    }

    private static bool MatchesRom(ElfSymbolTable elf, byte[] rom, string[] symbols)
    {
        foreach (var name in symbols)
        {
            var sym = elf.Get(name);
            var off = elf.RomOffset(name);
            if (sym is null || off is null || off.Value + 32 > rom.Length)
                return false;
            var expected = elf.ReadBytes(sym, 32);
            if (expected is null || !rom.AsSpan(off.Value, 32).SequenceEqual(expected))
                return false;
        }
        return true;
    }

    /// <summary>First ROM offset the build doesn't use (<c>__rom_end</c>), else past the last non-0xFF byte.</summary>
    public static int FreeSpaceStart(ElfSymbolTable elf, byte[] rom)
    {
        if (elf.Get("__rom_end") is { } end && end.Address > Gba.RomBase && end.Address - Gba.RomBase < rom.Length)
            return (int)(end.Address - Gba.RomBase + 3) & ~3;

        var last = rom.Length - 1;
        while (last > 0 && rom[last] == 0xFF)
            last--;
        return (last + 4) & ~3;
    }
}