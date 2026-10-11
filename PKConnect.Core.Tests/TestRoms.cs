namespace PKConnect.Core.Tests;

/// <summary>
/// Retail ROMs the tests check the patches against. They aren't part of the repository: set
/// PKCONNECT_TEST_ROMS to a folder holding them under these file names. A test whose ROM is
/// missing is skipped, so the rest of the suite runs anywhere.
/// </summary>
internal static class TestRoms
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("PKCONNECT_TEST_ROMS");

    public static string Emerald => In("Pokemon Emerald.gba");
    public static string FireRed => In("Pokemon Fire Red.gba");
    public static string LeafGreen => In("Pokemon Leaf Green.gba");
    public static string Crystal => In("Pokemon Crystal.gbc");
    public static string Yellow => In("Pokemon Yellow.gb");

    private static string In(string file) => Dir is null ? "" : Path.Combine(Dir, file);

    /// <summary>The ROM's bytes, or the test is skipped when it isn't on this machine.</summary>
    public static byte[] Read(string path)
    {
        Assert.SkipUnless(File.Exists(path), "ROM fixture not available (see PKCONNECT_TEST_ROMS)");
        return File.ReadAllBytes(path);
    }
}