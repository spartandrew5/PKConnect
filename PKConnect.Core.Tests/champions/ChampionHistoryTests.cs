using System.IO.Abstractions.TestingHelpers;
using PKConnect.Core.champions;
using PKVault.Core;

namespace PKConnect.Core.Tests.champions;

/// <summary>A region's title remembers who held it before.</summary>
public class ChampionHistoryTests
{
    private static ChampionTeamDTO Team(string name, string source) =>
        new(name, [new(445, 62)], Source: source, BeatenAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task TheTitle_PassesFromTheOriginalChampion_ThroughEveryHolder()
    {
        var service = new ChampionsService(new FileIOService(new MockFileSystem()));
        var original = (await service.GetChampions()).Regions["hoenn"].ChampionName;

        await service.SetChampion("hoenn", Team("Sky", "sky"));
        await service.SetChampion("hoenn", Team("Red", "red"));
        // The same trainer beating the League again only replaces their team.
        var champions = await service.SetChampion("hoenn", Team("Red", "red"));

        Assert.Equal("Red", champions.Regions["hoenn"].ChampionName);
        Assert.Equal([original, "Sky"], champions.PastHolders!["hoenn"]);
        Assert.False(champions.PastHolders.ContainsKey("kanto"));
    }
}