using System.IO.Abstractions.TestingHelpers;
using PKConnect.Core.champions;
using PKVault.Core;

namespace PKConnect.Core.Tests.champions;

public class ChampionsServiceTests
{
    private static readonly ChampionTeamDTO Sky = new("Sky", [new(445, 62), new(448, 60)], Source: "profile-1");

    private static (ChampionsService Service, MockFileSystem Files) Create()
    {
        var files = new MockFileSystem();
        return (new ChampionsService(new FileIOService(files)), files);
    }

    [Fact]
    public async Task WithNoTitlesWon_EveryRegionHasItsOriginalChampion()
    {
        var (service, _) = Create();

        var champions = await service.GetChampions();

        Assert.Equal("Steven", champions.Regions["hoenn"].ChampionName);
        Assert.Equal("default", champions.Regions["hoenn"].Source);
        Assert.Equal(6, champions.Regions["kanto"].Team.Count);
    }

    [Fact]
    public async Task ANewChampion_TakesTheRegion_AndIsRemembered()
    {
        var (service, files) = Create();

        var afterWin = await service.SetChampion(" Hoenn ", Sky, saveId: "42", hallOfFameFingerprint: "1:445,448");

        Assert.Equal("Sky", afterWin.Regions["hoenn"].ChampionName);
        Assert.Equal("1:445,448", afterWin.SaveSnapshots!["42"]);
        Assert.Equal("Blue", afterWin.Regions["kanto"].ChampionName); // the others keep theirs

        // A new service on the same files (a restart) still knows.
        var later = await new ChampionsService(new FileIOService(files)).GetChampions();
        Assert.Equal("Sky", later.Regions["hoenn"].ChampionName);
        Assert.Equal(2, later.Regions["hoenn"].Team.Count);
    }

    [Fact]
    public async Task ARegionIsRequired()
    {
        var (service, _) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetChampion(" ", Sky));
    }
}