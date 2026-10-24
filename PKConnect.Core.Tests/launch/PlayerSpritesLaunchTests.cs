using PKConnect.Core.launch;
using PKConnect.Core.patching;

namespace PKConnect.Core.Tests.launch;

/// <summary>The trainer's chosen character, written into the game at launch, against the real ROMs.</summary>
public class PlayerSpritesLaunchTests
{
    private static string RomOf(string gameId) => gameId switch
    {
        "yellow" => TestRoms.Yellow,
        "emerald" => TestRoms.Emerald,
        "firered" => TestRoms.FireRed,
        "leafgreen" => TestRoms.LeafGreen,
        "platinum" => TestRoms.Platinum,
        "heartgold" => TestRoms.HeartGold,
        "soulsilver" => TestRoms.SoulSilver,
        "white" => TestRoms.White,
        "white2" => TestRoms.White2,
        "emeraldexpansion" => TestRoms.EmeraldExpansion,
        "fireredexpansion" => TestRoms.FireRedExpansion,
        _ => throw new ArgumentException(gameId),
    };

    private static LaunchPatchService.LaunchPatch Build(string gameId, string? character, string homeRegion, out byte[] rom)
    {
        var path = RomOf(gameId);
        rom = TestRoms.Read(path);
        return LaunchPatchService.Build(gameId, rom, averageLevel: 40, homeRegion, randomSeed: 3, romPath: path, character: character);
    }

    [Theory]
    // Each character in a game of the generation they are from...
    [InlineData("yellow", "red")]
    [InlineData("emerald", "brendan")]
    [InlineData("emerald", "may")]
    [InlineData("firered", "red")]
    [InlineData("leafgreen", "leaf")]
    [InlineData("platinum", "lucas")]
    [InlineData("platinum", "dawn")]
    [InlineData("heartgold", "ethan")]
    [InlineData("soulsilver", "lyra")]
    [InlineData("white", "hilbert")]
    [InlineData("white", "hilda")]
    [InlineData("white2", "nate")]
    [InlineData("white2", "rosa")]
    [InlineData("emeraldexpansion", "brendan")]
    [InlineData("fireredexpansion", "red")]
    // ...and in another game of that generation, or of the Game Boy generation next to it.
    [InlineData("firered", "brendan")]
    [InlineData("emerald", "red")]
    [InlineData("heartgold", "lucas")]
    [InlineData("platinum", "ethan")]
    [InlineData("white", "nate")]
    public void ACharactersSprites_GoIntoTheGame(string gameId, string character)
    {
        var plain = Build(gameId, null, "kanto", out var rom);
        var dressed = Build(gameId, character, "kanto", out _);

        Assert.True(dressed.Summary.GraphicsApplied > 0, $"nothing was written: {string.Join(" | ", dressed.Summary.Warnings)}");
        Assert.True(dressed.Summary.GraphicsSkipped == 0, $"skipped: {string.Join(" | ", dressed.Summary.Warnings.Where(w => w.StartsWith("[graphics")))}");
        Assert.Equal(0, plain.Summary.GraphicsApplied);

        // The game still comes out whole, and differs from the one launched without a character.
        var dressedRom = dressed.Extension == ".ips" ? null : BpsPatchService.Decode(rom, dressed.Bytes);
        if (dressedRom is not null)
        {
            var plainRom = BpsPatchService.Decode(rom, plain.Bytes);
            Assert.Equal(plainRom.Length, dressedRom.Length);
            Assert.False(plainRom.AsSpan().SequenceEqual(dressedRom));
        }
        else
        {
            Assert.NotEqual(plain.Bytes, dressed.Bytes);
        }
    }

    [Theory]
    [InlineData("yellow", "nate")]   // Nate has no Game Boy sprites
    [InlineData("white", "brendan")]
    [InlineData("emerald", "nobody")]
    public void ACharacterWithoutSpritesForTheGame_ChangesNothing_AndIsNoWarning(string gameId, string character)
    {
        var plain = Build(gameId, null, "kanto", out _);
        var dressed = Build(gameId, character, "kanto", out _);

        Assert.Equal(0, dressed.Summary.GraphicsApplied);
        Assert.Equal(0, dressed.Summary.GraphicsSkipped);
        Assert.DoesNotContain(dressed.Summary.Warnings, warning => warning.StartsWith("[graphics"));
        Assert.Equal(plain.Bytes, dressed.Bytes);
    }

    [Fact]
    public void AGen2CharactersBackSprite_DoesNotFitYellow_AndSaysSo()
    {
        // Yellow's back sprite is 32x32 and Gen 2's is 48x48: the rest of Kris goes in, the back stays Red's.
        var patch = Build("yellow", "kris", "johto", out _);

        Assert.True(patch.Summary.GraphicsApplied > 0);
        Assert.Equal(1, patch.Summary.GraphicsSkipped);
        Assert.Contains(patch.Summary.Warnings, warning => warning.StartsWith("[graphics:BackImage]"));
    }

    [Fact]
    public void AtHome_TheSpritesAreStillWritten()
    {
        // No base patch on a home launch of a GBA game, but the trainer still looks like their character.
        var patch = Build("emerald", "may", "hoenn", out _);

        Assert.False(patch.Summary.IsVisiting);
        Assert.True(patch.Summary.GraphicsApplied > 0);
        Assert.Equal(0, patch.Summary.GraphicsSkipped);
    }
}