using PKConnect.Core.formats;
using PKHeX.Core;
using PKVault.Core;

namespace PKConnect.Core.Tests.formats;

/// <summary>PKVault's own conversion and save loading, taking Connect's formats through its extension points.</summary>
public class StorageHooksTests : IAsyncLifetime
{
    private const int Victini = 494;

    private readonly PkmConvertService convert = new(new FixedSettings(), new PKMConverterUtils());
    private Dictionary<ushort, StaticEvolve> evolves = [];

    public StorageHooksTests() => ConnectModule.RegisterFormats();

    public async ValueTask InitializeAsync() => evolves = await StaticEvolvesLoader.LoadData();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class FixedSettings : ISettingsService
    {
        public SettingsDTO GetSettings() => new(
            BuildID: default, RuntimeSystem: RuntimeSystem.LINUX, SourceProvider: SourceProvider.GithubRelease, FlatpakMigrated: false,
            Version: "", PkhexVersion: "", AppDirectory: "", SettingsPath: "", UserId: "",
            SettingsMutable: new(
                DB_PATH: "", SAVE_GLOBS: [], PKM_EXTERNAL_GLOBS: [], STORAGE_PATH: "", BACKUP_PATH: "",
                LANGUAGE: "en", HIDE_CHEATS: false, SKIP_LEGALITY_CHECKS: false
            )
        );

        public Task UpdateSettings(SettingsMutableDTO settingsMutable, bool restartSession, bool scanSaves, DataUpdateFlags flags) => throw new NotSupportedException();
        public Task UpdateSettingsSimple(SettingsMutableDTO settingsMutable, string userId) => throw new NotSupportedException();
        public Task<SettingsDTO> GetSettingsWithUserId() => throw new NotSupportedException();
        public SettingsDTO RefreshSettings(DataUpdateFlags flags) => throw new NotSupportedException();
        public (bool RestartSession, bool ScanSaves) GetUpdateDiff(SettingsMutableDTO updatedSettingsMutable, DataUpdateFlags flags) => throw new NotSupportedException();
    }

    private static string Asset(string file) => Path.Combine(AppContext.BaseDirectory, "assets", "saves", file);

    private static SaveWrapper Load(string path)
    {
        Assert.True(SaveFiles.TryLoad(File.ReadAllBytes(path), out var save, path), $"{path} is not a save PKHeX reads");
        return new SaveWrapper(save!);
    }

    private static ImmutablePKM WhiteParty(Func<ImmutablePKM, bool> which) => Load(Asset("white.sav")).GetPartyData().First(which);

    private static PkmVariantEntity Variant(EntityContext context, ushort species, bool isMain = false) => new()
    {
        Id = $"{context}", Hash = "", BoxId = "0", BoxSlot = 0, IsMain = isMain, IsExternal = false,
        AttachedSaveId = null, AttachedSavePkmIdBase = null, Context = context, Generation = context.Generation,
        Filepath = "", Species = species, Form = 0, Gender = Gender.Genderless, IsShiny = false, IsAlpha = false, PkmFile = null!,
    };

    [Fact]
    public void AGen5Pokemon_ConvertsToTheExpansionFormat_AndBack()
    {
        var victini = WhiteParty(pk => pk.Species == Victini);

        var expansion = convert.ConvertTo(victini, typeof(ExpansionPK3), null);
        var back = convert.ConvertTo(expansion, typeof(PK5), null);

        Assert.IsType<ExpansionPK3>(expansion.GetMutablePkm());
        Assert.Equal(Victini, expansion.Species);
        Assert.Equal(victini.Moves, expansion.Moves);
        Assert.IsType<PK5>(back.GetMutablePkm());
        Assert.Equal(victini.PID, back.PID);
        Assert.Equal(victini.Moves, back.Moves);
        Assert.Equal(victini.Nature, back.Nature);
        Assert.Equal(victini.CurrentLevel, back.CurrentLevel);
    }

    [Fact]
    public void AnExpansionPokemon_ReachesGenerationsPastItsBridges()
    {
        var expansion = convert.ConvertTo(WhiteParty(pk => pk.Species == Victini), typeof(ExpansionPK3), null);

        var pk7 = convert.ConvertTo(expansion, typeof(PK7), null);

        Assert.IsType<PK7>(pk7.GetMutablePkm());
        Assert.Equal(Victini, pk7.Species);
    }

    [Fact]
    public void MainStorage_KeepsAnOfficialFormat()
    {
        IPkmConvertService service = convert;
        var victini = WhiteParty(pk => pk.Species == Victini);
        var expansion = convert.ConvertTo(victini, typeof(ExpansionPK3), null);
        var added = convert.ConvertTo(victini, typeof(ExpansionPK4), null);

        Assert.IsType<ExpansionPK4>(added.GetMutablePkm());
        Assert.IsType<PK5>(service.ToStorageFormat(expansion).GetMutablePkm());
        Assert.IsType<PK5>(service.ToStorageFormat(added).GetMutablePkm());
        Assert.Same(victini, service.ToStorageFormat(victini));
    }

