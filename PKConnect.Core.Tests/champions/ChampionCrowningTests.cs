using PKConnect.Core.formats;
using PKConnect.Core.champions;
using PKHeX.Core;

namespace PKConnect.Core.Tests.champions;

/// <summary>A trainer becomes champion with the newest team in their save's Hall of Fame.</summary>
public class ChampionCrowningTests
{
    private static readonly DateTime Now = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static SaveFile Played(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "saves", file);
        Assert.True(SaveFiles.TryLoad(File.ReadAllBytes(path), out var save, path));
        return save!;
    }

    private static SaveFile BaseSave(string game)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "connect-data", "base-saves");
        var path = Directory.EnumerateFiles(folder, $"{game}.*").Single();
        Assert.True(SaveFiles.TryLoad(File.ReadAllBytes(path), out var save, path));
        return save!;
    }

    [Fact]
    public void AnEmptyHallOfFame_HasNoFingerprint_AndNoChampion()
    {
        var save = BaseSave("emerald");
        var hallOfFame = HallOfFameReader.Read(save);

        Assert.Equal("", ChampionCrowning.Fingerprint(hallOfFame));
        Assert.Null(ChampionCrowning.BuildChampion(save, hallOfFame, "red", "Red", Now));
    }

    [Fact]
    public void TheFingerprint_ChangesWhenTheLeagueIsBeatenAgain()
    {
        var hallOfFame = HallOfFameReader.Read(Played("crystal-post-elite-four-x3.sav"));

        var afterThree = ChampionCrowning.Fingerprint(hallOfFame);
        var afterTwo = ChampionCrowning.Fingerprint(hallOfFame[..2]);

        Assert.NotEqual("", afterThree);
        Assert.NotEqual(afterTwo, afterThree);
        // Looking again changes nothing.
        Assert.Equal(afterThree, ChampionCrowning.Fingerprint(HallOfFameReader.Read(Played("crystal-post-elite-four-x3.sav"))));
    }

    [Fact]
    public void TheChampion_IsTheNewestTeam_WithThePartysMoves()
    {
        var save = Played("emerald-post-elite-four.sav");
        var hallOfFame = HallOfFameReader.Read(save);

        var champion = ChampionCrowning.BuildChampion(save, hallOfFame, "red", "Red", Now);

        Assert.NotNull(champion);
        Assert.Equal(save.OT, champion.ChampionName);
        Assert.Equal("red", champion.Source);
        Assert.Equal(Now, champion.BeatenAt);
        Assert.Equal("E", champion.GameVersion);
        Assert.Equal([26, 130, 18, 45, 6, 51], champion.Team.Select(mon => mon.Species));
        // The Hall of Fame has no moves; the party members of the same species lend theirs.
        var inParty = champion.Team.Where(mon => save.PartyData.Any(member => member.Species == mon.Species)).ToList();
        Assert.NotEmpty(inParty);
        Assert.All(inParty, mon => Assert.NotEmpty(mon.Moves!));
        Assert.All(inParty.SelectMany(mon => mon.Moves!), move => Assert.False(string.IsNullOrWhiteSpace(move)));
    }

    [Fact]
    public void TheTitle_IsTheOneOfTheSavesRegion()
    {
        Assert.Equal("hoenn", ChampionCrowning.RegionOf(Played("emerald-post-elite-four.sav")));
        Assert.Equal("johto", ChampionCrowning.RegionOf(Played("crystal-post-elite-four-x3.sav")));
        Assert.Equal("kanto", ChampionCrowning.RegionOf(BaseSave("fireredexpansion")));
    }

    [Fact]
    public void InUnova_ALeagueWinWithoutTheEightBadges_IsNoTitle()
    {
        // The visitor's base save can reach the League with none.
        var white = (SAV5)BaseSave("white");
        Assert.False(ChampionCrowning.HasEarnedTheTitle(white));

        white.Misc.Badges = 0xFF;
        Assert.True(ChampionCrowning.HasEarnedTheTitle(white));
        Assert.True(ChampionCrowning.HasEarnedTheTitle(BaseSave("emerald")));
    }
}