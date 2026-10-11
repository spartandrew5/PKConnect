using PKConnect.Core.formats;
using PKConnect.Core.launch;
using PKConnect.Core.regions;
using PKHeX.Core;

namespace PKConnect.Core.Tests.regions;

/// <summary>The games and regions the app's screens are built from.</summary>
public class ConnectCatalogTests
{
    [Fact]
    public void EveryGame_IsSetInAKnownRegion_AndCanBePlayed()
    {
        var catalog = ConnectCatalog.Get();
        var regions = catalog.Regions.Select(region => region.Id).ToHashSet();

        Assert.Equal(["kanto", "johto", "hoenn", "sinnoh", "unova"], catalog.Regions.Select(region => region.Id));
        Assert.All(catalog.Regions, region => Assert.Equal(8, region.Badges.Length));
        Assert.All(catalog.Games, game =>
        {
            Assert.Contains(game.Region, regions);
            Assert.Equal(game.Region, game.Regions[0]);
            Assert.Equal(game.Region, ConnectRegions.ForGameId(game.Id));
            Assert.True(game.Playable, $"{game.Id}: no base save, or no launch patch");
        });
        Assert.Equal(catalog.Games.Count, catalog.Games.Select(game => game.Id).Distinct().Count());
    }

    [Fact]
    public void EveryBaseSave_IsRecognisedAsItsGame()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "connect-data", "base-saves");
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            Assert.True(SaveFiles.TryLoad(File.ReadAllBytes(path), out var save, path));
            Assert.True(Path.GetFileNameWithoutExtension(path) == ConnectCatalog.GameIdFor(save!), $"{Path.GetFileName(path)}: taken for {ConnectCatalog.GameIdFor(save!)} (version {save!.Version}, type {save.GetType().Name}, path {save.Metadata.FilePath})");
        }
    }

    [Fact]
    public void AGameOfTheCatalog_SaysWhatCanLiveInIt()
    {
        Assert.Equal(151, ConnectCatalog.Game("yellow")!.MaxSpecies);
        Assert.Equal(386, ConnectCatalog.Game("Emerald")!.MaxSpecies);
        // The Connect builds of the Gen 4 games reach Gen 5.
        Assert.Equal(649, ConnectCatalog.Game("platinum")!.MaxSpecies);
        Assert.Equal(["johto", "kanto"], ConnectCatalog.Game("heartgold")!.Regions);
        Assert.Null(ConnectCatalog.Game("ruby"));
        Assert.Null(ConnectCatalog.Game(null));
    }
}