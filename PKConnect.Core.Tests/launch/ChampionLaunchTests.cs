using PKConnect.Core.champions;
using PKConnect.Core.launch;
using PKConnect.Core.patching;
using PKHeX.Core;

namespace PKConnect.Core.Tests.launch;

/// <summary>The region's current champion in the launch patch, checked against the retail ROMs.</summary>
public class ChampionLaunchTests
{
    private static readonly ChampionTeamDTO Sky = new("Sky",
    [
        new(6, 60, Moves: ["Flamethrower", "Fly"], HeldItem: "Charcoal"),
        new(25, 58, Moves: ["Thunderbolt"]),
        new(445, 62, Moves: ["Earthquake"]), // Garchomp: not in these games
    ]);

    private static byte[] Launch(byte[] vanilla, LaunchPatchService.LaunchPatch patch)
    {
        var rom = (byte[])vanilla.Clone();
        IpsPatchService.ApplyInPlace(rom, IpsPatchService.Parse(patch.Bytes));
        return rom;
    }

    [Fact]
    public void Visiting_TheChampionSlotsHoldTheTeamAndTheName()
    {
        var vanilla = TestRoms.Read(TestRoms.Emerald);

        var patch = LaunchPatchService.Build("emerald", vanilla, averageLevel: 50, homeRegion: "kanto", Sky, randomSeed: 3);

        Assert.Equal(1, patch.Summary.ChampionApplied);
        Assert.Equal(0, patch.Summary.ChampionSkipped);
        var rom = Launch(vanilla, patch);
        var slot = BasePatchService.Load("emerald").Manifest.ChampionSlots[0];

        Assert.Equal(3, rom[slot.PartySizeOffset!.Value]);
        // TrainerMon: iv u16, level u8, pad, species u16 (Gen 3 internal id), item u16, moves
        Assert.Equal(SpeciesConverter.GetInternal3(6), BitConverter.ToUInt16(rom, slot.PartyOffset + 4));
        Assert.Equal(53, BitConverter.ToUInt16(rom, slot.PartyOffset + 8)); // Flamethrower
        // Levels: the champion's own curve, re-centered on the traveller's 50 plus the bonus of 4.
        var levels = Enumerable.Range(0, 3).Select(i => (int)rom[slot.PartyOffset + i * slot.RecordSize + 2]).ToArray();
        Assert.Equal(54, (int)Math.Round(levels.Average()));
        Assert.True(levels[2] > levels[1]);

        var nameOffset = slot.PartySizeOffset.Value - GymTrainerTableData.PartySizeOffset + GymTrainerTableData.TrainerNameOffset;
        Assert.Equal("Sky", StringConverter3.GetString(rom.AsSpan(nameOffset, GymTrainerTableData.TrainerNameLength), (int)LanguageID.English));
    }

    [Theory]
    [InlineData("emerald", "hoenn", 1)]
    [InlineData("firered", "kanto", 3)] // one champion battle per rival starter
    [InlineData("leafgreen", "kanto", 3)]
    public void AtHome_OnGba_TheVanillaChampionIsPointedAtTheTeam(string gameId, string home, int battles)
    {
        var vanilla = TestRoms.Read(gameId switch { "emerald" => TestRoms.Emerald, "firered" => TestRoms.FireRed, _ => TestRoms.LeafGreen });

        var patch = LaunchPatchService.Build(gameId, vanilla, averageLevel: 50, homeRegion: home, Sky);

        Assert.False(patch.Summary.BasePatchApplied);
        Assert.Equal(battles, patch.Summary.ChampionApplied);
        Assert.Empty(patch.Summary.Warnings);

        var rom = Launch(vanilla, patch);
        foreach (var trainerId in ChampionBattleConfigLoader.Load().GbaChampionTrainerIds[gameId])
        {
            var trainer = GymTrainerTableData.GetTrainerStructOffset(gameId, trainerId, rom);
            Assert.Equal(3, rom[trainer + GymTrainerTableData.PartySizeOffset]);
        }
        // Nothing but the champion changed: the patch is small.
        Assert.InRange(patch.Bytes.Length, 50, 2000);
    }

    [Fact]
    public void AtHome_OnYellow_TheChampionGetsItsOwnPartyZone()
    {
        var vanilla = TestRoms.Read(TestRoms.Yellow);

        var patch = LaunchPatchService.Build("yellow", vanilla, averageLevel: 50, homeRegion: "kanto", Sky);

        Assert.False(patch.Summary.BasePatchApplied);
        Assert.True(patch.Summary.ChampionApplied > 0);
        Assert.Equal(0, patch.Summary.ChampionSkipped);
        Assert.NotEqual(vanilla, Launch(vanilla, patch));
    }

    [Fact]
    public void WithNoChampion_AHomeLaunchLeavesTheGameAlone()
    {
        var vanilla = TestRoms.Read(TestRoms.Emerald);

        var patch = LaunchPatchService.Build("emerald", vanilla, averageLevel: 50, homeRegion: "hoenn", champion: null);

        Assert.Equal(vanilla, Launch(vanilla, patch));
    }
}