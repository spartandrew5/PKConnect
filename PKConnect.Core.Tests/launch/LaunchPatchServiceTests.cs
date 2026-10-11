using PKConnect.Core.launch;
using PKConnect.Core.patching;

namespace PKConnect.Core.Tests.launch;

public class LaunchPatchServiceTests
{
    [Fact]
    public void UnsupportedGame_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => LaunchPatchService.Build("sword", new byte[16], averageLevel: 30, homeRegion: "kanto"));
    }

    [Theory]
    [InlineData("hoenn")] // at home
    [InlineData(null)]    // no home region known: nowhere is a visit
    public void HomeLaunch_IsAnEmptyPatch(string? homeRegion)
    {
        var rom = new byte[0x1000];

        var patch = LaunchPatchService.Build("emerald", rom, averageLevel: 30, homeRegion);

        Assert.False(patch.Summary.IsVisiting);
        Assert.False(patch.Summary.BasePatchApplied);
        Assert.Equal(".ips", patch.Extension);
        Assert.Empty(IpsPatchService.Parse(patch.Bytes));
    }

    [Fact]
    public void VisitingLaunch_WithTheWrongRom_SaysSo()
    {
        var notEmerald = new byte[0x1000000];

        var error = Assert.Throws<InvalidOperationException>(() => LaunchPatchService.Build("emerald", notEmerald, averageLevel: 30, homeRegion: "kanto"));

        Assert.Contains("unmodified", error.Message);
    }

    [Fact]
    public void VisitingLaunch_AgainstTheRealRom_PatchesTheGameAndItsGyms()
    {
        var vanilla = TestRoms.Read(TestRoms.Emerald);

        var patch = LaunchPatchService.Build("emerald", vanilla, averageLevel: 42, homeRegion: "kanto", randomSeed: 7);

        Assert.True(patch.Summary.BasePatchApplied);
        Assert.Equal(8, patch.Summary.TeamsGenerated);
        Assert.Empty(patch.Summary.Warnings);

        // The patch turns the vanilla ROM into the base build plus this launch's teams.
        var basePatch = BasePatchService.Resolve("emerald", vanilla);
        var expected = BasePatchService.ApplyToVanilla(basePatch, vanilla);
        var (gymEdits, _) = ConnectTeamPatchService.BuildGymEdits(basePatch, 42, randomSeed: 7);
        IpsPatchService.ApplyInPlace(expected, gymEdits);

        var launched = (byte[])vanilla.Clone();
        IpsPatchService.ApplyInPlace(launched, IpsPatchService.Parse(patch.Bytes));
        Assert.Equal(expected, launched);

        // A leader's first Pokémon is around the traveller's level.
        var firstGym = basePatch.Manifest.Gyms[0];
        Assert.InRange(launched[firstGym.PartyOffset + 2], 40, 44);
    }

    [Fact]
    public void WriteFor_PutsThePatchBesideTheRom_AndRemovesAStaleOneOfTheOtherFormat()
    {
        var dir = Directory.CreateTempSubdirectory("pkconnect-launch-");
        try
        {
            var rom = Path.Combine(dir.FullName, "Emerald.gba");
            File.WriteAllBytes(rom, new byte[0x1000]);
            File.WriteAllBytes(Path.ChangeExtension(rom, ".bps"), [1, 2, 3]);

            var result = LaunchPatchService.WriteFor("emerald", rom, averageLevel: null, homeRegion: "hoenn");

            Assert.True(result.Success);
            Assert.True(result.HomeRegionLaunch);
            Assert.Equal(Path.ChangeExtension(rom, ".ips"), result.PatchPath);
            Assert.True(File.Exists(result.PatchPath));
            Assert.False(File.Exists(Path.ChangeExtension(rom, ".bps")));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}