namespace PKConnect.Core.Tests;

/// <summary>
/// Retail ROMs the tests check the patches against. They aren't part of the repository: set
/// PKCONNECT_TEST_ROMS to a folder holding them under these file names. A test whose ROM is
/// missing is skipped, so the rest of the suite runs anywhere.
///
/// The DS games are looked for under PKCONNECT_TEST_NDS_ROMS, each in a folder named for it
/// ("Pokemon - Platinum Version (USA)" holding a .nds of the same name). The pokeemerald-expansion
/// builds are the ones in the decomp folder PKCONNECT_TEST_EXPANSION names, beside their .elf.
/// </summary>
internal static class TestRoms
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("PKCONNECT_TEST_ROMS");

    public static string Emerald => In("Pokemon Emerald.gba");
    public static string FireRed => In("Pokemon Fire Red.gba");
    public static string LeafGreen => In("Pokemon Leaf Green.gba");
    public static string Crystal => In("Pokemon Crystal.gbc");
    public static string Yellow => In("Pokemon Yellow.gb");

    private static readonly string? NdsDir = Environment.GetEnvironmentVariable("PKCONNECT_TEST_NDS_ROMS");
    private static readonly string? ExpansionDir = Environment.GetEnvironmentVariable("PKCONNECT_TEST_EXPANSION");

    public static string Platinum => Nds("Pokemon - Platinum Version (USA)");
    public static string HeartGold => Nds("Pokemon - HeartGold Version (USA)");
    public static string SoulSilver => Nds("Pokemon - SoulSilver Version (USA)");
    public static string Black => Nds("Pokemon - Black Version (USA, Europe) (NDSi Enhanced)");
    public static string White => Nds("Pokemon - White Version (USA, Europe) (NDSi Enhanced)");
    public static string Black2 => Nds("Pokemon - Black Version 2 (USA, Europe) (NDSi Enhanced)");
    public static string White2 => Nds("Pokemon - White Version 2 (USA, Europe) (NDSi Enhanced)");

    public static string EmeraldExpansion => Expansion("pokeemerald.gba");
    public static string FireRedExpansion => Expansion("pokefirered.gba");
    public static string LeafGreenExpansion => Expansion("pokeleafgreen.gba");

    private static string Expansion(string file) => ExpansionDir is null ? "" : Path.Combine(ExpansionDir, file);

    // The dump's file may carry a catalogue number before the name.
    private static string Nds(string name)
    {
        var folder = NdsDir is null ? null : Path.Combine(NdsDir, name);
        if (folder is null || !Directory.Exists(folder))
            return "";
        return Directory.EnumerateFiles(folder, "*.nds").FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).EndsWith(name, StringComparison.OrdinalIgnoreCase)) ?? "";
    }

    private static string In(string file) => Dir is null ? "" : Path.Combine(Dir, file);

    /// <summary>The ROM's bytes, or the test is skipped when it isn't on this machine.</summary>
    public static byte[] Read(string path)
    {
        Assert.SkipUnless(File.Exists(path), "ROM fixture not available (see PKCONNECT_TEST_ROMS)");
        return File.ReadAllBytes(path);
    }
}