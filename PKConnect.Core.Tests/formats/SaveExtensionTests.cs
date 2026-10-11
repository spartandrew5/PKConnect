using PKConnect.Core.formats;
using PKHeX.Core;
using PKVault.Core;

namespace PKConnect.Core.Tests.formats;

/// <summary>PKVault's own save wrapper, reading and writing the saves Connect's builds produce.</summary>
public class SaveExtensionTests
{
    private const int Victini = 494;

    public SaveExtensionTests() => ConnectModule.RegisterFormats();

    private static string Asset(string file) => Path.Combine(AppContext.BaseDirectory, "assets", "saves", file);

    private static SaveWrapper Load(string path)
    {
        Assert.True(SaveUtil.TryGetSaveFile(File.ReadAllBytes(path), out var save, path), $"{path} is not a save PKHeX reads");
        return new SaveWrapper(save!);
    }

    /// <summary>The file as written, loaded again the way the game or the next session would see it.</summary>
    private static SaveWrapper Reload(SaveWrapper wrapper, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pkconnect-{Guid.NewGuid():N}{extension}");
        try
        {
            File.WriteAllBytes(path, wrapper.GetSaveFileData());
            return Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ExpansionPK4 VictiniFromWhite()
    {
        var white = Load(Asset("white.sav"));
        var pk5 = (PK5)white.GetPartyData().First(pk => pk.Species == Victini).GetMutablePkm();
        return Gen4AddedSpecies.FromPK5(pk5);
    }

    [Theory]
    [InlineData("platinum.sav")]
    [InlineData("heartgold.dsv")]
    public void Gen4Save_TakesAGen5Pokemon_AndGivesItBack(string file)
    {
        var wrapper = Load(Asset(file));
        Assert.IsType<AddedSpeciesSaveExtension>(wrapper.FormatExtension);
        var victini = VictiniFromWhite();

        wrapper.SetBoxSlotAtIndex(new ImmutablePKM(victini), 0, 0);
        wrapper.SetPartySlotAtIndex(new ImmutablePKM(victini.Clone()), 0);

        var reloaded = Reload(wrapper, Path.GetExtension(file));
        Assert.True(reloaded.ChecksumsValid);

        var boxed = Assert.IsType<ExpansionPK4>(reloaded.GetBoxData(0)[0].GetMutablePkm());
        Assert.Equal(Victini, boxed.Species);
        Assert.Equal(victini.CurrentLevel, boxed.CurrentLevel);
        Assert.Equal(victini.Moves, boxed.Moves);

        // In the party it carries the stats the build's own table gives, not Gen 4's.
        var party = Assert.IsType<ExpansionPK4>(reloaded.GetPartyData()[0].GetMutablePkm());
        Assert.Equal(victini.Stat_HPMax, party.Stat_HPMax);
        Assert.Equal(victini.Stat_SPA, party.Stat_SPA);
        Assert.Contains(reloaded.GetAllPKM(), pk => pk.GetMutablePkm() is ExpansionPK4);
    }

    [Fact]
    public void Gen4Save_LeavesItsOwnPokemonAsTheyAre()
    {
        var wrapper = Load(Asset("platinum.sav"));
        var bulbasaur = new PK4 { Species = 1, CurrentLevel = 12, PID = 0x1234, Move1 = 33 };
        bulbasaur.RefreshChecksum();

        wrapper.SetBoxSlotAtIndex(new ImmutablePKM(bulbasaur), 0, 1);

        var stored = Reload(wrapper, ".sav").GetBoxData(0)[1];
        Assert.IsType<PK4>(stored.GetMutablePkm());
        Assert.Equal(1, stored.Species);
        Assert.Equal(12, stored.CurrentLevel);
    }

    private static ExpansionPK3 ExpansionVictini()
    {
        var white = Load(Asset("white.sav"));
        return ExpansionConverter.FromOfficial(white.GetPartyData().First(pk => pk.Species == Victini).GetMutablePkm());
    }

    [Fact]
    public void ExpansionSave_IsReadInItsOwnFormat()
    {
        var wrapper = Load(Asset("emeraldexpansion.sav"));

        var extension = Assert.IsType<ExpansionSaveExtension>(wrapper.FormatExtension);
        Assert.Equal(ExpansionSaveDetector.GameId, extension.GameId);
        Assert.IsType<ExpansionPK3>(wrapper.GetBlankPKM().GetMutablePkm());
        Assert.Equal(wrapper.PartyCount, wrapper.GetPartyData().Count);
        Assert.All(wrapper.GetBoxData(0), pk => Assert.IsType<ExpansionPK3>(pk.GetMutablePkm()));
    }

    [Fact]
    public void ExpansionSave_WritesSlots_AndReadsThemBack()
    {
        var wrapper = Load(Asset("emeraldexpansion.sav"));
        var victini = ExpansionVictini();
        var partySlot = wrapper.PartyCount;

        wrapper.SetBoxSlotAtIndex(new ImmutablePKM(victini), 2, 5);
        wrapper.SetPartySlotAtIndex(new ImmutablePKM(victini.Clone()), partySlot);

        var reloaded = Reload(wrapper, ".sav");
        Assert.True(reloaded.ChecksumsValid);
        Assert.IsType<ExpansionSaveExtension>(reloaded.FormatExtension);
        Assert.Equal(partySlot + 1, reloaded.PartyCount);

        foreach (var stored in new[] { reloaded.GetBoxData(2)[5], reloaded.GetPartyData()[partySlot] })
        {
            Assert.IsType<ExpansionPK3>(stored.GetMutablePkm());
            Assert.Equal(Victini, stored.Species);
            Assert.Equal(victini.CurrentLevel, stored.CurrentLevel);
            Assert.Equal(victini.PID, stored.PID);
            Assert.Equal(victini.Moves, stored.GetMutablePkm().Moves);
        }
        Assert.Equal(victini.Stat_HPMax, reloaded.GetPartyData()[partySlot].GetMutablePkm().Stat_HPMax);
        Assert.Equal(2, reloaded.GetAllPKM().Count(pk => pk.Species == Victini));
    }
    [Fact]
    public void AClone_KeepsItsExtension()
    {
        var wrapper = Load(Asset("emeraldexpansion.sav"));

        var clone = wrapper.Clone();

        Assert.IsType<ExpansionSaveExtension>(clone.FormatExtension);
        Assert.NotSame(wrapper.FormatExtension, clone.FormatExtension);
        // The clone writes to its own copy of the save.
        clone.SetBoxSlotAtIndex(new ImmutablePKM(ExpansionVictini()), 0, 0);
        Assert.Equal(Victini, clone.GetBoxData(0)[0].Species);
        Assert.Equal(0, wrapper.GetBoxData(0)[0].Species);
    }

    [Fact]
    public void AnOrdinarySave_HasNoExtension()
    {
        var wrapper = Load(Path.Combine(AppContext.BaseDirectory, "connect-data", "base-saves", "emerald.sav"));

        Assert.Null(wrapper.FormatExtension);
        Assert.All(wrapper.GetPartyData(), pk => Assert.IsType<PK3>(pk.GetMutablePkm()));
    }

    [Fact]
    public void ExpansionSave_CountsWhatItOwns_InItsPokedex()
    {
        var wrapper = Load(Asset("emeraldexpansion.sav"));
        var before = wrapper.CaughtCount;

        wrapper.SetBoxSlotAtIndex(new ImmutablePKM(ExpansionVictini()), 0, 0);

        Assert.Equal(before + 1, wrapper.CaughtCount);
        Assert.True(wrapper.SeenCount >= wrapper.CaughtCount);
    }

    [Fact]
    public async Task ExpansionSave_HasItsOwnPokedex_PastGen3()
    {
        var wrapper = Load(Asset("emeraldexpansion.sav"));
        wrapper.SetBoxSlotAtIndex(new ImmutablePKM(ExpansionVictini()), 0, 0);
        var service = Assert.IsType<ExpansionDexService>(wrapper.FormatExtension!.CreateDexService());

        Dictionary<ushort, Dictionary<uint, DexItemDTO>> dex = [];
        Assert.True(await service.UpdateDexWithSave(dex, null!, null));

        Assert.Equal(ExpansionDexFlags.MaxDexSpecies, dex.Count);
        var victini = Assert.Single(dex[Victini][wrapper.Id].Forms);
        Assert.True(victini.IsOwned);
        Assert.True(victini.IsCaught);
        Assert.Equal(EntityContext.Gen3, victini.Context);
        Assert.Equal(2, victini.Types.Distinct().Count());
        Assert.Equal(2, dex[25][wrapper.Id].Forms.Count); // Pikachu, male and female
        Assert.False(dex[25][wrapper.Id].Forms[0].IsOwned);
    }

    [Theory]
    [InlineData("emeraldexpansion.sav", "emeraldexpansion.sav", GameVersion.E)]
    [InlineData("emeraldexpansion.sav", "fireredexpansion.sav", GameVersion.FR)]
    [InlineData("emeraldexpansion.sav", "pokeleafgreen.sav", GameVersion.LG)]
    [InlineData("heartgold.dsv", "heartgold.dsv", GameVersion.HG)]
    [InlineData("heartgold.dsv", "soulsilver.dsv", GameVersion.SS)]
    [InlineData("platinum.sav", "platinum.sav", GameVersion.Pt)]
    public void ASave_PlaysAsTheGameItsFileIsNamedFor(string asset, string fileName, GameVersion expected)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"pkconnect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, fileName);
            File.Copy(Asset(asset), path);

            Assert.Equal(expected, Load(path).PlayedVersion);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void TheSameTrainersSaves_HaveDifferentIds()
    {
        // A profile keeps one identity across its saves, and several of these are the same save
        // type to PKHeX.
        var folder = Path.Combine(Path.GetTempPath(), $"pkconnect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            SaveWrapper Named(string asset, string name)
            {
                var path = Path.Combine(folder, name);
                File.Copy(asset, path);
                return Load(path);
            }

            var vanilla = Named(Path.Combine(AppContext.BaseDirectory, "connect-data", "base-saves", "emerald.sav"), "emerald.sav");
            var expansion = Named(Asset("emeraldexpansion.sav"), "emeraldexpansion.sav");
            var fireRed = Named(Asset("emeraldexpansion.sav"), "fireredexpansion.sav");
            var heartGold = Named(Asset("heartgold.dsv"), "heartgold.dsv");
            var soulSilver = Named(Asset("heartgold.dsv"), "soulsilver.dsv");
            foreach (var save in new[] { vanilla, expansion, fireRed })
            {
                var raw = save.GetSave();
                raw.TID16 = 12345; raw.SID16 = 54321; raw.OT = "RED"; raw.Gender = 0; raw.Language = 2;
            }

            Assert.Equal(3, new[] { vanilla.Id, expansion.Id, fireRed.Id }.Distinct().Count());
            Assert.NotEqual(heartGold.Id, soulSilver.Id);
            Assert.Equal(expansion.Id, expansion.Clone().Id);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}