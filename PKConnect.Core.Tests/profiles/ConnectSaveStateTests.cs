using PKConnect.Core.profiles;
using PKHeX.Core;

namespace PKConnect.Core.Tests.profiles;

/// <summary>The Connect state written into the base saves that come with the program.</summary>
public class ConnectSaveStateTests
{
    private static SaveFile BaseSave(string game)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "connect-data", "base-saves");
        var path = Directory.EnumerateFiles(folder, $"{game}.*").Single();
        Assert.True(SaveUtil.TryGetSaveFile(File.ReadAllBytes(path), out var save, path), $"{game}.sav is not a save PKHeX reads");
        return save!;
    }

    // Under its own name: an expansion FireRed/LeafGreen save is only told from Emerald's by it.
    private static SaveFile Reload(SaveFile save)
    {
        Assert.True(SaveUtil.TryGetSaveFile(save.Write().ToArray(), out var again, save.Metadata.FilePath));
        return again!;
    }

    [Theory]
    [InlineData("emerald")]
    [InlineData("firered")]
    [InlineData("leafgreen")]
    [InlineData("crystal")]
    [InlineData("yellow")]
    [InlineData("platinum")]
    [InlineData("heartgold")]
    [InlineData("soulsilver")]
    [InlineData("emeraldexpansion")]
    [InlineData("fireredexpansion")]
    [InlineData("leafgreenexpansion")]
    public void HomeRegionAndConqueredRegions_SurviveBeingWrittenToTheFile(string game)
    {
        var save = BaseSave(game);
        Assert.True(ConnectSaveState.IsSupported(save));

        Assert.True(ConnectSaveState.Apply(save, "Sinnoh", ["kanto", "hoenn", "nowhere"]));

        var written = Reload(save);
        Assert.Equal((4, 2), ConnectSaveState.Read(written));
        // Writing the same again changes nothing.
        Assert.False(ConnectSaveState.Apply(written, "sinnoh", ["hoenn", "kanto"]));
    }

    [Fact]
    public void ConqueredRegions_AreOnlyEverAdded()
    {
        var save = BaseSave("emerald");
        ConnectSaveState.Apply(save, "kanto", ["kanto", "johto"]);

        // A later write that knows of fewer regions lowers the count but leaves the flags.
        ConnectSaveState.Apply(save, "kanto", ["kanto"]);

        var flags = (IEventFlagArray)save;
        Assert.True(flags.GetEventFlag(2304));  // kanto
        Assert.True(flags.GetEventFlag(2305));  // johto: still set
        Assert.Equal((1, 1), ConnectSaveState.Read(save));
    }

    [Fact]
    public void NoHomeRegion_IsWrittenAsNone()
    {
        var save = BaseSave("crystal");
        ConnectSaveState.Apply(save, "johto", []);

        ConnectSaveState.Apply(save, null, []);

        Assert.Equal((0, 0), ConnectSaveState.Read(save));
    }

    [Fact]
    public void TheRestOfTheSave_IsUntouched()
    {
        var before = BaseSave("firered");
        var after = BaseSave("firered");

        ConnectSaveState.Apply(after, "hoenn", ["hoenn"]);

        Assert.Equal(before.OT, after.OT);
        Assert.Equal(before.PartyCount, after.PartyCount);
        var changedFlags = Enumerable.Range(0, ((IEventFlagArray)before).EventFlagCount)
            .Count(i => ((IEventFlagArray)before).GetEventFlag(i) != ((IEventFlagArray)after).GetEventFlag(i));
        Assert.InRange(changedFlags, 0, 1); // the Hoenn champion flag, unless the base save had it already
    }

    [Fact]
    public void AnExpansionFireRedSave_KeepsItsStateWhereTheBuildReadsIt()
    {
        // PKHeX reads the save as Emerald, whose variables start 12 bytes later than this build's.
        var save = BaseSave("fireredexpansion");
        ConnectSaveState.Apply(save, "hoenn", ["kanto"]);

        var asEmerald = (IEventWorkArray<ushort>)save;
        Assert.Equal((3, 1), ConnectSaveState.Read(save));
        Assert.Equal(3, asEmerald.GetWork(16632 - 0x4000 - 6));
        Assert.True(((IEventFlagArray)save).GetEventFlag(2243)); // FireRed's flag for Kanto, not Emerald's
    }

    [Theory]
    [InlineData("black")]
    [InlineData("white2")]
    public void TheGen5Games_KeepNoStateInTheSave(string game)
    {
        // Their ROM is edited at launch for a visitor; nothing is read from the save.
        Assert.False(ConnectSaveState.IsSupported(BaseSave(game)));
    }
}