    [Fact]
    public void ASavesPokemon_AndItsStoredVariant_CompareEqual()
    {
        IPkmConvertService service = convert;
        // What an attached Pokémon's synchronization looks at to tell whether either side changed.
        var expansion = convert.ConvertTo(WhiteParty(pk => pk.Species == Victini), typeof(ExpansionPK3), null);

        var stored = service.ToStorageFormat(expansion);

        Assert.Equal(stored.DynamicChecksum, expansion.DynamicChecksum);
        Assert.Equal(ExpansionPK3.NicknameMaxLength, expansion.MaxStringLengthNickname);
        // Its own learnsets, where PKHeX's Gen 3 ones have nothing for the species.
        Assert.NotEmpty(PkmFormatExtension.For(expansion.GetMutablePkm())!.LearnableMoves(expansion.GetMutablePkm())!);
    }

    [Fact]
    public void ExpansionSave_TakesAStoredPokemon_InItsOwnFormat()
    {
        var save = Load(Asset("emeraldexpansion.sav"));
        var loader = new SavePkmLoader(convert, "en", evolves, save);
        var victini = WhiteParty(pk => pk.Species == Victini);

        var dto = loader.CreateDTO(save, victini, 0, 0);
        loader.WriteDto(dto);

        Assert.IsType<ExpansionPK3>(dto.Pkm.GetMutablePkm());
        Assert.False(dto.CanEvolve);
        var stored = save.GetBoxData(0)[0];
        Assert.IsType<ExpansionPK3>(stored.GetMutablePkm());
        Assert.Equal(Victini, stored.Species);
        Assert.Equal(victini.Moves, stored.Moves);
        // Read again from the save, it is the Pokémon the DTO was made for.
        Assert.Equal(dto.IdBase, new SavePkmLoader(convert, "en", evolves, save).GetDto("0", 0)?.IdBase);
    }

    [Theory]
    [InlineData("platinum.sav")]
    [InlineData("heartgold.dsv")]
    public void Gen4Save_TakesAStoredGen5Pokemon(string file)
    {
        var save = Load(Asset(file));
        var loader = new SavePkmLoader(convert, "en", evolves, save);
        var victini = WhiteParty(pk => pk.Species == Victini);

        var dto = loader.CreateDTO(save, victini, 0, 0);
        loader.WriteDto(dto);

        Assert.IsType<ExpansionPK4>(dto.Pkm.GetMutablePkm());
        var stored = save.GetBoxData(0)[0];
        Assert.IsType<ExpansionPK4>(stored.GetMutablePkm());
        Assert.Equal(Victini, stored.Species);
        Assert.Equal(victini.Moves, stored.Moves);
    }

    [Fact]
    public void ExpansionSave_PicksABridgeVariant()
    {
        var save = Load(Asset("emeraldexpansion.sav"));
        var gen4 = Variant(EntityContext.Gen4, 25);
        var gen5 = Variant(EntityContext.Gen5, 25);
        var gen7Main = Variant(EntityContext.Gen7, 25, isMain: true);

        Assert.Same(gen5, save.FindVariant([gen7Main, gen4, gen5]));
        Assert.Same(gen4, save.FindVariant([gen7Main, gen4]));
        Assert.Null(save.FindVariant([gen7Main]));
        // The main variant keeps the Pokémon's original data, so it wins when it is a bridge itself.
        var gen4Main = Variant(EntityContext.Gen4, 25, isMain: true);
        Assert.Same(gen4Main, save.FindVariant([gen5, gen4Main]));

        Assert.Equal(EntityContext.Gen4, save.ContextToCreate(25));
        Assert.Equal(EntityContext.Gen5, save.ContextToCreate(Victini));
        Assert.True(save.CanEnter(EntityContext.Gen5, Victini));
        Assert.True(save.IsPresentInGame(Victini, 0));
        Assert.True(save.IsPresentInGame(25, 0));
    }

    [Fact]
    public void Gen4Save_TakesAnAddedSpeciesFromItsGen5Variant()
    {
        var save = Load(Asset("platinum.sav"));
        var gen4 = Variant(EntityContext.Gen4, 25);
        var gen5 = Variant(EntityContext.Gen5, Victini, isMain: true);

        Assert.Same(gen5, save.FindVariant([gen5]));
        Assert.Null(save.FindVariant([Variant(EntityContext.Gen6, Victini, isMain: true)]));
        Assert.Same(gen4, save.FindVariant([Variant(EntityContext.Gen6, 25, isMain: true), gen4]));

        Assert.Equal(EntityContext.Gen5, save.ContextToCreate(Victini));
        Assert.Equal(EntityContext.Gen4, save.ContextToCreate(25));
        Assert.True(save.CanEnter(EntityContext.Gen5, Victini));
        Assert.True(save.CanEnter(EntityContext.Gen4, 25));
        Assert.False(save.CanEnter(EntityContext.Gen6, 25));
        Assert.True(save.IsPresentInGame(Victini, 0));
        Assert.True(save.IsPresentInGame(25, 0));
    }

    [Fact]
    public void AnOrdinarySave_KeepsTheRegularRules()
    {
        var save = Load(Path.Combine(AppContext.BaseDirectory, "connect-data", "base-saves", "emerald.sav"));
        var gen3 = Variant(EntityContext.Gen3, 25);

        Assert.Same(gen3, save.FindVariant([Variant(EntityContext.Gen4, 25, isMain: true), gen3]));
        Assert.Null(save.FindVariant([Variant(EntityContext.Gen4, 25, isMain: true)]));
        Assert.Equal(EntityContext.Gen3, save.ContextToCreate(25));
        Assert.False(save.CanEnter(EntityContext.Gen4, 25));
        Assert.False(save.IsPresentInGame(Victini, 0));
    }
}