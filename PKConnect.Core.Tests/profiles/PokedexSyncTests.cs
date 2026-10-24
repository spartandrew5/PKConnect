using PKConnect.Core.formats;
using PKConnect.Core.profiles;
using PKHeX.Core;

namespace PKConnect.Core.Tests.profiles;

/// <summary>A trainer's Pokédex, carried from the saves they have into the one they are in.</summary>
public class PokedexSyncTests
{
    private const ushort Bulbasaur = 1, Pikachu = 25, Lugia = 249, Blaziken = 257, Lucario = 448, Serperior = 497;

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

    /// <summary>Seen everywhere from Kanto to Unova; Bulbasaur, Blaziken and Serperior caught.</summary>
    private static PokedexSync.Known Journey() =>
        new([Bulbasaur, Pikachu, Lugia, Blaziken, Lucario, Serperior], [Bulbasaur, Blaziken, Serperior]);

    [Theory]
    // The newest species each game has a Pokédex entry for.
    [InlineData("yellow", 151)]
    [InlineData("crystal", 251)]
    [InlineData("firered", 386)]
    [InlineData("emerald", 386)]
    [InlineData("platinum", 493)]   // the Connect build has later species, but no entries for them
    [InlineData("heartgold", 493)]
    [InlineData("white", 649)]
    [InlineData("white2", 649)]
    public void ASave_LearnsWhatTheTrainerKnows_OfTheSpeciesItHasEntriesFor(string game, int newest)
    {
        var save = BaseSave(game);
        var before = PokedexSync.Read(save);
        var journey = Journey();

        var added = PokedexSync.Apply(save, journey);

        var after = PokedexSync.Read(Reload(save));
        foreach (var species in journey.Seen)
        {
            var hasEntry = species <= newest;
            Assert.Equal(hasEntry || before.Seen.Contains(species), after.Seen.Contains(species));
            if (journey.Caught.Contains(species))
                Assert.Equal(hasEntry || before.Caught.Contains(species), after.Caught.Contains(species));
        }
        // (A starting save that has been played in may know some of it already.)
        var news = journey.Seen.Any(s => s <= newest && (!before.Seen.Contains(s) || (journey.Caught.Contains(s) && !before.Caught.Contains(s))));
        Assert.Equal(news, added > 0);
        // Nothing it knew is lost, and nothing nobody saw appears.
        Assert.Superset(before.Seen, after.Seen);
        Assert.Superset(before.Caught, after.Caught);
        Assert.Subset(new HashSet<ushort>(before.Seen.Union(journey.Seen)), after.Seen);

        // Told the same again, it has nothing to add.
        Assert.Equal(0, PokedexSync.Apply(save, journey));
    }

    [Fact]
    public void WhatWasOnlySeen_IsNotCaught()
    {
        var save = BaseSave("firered");
        var before = PokedexSync.Read(save);

        PokedexSync.Apply(save, new([Pikachu], []));

        var after = PokedexSync.Read(Reload(save));
        Assert.Contains(Pikachu, after.Seen);
        Assert.Equal(before.Caught.Contains(Pikachu), after.Caught.Contains(Pikachu));
    }

    [Fact]
    public void FromOneGameToAnother()
    {
        // Caught in FireRed, then the trainer arrives in Yellow for the first time.
        var fireRed = BaseSave("firered");
        PokedexSync.Apply(fireRed, new([Bulbasaur, Pikachu, Blaziken], [Bulbasaur, Blaziken]));
        var yellow = BaseSave("yellow");

        PokedexSync.Apply(yellow, PokedexSync.Read(fireRed));

        var known = PokedexSync.Read(Reload(yellow));
        Assert.Contains(Bulbasaur, known.Caught);
        Assert.Contains(Pikachu, known.Seen);
        Assert.DoesNotContain(Blaziken, known.Seen); // Yellow has never heard of Hoenn
    }

    [Theory]
    [InlineData("platinum")]
    [InlineData("white")]
    public void InTheDsGames_ASeenSpeciesHasAGenderToShow(string game)
    {
        var save = BaseSave(game);
        PokedexSync.Apply(save, new([Lucario], []));
        var written = Reload(save);

        Assert.True(written.GetSeen(Lucario));
        if (written is SAV5 sav5)
            Assert.True(sav5.Zukan.GetDisplayedAny(Lucario));
    }
}
