using PKConnect.Core.champions;
using PKHeX.Core;

namespace PKConnect.Core.Tests.champions;

/// <summary>
/// The Hall of Fame read from played saves. Crystal has no reader in PKHeX at all: its teams are
/// read from the save's own bytes (sHallOfFame, checked against pokecrystal's symbols).
/// </summary>
public class HallOfFameExtractionTest
{
    [Fact]
    public void ExtractHallOfFame_Emerald_PostEliteFour_ReturnsNewestTeam()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "assets", "saves", "emerald-post-elite-four.sav"));
        SaveUtil.TryGetSaveFile(bytes, out var save);
        ArgumentNullException.ThrowIfNull(save);
        Assert.IsAssignableFrom<SAV3>(save);

        var hallOfFame = HallOfFameReader.Read(save);

        Assert.Single(hallOfFame);
        var newest = hallOfFame[^1];
        var species = newest.Pokemon.Select(p => p.Species).ToArray();
        Assert.Equal(new[] { 26, 130, 18, 45, 6, 51 }, species);
    }

    [Fact]
    public void ExtractHallOfFame_Crystal_ThreeTimeChampion_ReturnsAllTeamsNewestLast()
    {
        // A real save beaten 3 times: oldest win used a underleveled Togepi/
        // Marill, then two full-level-100 teams. Byte offsets confirmed against
        // pokecrystal.sym (sHallOfFame @ bank 01:b2c0).
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "assets", "saves", "crystal-post-elite-four-x3.sav"));
        SaveUtil.TryGetSaveFile(bytes, out var save);
        ArgumentNullException.ThrowIfNull(save);
        var sav2 = Assert.IsAssignableFrom<SAV2>(save);
        Assert.Equal(GameVersion.C, sav2.Version);

        var hallOfFame = HallOfFameReader.Read(save);

        Assert.Equal(3, hallOfFame.Count);

        var oldest = hallOfFame[0];
        Assert.Equal(new[] { 157, 18, 160, 154, 175, 183 }, oldest.Pokemon.Select(p => p.Species));
        Assert.Equal(5, oldest.Pokemon[4].Level);
        Assert.Equal("TOGEPI", oldest.Pokemon[4].Nickname);

        var newest = hallOfFame[^1];
        Assert.Equal(new[] { 26, 130, 18, 45, 6, 51 }, newest.Pokemon.Select(p => p.Species));
        Assert.All(newest.Pokemon, p => Assert.Equal(100, p.Level));
        Assert.Equal("RAICHU", newest.Pokemon[0].Nickname);
    }

    [Fact]
    public void ExtractHallOfFame_Sav2_NonCrystal_ReturnsEmpty_UnverifiedLayout()
    {
        // Gold/Silver aren't verified against this offset, so they're left
        // empty rather than risking a garbled read.
        var sav2 = new SAV2 { Version = GameVersion.GD };

        var hallOfFame = HallOfFameReader.Read(sav2);

        Assert.Empty(hallOfFame);
    }

    [Fact]
    public void NoBaseSave_ComesWithAHallOfFame()
    {
        // A visitor who has never beaten the League must not be taken for its champion.
        var folder = Path.Combine(AppContext.BaseDirectory, "connect-data", "base-saves");
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            Assert.True(SaveUtil.TryGetSaveFile(File.ReadAllBytes(path), out var save, path), path);
            Assert.True(save!.ChecksumsValid, $"{Path.GetFileName(path)}: checksums");
            Assert.True(HallOfFameReader.Read(save).Count == 0, $"{Path.GetFileName(path)} has Hall of Fame teams");
        }
    }
}