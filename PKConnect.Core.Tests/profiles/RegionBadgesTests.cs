using PKConnect.Core.formats;
using PKConnect.Core.profiles;
using PKHeX.Core;

namespace PKConnect.Core.Tests.profiles;

/// <summary>A trainer's gym badges, kept in step across their saves of one region.</summary>
public class RegionBadgesTests
{
    private static readonly IReadOnlyDictionary<string, bool[]> NoLedger = new Dictionary<string, bool[]>();

    private static SaveFile BaseSave(string game)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "connect-data", "base-saves");
        var path = Directory.EnumerateFiles(folder, $"{game}.*").Single();
        Assert.True(SaveFiles.TryLoad(File.ReadAllBytes(path), out var save, path));
        return save!;
    }

    private static SaveFile Reload(SaveFile save)
    {
        Assert.True(SaveFiles.TryLoad(save.Write().ToArray(), out var again, save.Metadata.FilePath));
        return again!;
    }

    private static bool[] Badges(params int[] earned) => [.. Enumerable.Range(0, 8).Select(earned.Contains)];

    [Theory]
    [InlineData("yellow", "kanto")]
    [InlineData("firered", "kanto")]
    [InlineData("leafgreen", "kanto")]
    [InlineData("fireredexpansion", "kanto")]
    [InlineData("emerald", "hoenn")]
    [InlineData("emeraldexpansion", "hoenn")]
    [InlineData("crystal", "johto", "kanto")]
    [InlineData("heartgold", "johto", "kanto")]
    [InlineData("platinum", "sinnoh")]
    [InlineData("white2", "unova")]
    public void ASave_HoldsTheBadgesOfItsGamesRegions(string game, params string[] regions)
    {
        Assert.Equal(regions, RegionBadges.RegionsFor(BaseSave(game)));
    }

    [Theory]
    [InlineData("yellow", "kanto")]
    [InlineData("firered", "kanto")]
    [InlineData("fireredexpansion", "kanto")]
    [InlineData("emerald", "hoenn")]
    [InlineData("emeraldexpansion", "hoenn")]
    [InlineData("crystal", "johto")]
    [InlineData("crystal", "kanto")]
    [InlineData("platinum", "sinnoh")]
    [InlineData("heartgold", "johto")]
    [InlineData("heartgold", "kanto")]
    [InlineData("soulsilver", "kanto")]
    [InlineData("black", "unova")]
    [InlineData("white2", "unova")]
    public void Badges_SurviveBeingWrittenToTheFile(string game, string region)
    {
        var save = BaseSave(game);
        var target = Badges(0, 1, 4, 7);

        Assert.True(RegionBadges.Apply(save, region, target));

        var written = Reload(save);
        Assert.Equal(target, RegionBadges.Read(written, region));
        // Writing the same again changes nothing.
        Assert.False(RegionBadges.Apply(written, region, target));
        // Taking one away works too.
        RegionBadges.Apply(written, region, Badges(0, 4, 7));
        Assert.Equal(Badges(0, 4, 7), RegionBadges.Read(Reload(written), region));
    }

    [Fact]
    public void TheGameBoyGames_ShowTheirBadgesOnTheTrainerCard()
    {
        // The flags say a leader was beaten; the badge itself is a bit elsewhere.
        var yellow = (SAV1)BaseSave("yellow");
        RegionBadges.Apply(yellow, "kanto", Badges(0, 2));
        Assert.Equal(0b101, yellow.Badges);

        // Crystal keeps Kanto's eight after Johto's.
        var crystal = (SAV2)BaseSave("crystal");
        RegionBadges.Apply(crystal, "johto", Badges(0));
        RegionBadges.Apply(crystal, "kanto", Badges(1));
        Assert.Equal(0b10_0000_0001, crystal.Badges);
    }

    [Fact]
    public void TheGbaGames_KeepTheLeadersStoryFlagInStep()
    {
        var emerald = BaseSave("emerald");
        RegionBadges.Apply(emerald, "hoenn", Badges(0));

        var flags = (IEventFlagArray)emerald;
        Assert.True(flags.GetEventFlag(2151));  // FLAG_BADGE01_GET
        Assert.True(flags.GetEventFlag(1264));  // FLAG_DEFEATED_RUSTBORO_GYM
    }

    [Fact]
    public void ARegionOfAnotherGame_IsLeftAlone()
    {
        var emerald = BaseSave("emerald");
        Assert.Null(RegionBadges.Read(emerald, "kanto"));
        Assert.False(RegionBadges.Apply(emerald, "kanto", Badges(0, 1)));
    }

    [Fact]
    public void TheFirstSaveOfARegion_SetsTheTrainersBadges()
    {
        var yellow = BaseSave("yellow");
        RegionBadges.Apply(yellow, "kanto", Badges(0, 1));

        var result = RegionBadges.Reconcile(yellow, NoLedger, null);

        Assert.False(result.SaveChanged);
        Assert.Equal(Badges(0, 1), result.Ledger["kanto"]);
        Assert.Equal(Badges(0, 1), result.Snapshot["kanto"]);
    }

    [Fact]
    public void ANewSaveOfTheSameRegion_TakesTheTrainersBadges()
    {
        // Two badges earned in Yellow; the trainer then travels to FireRed.
        var ledger = new Dictionary<string, bool[]> { ["kanto"] = Badges(0, 1) };
        var fireRed = BaseSave("firered");

        var result = RegionBadges.Reconcile(fireRed, ledger, null);

        Assert.True(result.SaveChanged);
        Assert.Equal(Badges(0, 1), RegionBadges.Read(fireRed, "kanto"));
        Assert.Empty(result.Ledger);
        Assert.Equal(Badges(0, 1), result.Snapshot["kanto"]);
    }

    [Fact]
    public void ABadgeEarnedInTheGame_JoinsTheTrainersBadges()
    {
        var ledger = new Dictionary<string, bool[]> { ["kanto"] = Badges(0, 1) };
        var snapshot = new Dictionary<string, bool[]> { ["kanto"] = Badges(0, 1) };
        var fireRed = BaseSave("firered");
        RegionBadges.Apply(fireRed, "kanto", Badges(0, 1, 2)); // the player beat Lt. Surge

        var result = RegionBadges.Reconcile(fireRed, ledger, snapshot);

        Assert.False(result.SaveChanged);
        Assert.Equal(Badges(0, 1, 2), result.Ledger["kanto"]);
        Assert.Equal(Badges(0, 1, 2), result.Snapshot["kanto"]);
    }

    [Fact]
    public void ABadgeTakenAwayElsewhere_GoesAwayHere()
    {
        // The save had three when last seen; the trainer's ledger has since lost one.
        var ledger = new Dictionary<string, bool[]> { ["kanto"] = Badges(0, 1) };
        var snapshot = new Dictionary<string, bool[]> { ["kanto"] = Badges(0, 1, 2) };
        var fireRed = BaseSave("firered");
        RegionBadges.Apply(fireRed, "kanto", Badges(0, 1, 2));

        var result = RegionBadges.Reconcile(fireRed, ledger, snapshot);

        Assert.True(result.SaveChanged);
        Assert.Equal(Badges(0, 1), RegionBadges.Read(fireRed, "kanto"));
        Assert.Empty(result.Ledger);
    }

    [Fact]
    public void OneRegionsBadges_DontTouchAnothers()
    {
        var ledger = new Dictionary<string, bool[]> { ["kanto"] = Badges(0, 1, 2) };
        var crystal = BaseSave("crystal");
        RegionBadges.Apply(crystal, "johto", Badges(0));

        var result = RegionBadges.Reconcile(crystal, ledger, null);

        Assert.Equal(Badges(0), RegionBadges.Read(crystal, "johto"));
        Assert.Equal(Badges(0, 1, 2), RegionBadges.Read(crystal, "kanto"));
        Assert.Equal(Badges(0), result.Ledger["johto"]); // Johto is new to the trainer
        Assert.False(result.Ledger.ContainsKey("kanto"));
    }

    [Fact]
    public void ADsGame_TakesTheTrainersBadges_AndKeepsItsRegionsApart()
    {
        var ledger = new Dictionary<string, bool[]> { ["kanto"] = Badges(0, 1), ["johto"] = Badges(3) };
        var heartGold = BaseSave("heartgold");

        var result = RegionBadges.Reconcile(heartGold, ledger, null);

        Assert.True(result.SaveChanged);
        var written = Reload(heartGold);
        Assert.Equal(Badges(3), RegionBadges.Read(written, "johto"));
        Assert.Equal(Badges(0, 1), RegionBadges.Read(written, "kanto"));
        // The games count them from the trainer's record.
        Assert.Equal(0b1000, ((SAV4HGSS)written).Badges);
        Assert.Equal(0b11, ((SAV4HGSS)written).Badges16);
    }

    [Theory]
    [InlineData("platinum", "sinnoh", 1360 + 787)]
    [InlineData("heartgold", "johto", 0x88A)]
    [InlineData("heartgold", "kanto", 0x892)]
    [InlineData("soulsilver", "kanto", 0x892)]
    public void InAConnectBuild_ABadgeFromElsewhere_BringsTheLeadersPrizeWithIt(string game, string region, int firstPrizeFlag)
    {
        var save = BaseSave(game);
        var flags = (IEventFlagArray)save;
        // A badge the save earned itself, its prize still waiting.
        RegionBadges.Apply(save, region, Badges(0));
        flags.SetEventFlag(firstPrizeFlag, false);

        RegionBadges.Apply(save, region, Badges(0, 2));

        var written = (IEventFlagArray)Reload(save);
        Assert.False(written.GetEventFlag(firstPrizeFlag));     // earned here: left as the game had it
        Assert.True(written.GetEventFlag(firstPrizeFlag + 2));  // arrived: nothing left to hand over
        Assert.False(written.GetEventFlag(firstPrizeFlag + 1)); // no badge, no prize

        // A badge taken away takes its prize away too, so it can be earned properly.
        RegionBadges.Apply(save, region, Badges(0));
        Assert.False(((IEventFlagArray)Reload(save)).GetEventFlag(firstPrizeFlag + 2));
    }
}
