using PKConnect.Core.graphics;
using PKConnect.Core.launch;

namespace PKConnect.Core.Tests.launch;

/// <summary>
/// In the HeartGold/SoulSilver Connect builds Professor Oak stands where Red did at Mt. Silver.
/// The build has his team and lines; his battle picture is written at launch, against the real ROMs.
/// </summary>
public class OakAtMtSilverTests
{
    [Theory]
    [InlineData("heartgold")]
    [InlineData("soulsilver")]
    public void OaksPicture_GoesIntoEveryLaunch(string gameId)
    {
        var path = gameId == "heartgold" ? TestRoms.HeartGold : TestRoms.SoulSilver;
        var rom = TestRoms.Read(path);
        var basePatch = Gen4ConnectPatchService.Resolve(gameId, rom);
        Assert.NotNull(basePatch);

        var (edits, results) = PlayerGraphicsPatchService.BuildConnectFixedGraphicsEdits(gameId, basePatch.Patched);

        Assert.NotEmpty(edits);
        Assert.All(results, result => Assert.True(result.Applied, $"{result.Slot}: {result.Reason}"));
        // And it changes the picture that was there.
        Assert.Contains(edits, edit => !basePatch.Patched.AsSpan(edit.Offset, edit.Data.Length).SequenceEqual(edit.Data));

        var launch = LaunchPatchService.Build(gameId, rom, averageLevel: 40, homeRegion: "kanto", randomSeed: 3, romPath: path);
        Assert.DoesNotContain(launch.Summary.Warnings, warning => warning.StartsWith("[connect-graphics"));
    }

    [Fact]
    public void OtherGames_HaveNoFixedPictures()
    {
        var (edits, results) = PlayerGraphicsPatchService.BuildConnectFixedGraphicsEdits("platinum", []);
        Assert.Empty(edits);
        Assert.Empty(results);
    }
}
