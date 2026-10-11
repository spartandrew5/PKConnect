using PKConnect.Core.champions;

namespace PKConnect.Core.Tests.champions;

public class ChampionTeamSubstitutionTests
{
    private const int Garchomp = 445;
    private const int Lucario = 448;
    private const int Blaziken = 257;

    // A later champion's team is fitted to an older game: what the game has stays, the rest is replaced.
    [Fact]
    public void Emerald_KeepsItsOwnSpecies_AndReplacesLaterOnes()
    {
        ChampionMonDTO[] team =
        [
            new(Blaziken, 60, Moves: ["Blaze Kick", "Sky Uppercut", "Flare Blitz", "Slash"]),
            new(Garchomp, 62, Moves: ["Earthquake", "Dragon Rush"]),
            new(Lucario, 60, Moves: ["Aura Sphere"]),
        ];

        var adapted = ChampionTeamSubstitution.Adapt("emerald", team);

        Assert.Equal(3, adapted.Count);
        Assert.Equal(Blaziken, adapted[0].Species);
        Assert.All(adapted, m => Assert.InRange(m.Species, 1, 386));
        Assert.Equal(adapted.Count, adapted.Select(m => m.Species).Distinct().Count());
        // Flare Blitz is a Gen 4 move: gone, the Gen 3 ones stay.
        Assert.DoesNotContain("Flare Blitz", adapted[0].Moves!);
        Assert.Contains("Blaze Kick", adapted[0].Moves!);
        // Levels are the champion's own.
        Assert.Equal([60, 62, 60], adapted.Select(m => m.Level));
    }

    [Fact]
    public void Yellow_OnlyHasTheFirstGeneration()
    {
        ChampionMonDTO[] team = [new(Garchomp, 62), new(6, 60, Moves: ["Flamethrower", "Dragon Claw"])];

        var adapted = ChampionTeamSubstitution.Adapt("yellow", team);

        Assert.Equal(2, adapted.Count);
        Assert.All(adapted, m => Assert.InRange(m.Species, 1, 151));
        Assert.Contains(adapted, m => m.Species == 6);
    }

    [Fact]
    public void AGameWithNoTarget_GetsTheTeamAsItIs()
    {
        ChampionMonDTO[] team = [new(Garchomp, 62)];

        Assert.Equal(team, ChampionTeamSubstitution.Adapt("some-future-game", team));
    }
}