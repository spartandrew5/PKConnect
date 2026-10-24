using PKConnect.Core.champions;
using PKConnect.Core.graphics;
using PKConnect.Core.launch;

namespace PKConnect.Core.Tests.launch;

/// <summary>The champion fought at the League looks like the trainer who holds the title.</summary>
public class ChampionLookLaunchTests
{
    private static readonly ChampionTeamDTO Sky = new("Sky",
        [new(445, 62, Moves: ["Earthquake", "Flamethrower"]), new(448, 60, Moves: ["Aura Sphere"])],
        Source: "sky", BeatenAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private static string RomOf(string gameId) => gameId switch
    {
        "yellow" => TestRoms.Yellow,
        "emerald" => TestRoms.Emerald,
        "firered" => TestRoms.FireRed,
        "platinum" => TestRoms.Platinum,
        "heartgold" => TestRoms.HeartGold,
        "white" => TestRoms.White,
        "white2" => TestRoms.White2,
        "emeraldexpansion" => TestRoms.EmeraldExpansion,
        "fireredexpansion" => TestRoms.FireRedExpansion,
        _ => throw new ArgumentException(gameId),
    };

    private static LaunchPatchService.LaunchPatch Build(string gameId, string homeRegion, string? championCharacter, ChampionTeamDTO? champion = null)
    {
        var path = RomOf(gameId);
        var rom = TestRoms.Read(path);
        return LaunchPatchService.Build(
            gameId, rom, averageLevel: 50, homeRegion, champion ?? Sky, randomSeed: 5, romPath: path,
            championLook: championCharacter is null ? null : new PlayerGraphicsRequest(championCharacter, null));
    }

    [Theory]
    // Visiting...
    [InlineData("yellow", "johto", "red")]
    [InlineData("emerald", "kanto", "may")]
    [InlineData("firered", "hoenn", "leaf")]
    [InlineData("platinum", "kanto", "dawn")]
    [InlineData("heartgold", "kanto", "lyra")]
    [InlineData("white", "kanto", "hilda")]
    [InlineData("white2", "kanto", "rosa")]
    [InlineData("emeraldexpansion", "kanto", "brendan")]
    [InlineData("fireredexpansion", "hoenn", "red")]
    // ...and at home, where the champion is the one difference from the game as sold.
    [InlineData("yellow", "kanto", "red")]
    [InlineData("emerald", "hoenn", "brendan")]
    [InlineData("firered", "kanto", "red")]
    [InlineData("white", "unova", "hilbert")]
    public void TheChampion_TakesTheTitleHoldersLook(string gameId, string homeRegion, string character)
    {
        var plain = Build(gameId, homeRegion, null);
        var dressed = Build(gameId, homeRegion, character);

        Assert.True(dressed.Summary.ChampionApplied > 0);
        Assert.DoesNotContain(dressed.Summary.Warnings, warning => warning.StartsWith("[champion-graphics"));
        Assert.NotEqual(plain.Bytes, dressed.Bytes);
        // The player's own sprites aren't the champion's business. (In Gen 5 the two are written
        // together, into one archive, and counted together.)
        if (!gameId.StartsWith("white"))
            Assert.Equal(0, dressed.Summary.GraphicsApplied);
        else
            Assert.True(dressed.Summary.GraphicsApplied > 0);
    }

    [Theory]
    [InlineData("white", "brendan")]   // no Gen 5 sprites
    [InlineData("yellow", "nate")]   // no Game Boy sprites
    public void AHolderWithoutSpritesForTheGame_LeavesTheGamesOwnChampion(string gameId, string character)
    {
        var plain = Build(gameId, "kanto", null);
        var dressed = Build(gameId, "kanto", character);

        Assert.Equal(plain.Bytes, dressed.Bytes);
        Assert.DoesNotContain(dressed.Summary.Warnings, warning => warning.StartsWith("[champion-graphics"));
    }
}