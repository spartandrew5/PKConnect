using PKConnect.Core.champions;
using PKConnect.Core.launch;
using PKConnect.Core.nds;
using PKConnect.Core.patching;

namespace PKConnect.Core.Tests.launch;

/// <summary>The launch patch for the games past the Game Boy and GBA ones, built against the real ROMs.</summary>
public class LaunchGamesTests
{
    private const int Garchomp = 445;

    private static readonly ChampionTeamDTO Champion = new("Cynthia",
    [
        new(Garchomp, 62, Moves: ["Dragon Rush", "Earthquake", "Flamethrower", "Giga Impact"], HeldItem: "Sitrus Berry"),
        new(448, 60, Moves: ["Aura Sphere", "Psychic"]),
    ]);

    private static byte[] Member(byte[] rom, string path, int index)
    {
        var file = NitroFs.ResolveFile(rom, path)!.Value;
        var narc = rom.AsSpan(file.Offset, file.Length).ToArray();
        var (offset, length) = NarcArchive.GetMemberRange(narc, index);
        return narc.AsSpan(offset, length).ToArray();
    }

    public static TheoryData<string, int> Gen4Games => new()
    {
        { "platinum", 8 },
        { "heartgold", 16 },  // Johto's eight leaders, then Kanto's
        { "soulsilver", 16 },
    };

    private static string RomOf(string gameId) => gameId switch
    {
        "platinum" => TestRoms.Platinum,
        "heartgold" => TestRoms.HeartGold,
        "soulsilver" => TestRoms.SoulSilver,
        "white" => TestRoms.White,
        "white2" => TestRoms.White2,
        "emeraldexpansion" => TestRoms.EmeraldExpansion,
        "fireredexpansion" => TestRoms.FireRedExpansion,
        "leafgreenexpansion" => TestRoms.LeafGreenExpansion,
        _ => throw new ArgumentException(gameId),
    };

    [Theory]
    [MemberData(nameof(Gen4Games))]
    public void Gen4_VisitingLaunch_FillsEveryGymAndTheChampion(string gameId, int gyms)
    {
        var rom = TestRoms.Read(RomOf(gameId));
        var manifest = Gen4ConnectPatchService.LoadManifest(gameId)!;

        var patch = LaunchPatchService.Build(gameId, rom, averageLevel: 50, homeRegion: "hoenn", Champion, randomSeed: 7);

        Assert.Equal(".bps", patch.Extension);
        Assert.Empty(patch.Summary.Warnings);
        Assert.True(patch.Summary.IsVisiting);
        Assert.True(patch.Summary.BasePatchApplied);
        Assert.Equal(gyms, patch.Summary.TeamsGenerated);
        Assert.Equal(manifest.ChampionTrainerIds.Length, patch.Summary.ChampionApplied);

        // The one patch, applied to the retail ROM, gives the Connect build with this launch's teams in it.
        var patched = BpsPatchService.Decode(rom, patch.Bytes);
        foreach (var championId in manifest.ChampionTrainerIds)
        {
            var party = Member(patched, manifest.TrpokePath, championId);
            Assert.Equal(Garchomp, BitConverter.ToUInt16(party, 4));
        }
        var leader = Member(patched, manifest.TrpokePath, manifest.GymTrainerIds[0]);
        Assert.InRange(BitConverter.ToUInt16(leader, 2), 46, 54); // level, around the traveller's
        Assert.InRange(BitConverter.ToUInt16(leader, 4), 1, 649);
    }

    [Fact]
    public void Gen4_HomeLaunch_KeepsTheLeadersTeams_AndStillSetsTheChampion()
    {
        var rom = TestRoms.Read(TestRoms.Platinum);

        var patch = LaunchPatchService.Build("platinum", rom, averageLevel: 50, homeRegion: "sinnoh", Champion, randomSeed: 7);

        Assert.False(patch.Summary.IsVisiting);
        Assert.True(patch.Summary.BasePatchApplied); // one build serves both; it reads the home region from the save
        Assert.Equal(0, patch.Summary.TeamsGenerated);
        Assert.True(patch.Summary.ChampionApplied > 0);
    }

    [Fact]
    public void Gen4_ARomOfAnotherRevision_IsRefusedWithAReason()
    {
        var rom = TestRoms.Read(TestRoms.Platinum);
        var other = (byte[])rom.Clone();
        other[0x200] ^= 0xFF;

        var ex = Assert.Throws<InvalidOperationException>(() => LaunchPatchService.Build("platinum", other, 50, "hoenn"));
        Assert.Contains("revision", ex.Message);
    }

    [Theory]
    [InlineData("white", 11)]  // Striaton's three, six more, Opelucid's two
    [InlineData("white2", 8)]
    public void Gen5_VisitingLaunch_EditsTheRetailRom(string gameId, int gyms)
    {
        var rom = TestRoms.Read(RomOf(gameId));

        var patch = LaunchPatchService.Build(gameId, rom, averageLevel: 50, homeRegion: "kanto", Champion, randomSeed: 7);

        Assert.Equal(".bps", patch.Extension);
        Assert.Empty(patch.Summary.Warnings);
        Assert.False(patch.Summary.BasePatchApplied);
        Assert.Equal(gyms, patch.Summary.TeamsGenerated);
        Assert.True(patch.Summary.ChampionApplied > 0);
        // A patch of edits, not a copy of the ROM.
        Assert.InRange(patch.Bytes.Length, 1, 16 * 1024 * 1024);
        Assert.Equal(rom.Length, BpsPatchService.Decode(rom, patch.Bytes).Length);
    }

    [Theory]
    [InlineData("emeraldexpansion", 1)]
    [InlineData("fireredexpansion", 3)]   // one champion battle per rival starter
    [InlineData("leafgreenexpansion", 3)]
    public void Expansion_Launch_FindsTheBuildThroughItsElf(string gameId, int championBattles)
    {
        var romPath = RomOf(gameId);
        var rom = TestRoms.Read(romPath);

        var patch = LaunchPatchService.Build(gameId, rom, averageLevel: 50, homeRegion: "johto", Champion, randomSeed: 7, romPath: romPath);

        Assert.Equal(".bps", patch.Extension);
        Assert.Equal(championBattles, patch.Summary.ChampionApplied);
        Assert.Equal(0, patch.Summary.ChampionSkipped);
        // A build with the Connect port has its eight gyms filled; one without has none to fill.
        Assert.Contains(patch.Summary.TeamsGenerated, new[] { 0, 8 });
        Assert.Equal(0, patch.Summary.TeamsSkipped);
        Assert.Equal(rom.Length, BpsPatchService.Decode(rom, patch.Bytes).Length);
    }

    [Fact]
    public void Expansion_WithoutItsElf_IsRefusedWithAReason()
    {
        var rom = TestRoms.Read(TestRoms.EmeraldExpansion);
        var folder = Path.Combine(Path.GetTempPath(), $"pkconnect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var lonely = Path.Combine(folder, "pokeemerald.gba");
            File.WriteAllBytes(lonely, rom);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                LaunchPatchService.Build("emeraldexpansion", rom, 50, "johto", romPath: lonely));
            Assert.Contains(".elf", ex.Message);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("platinum")]
    [InlineData("heartgold")]
    [InlineData("soulsilver")]
    [InlineData("black")]
    [InlineData("white")]
    [InlineData("black2")]
    [InlineData("white2")]
    [InlineData("emeraldexpansion")]
    [InlineData("fireredexpansion")]
    [InlineData("leafgreenexpansion")]
    public void EveryGame_IsLaunchable(string gameId) => Assert.True(LaunchPatchService.IsSupportedGame(gameId));
}