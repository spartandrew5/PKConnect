using PKHeX.Core;
using PKConnect.Core.formats;
using PKVault.Core;

namespace PKConnect.Core.Tests.formats;

/// <summary>
/// The species the Platinum Connect build adds past #493: read from a Platinum save with the game's
/// own stats, and travel to and from the PK5 main storage keeps.
/// </summary>
public class Gen4AddedSpeciesTests
{
    private const ushort Victini = 494;
    private const ushort Snivy = 495;
    private const ushort Servine = 496;

    private static readonly string BaseSaves = Path.Combine(AppContext.BaseDirectory, "assets", "saves");

    // A save played on the decomp build (local-only fixture): the game itself computed these party stats.
    private static readonly string PlayedSave = Environment.GetEnvironmentVariable("PKCONNECT_TEST_PLATINUM_SAVE") ?? "";

    private static PK5 MakePk5(ushort species, Nature nature, bool shiny = false, byte level = 30)
    {
        var pk = new PK5
        {
            Species = species,
            TID16 = 12345,
            SID16 = 54321,
            Language = (int)LanguageID.English,
            Version = GameVersion.W,
            OriginalTrainerName = "Hilda",
            Ball = (byte)Ball.Poke,
            MetLevel = 5,
            MetLocation = 16,
        };
        pk.PID = 0x1234_5678;
        if (shiny)
            pk.SetShiny();
        pk.Gender = pk.PersonalInfo.IsDualGender ? EntityGender.GetFromPIDAndRatio(pk.PID, pk.PersonalInfo.Gender) : pk.PersonalInfo.FixedGender();
        pk.Nature = nature;
        pk.RefreshAbility(0);
        pk.IVs = [31, 20, 11, 7, 0, 25];
        pk.EV_ATK = 100;
        pk.CurrentLevel = level;
        pk.Move1 = (ushort)Move.Tackle;
        pk.Move2 = (ushort)Move.LeafBlade;
        pk.Move3 = 536; // Leaf Tornado: Gen 5 only
        pk.HealPP();
        pk.Nickname = SpeciesName.GetSpeciesNameGeneration(species, pk.Language, 5);
        pk.ResetPartyStats();
        pk.RefreshChecksum();
        return pk;
    }

    [Fact]
    public void Table_HoldsTheBuildsSpecies()
    {
        // Every Gen 5 species, Victini to Genesect.
        Assert.Equal(Enumerable.Range(494, 156).Select(n => (ushort)n), Gen4AddedSpecies.Species);
        Assert.False(Gen4AddedSpecies.Contains(493));
        Assert.False(Gen4AddedSpecies.Contains(650));

        // The game's stats are Gen 5's own, so a Pokémon has the same stats on both sides of a trip.
        foreach (var species in Gen4AddedSpecies.Species)
        {
            var theirs = PersonalTable.B2W2[species];
            var ours = Gen4AddedSpecies.GetPersonalInfo(species, 0);
            Assert.True(
                (theirs.HP, theirs.ATK, theirs.DEF, theirs.SPE, theirs.SPA, theirs.SPD, theirs.Type1, theirs.Type2, theirs.EXPGrowth, theirs.Gender)
                    == (ours.HP, ours.ATK, ours.DEF, ours.SPE, ours.SPA, ours.SPD, ours.Type1, ours.Type2, ours.EXPGrowth, ours.Gender),
                $"#{species} differs from Gen 5");
            // Its real abilities too: the build has Gen 5's abilities, so nothing is a stand-in.
            var second = theirs.Ability2 == theirs.Ability1 ? 0 : theirs.Ability2;
            Assert.True((theirs.Ability1, second) == (ours.Ability1, ours.Ability2),
                $"#{species} abilities {ours.Ability1}/{ours.Ability2}, Gen 5 has {theirs.Ability1}/{theirs.Ability2}");
        }

        var victini = Gen4AddedSpecies.GetPersonalInfo(Victini, 0);
        Assert.Equal((100, 100, 100, 100, 100, 100), (victini.HP, victini.ATK, victini.DEF, victini.SPE, victini.SPA, victini.SPD));
        var snivy = Gen4AddedSpecies.GetPersonalInfo(Snivy, 0);
        Assert.Equal((45, 45, 55, 63, 45, 55), (snivy.HP, snivy.ATK, snivy.DEF, snivy.SPE, snivy.SPA, snivy.SPD));
        Assert.Equal((int)Ability.Overgrow, snivy.Ability1);
        Assert.Equal((int)Ability.VictoryStar, victini.Ability1);
        // Types use PKHeX's numbering, as its own Gen 4 entries do.
        Assert.Equal(((byte)MoveType.Psychic, (byte)MoveType.Fire), (victini.Type1, victini.Type2));
        Assert.Equal(((byte)MoveType.Grass, (byte)MoveType.Grass), (snivy.Type1, snivy.Type2));
        var dragonite = Gen4AddedSpecies.GetPersonalInfo(149, 0);
        Assert.Equal(((byte)MoveType.Dragon, (byte)MoveType.Flying), (dragonite.Type1, dragonite.Type2));

        // Everything else still comes from PKHeX.
        Assert.Equal(PersonalTable.HGSS[25].HP, Gen4AddedSpecies.GetPersonalInfo(25, 0).HP);
    }

    [Theory]
    [InlineData(Victini, Nature.Timid, false)]
    [InlineData(Snivy, Nature.Adamant, false)]
    [InlineData(Servine, Nature.Hardy, true)]
    public void Pk5_RoundTripsThroughPlatinum(ushort species, Nature nature, bool shiny)
    {
        var source = MakePk5(species, nature, shiny);

        var inSave = Gen4AddedSpecies.FromPK5(source);
        // Stored the way a save slot is, through PKHeX's own type.
        var slot = Gen4AddedSpecies.Downgrade(inSave, new SAV4Pt());
        var stored = new byte[slot.SIZE_STORED];
        slot.WriteEncryptedDataStored(stored);
        inSave = Assert.IsType<ExpansionPK4>(Gen4AddedSpecies.Upgrade(new PK4(stored)));

        Assert.Equal(species, inSave.Species);
        Assert.True(inSave.ChecksumValid);
        // Gen 4 reads the nature and shininess off the PID.
        Assert.Equal(nature, inSave.Nature);
        Assert.Equal(shiny, inSave.IsShiny);
        Assert.Equal(source.Gender, inSave.Gender);
        Assert.Equal(source.CurrentLevel, inSave.CurrentLevel);
        Assert.Equal(source.EXP, inSave.EXP);
        Assert.Equal(source.IVs, inSave.IVs);
        // The build has Gen 5's moves, so all three stay, with Gen 5's PP for the Gen 5 one.
        Assert.Equal([(ushort)Move.Tackle, (ushort)Move.LeafBlade, 536, 0], inSave.Moves);
        Assert.Equal(source.Move3_PP, inSave.Move3_PP);
        Assert.True(inSave.Move3_PP > 0);
        Assert.Equal(SpeciesName.GetSpeciesNameGeneration(species, source.Language, 4), inSave.Nickname);
        Assert.False(inSave.IsNicknamed);
        Assert.Equal(inSave.PersonalInfo.Ability1, inSave.Ability);

        var back = Gen4AddedSpecies.ToPK5(inSave);
        Assert.Equal(species, back.Species);
        Assert.Equal(nature, back.Nature);
        Assert.Equal(shiny, back.IsShiny);
        Assert.Equal(source.Gender, back.Gender);
        Assert.Equal(source.CurrentLevel, back.CurrentLevel);
        Assert.Equal(source.IVs, back.IVs);
        Assert.Equal(source.ID32, back.ID32);
        Assert.Equal(source.OriginalTrainerName, back.OriginalTrainerName);
        Assert.Equal(source.Nickname, back.Nickname);
        // Gen 5's own ability, not the Gen 4 stand-in.
        Assert.Equal(source.Ability, back.Ability);
        Assert.True(back.ChecksumValid);
    }

    [Fact]
    public void MatchingPid_IsKept()
    {
        var source = MakePk5(Snivy, Nature.Hardy);
        source.Nature = (Nature)(source.PID % 25);

        Assert.Equal(source.PID, Gen4AddedSpecies.FromPK5(source).PID);
    }

    [Fact]
    public void ReplacedPid_IsTheSameOnEveryVisit()
    {
        var source = MakePk5(Victini, Nature.Timid);
        Assert.NotEqual(Nature.Timid, (Nature)(source.PID % 25));

        var first = Gen4AddedSpecies.FromPK5(source).PID;
        Assert.NotEqual(source.PID, first);
        Assert.Equal(first, Gen4AddedSpecies.FromPK5(source).PID);
    }

    [Fact]
    public void Gen4Species_KeepsItsGen5Move()
    {
        // A Dragonite that learned Scald in Gen 5.
        var source = MakePk5(149, Nature.Adamant);
        source.Move3 = 503;
        source.HealPP();
        source.RefreshChecksum();

        var inSave = Gen4AddedSpecies.FromPK5(source);
        Assert.Equal(149, inSave.Species);
        // Gen 4's own data for a Gen 4 species.
        Assert.Equal(PersonalTable.HGSS[149].HP, inSave.PersonalInfo.HP);

        // The save slot is plain PK4 bytes; the Gen 5 move is what marks it as needing our reader.
        var slot = Assert.IsType<PK4>(Gen4AddedSpecies.Downgrade(inSave, new SAV4Pt()));
        Assert.True(Gen4AddedSpecies.IsAddedContent(slot));
        var stored = new byte[slot.SIZE_STORED];
        slot.WriteEncryptedDataStored(stored);
        var reread = Assert.IsType<ExpansionPK4>(Gen4AddedSpecies.Upgrade(new PK4(stored)));
        Assert.Equal(503, reread.Move3);
        Assert.Equal(15, reread.Move3_PP);

        var back = Gen4AddedSpecies.ToPK5(reread);
        Assert.Equal(503, back.Move3);
        Assert.Equal(source.Nature, back.Nature);
    }

    [Fact]
    public void VanillaSpecies_StayPk4()
    {
        var pikachu = new PK4 { Species = 25 };
        Assert.Same(pikachu, Gen4AddedSpecies.Upgrade(pikachu));
        Assert.Same(pikachu, Gen4AddedSpecies.Downgrade(pikachu, new SAV4Pt()));
    }

    [Fact]
    public void PlayedSave_StatsMatchTheGames()
    {
        if (!File.Exists(PlayedSave) || !SaveFiles.TryLoad(File.ReadAllBytes(PlayedSave), out var save, PlayedSave) || save is not SAV4Pt)
        {
            return; // local-only fixture
        }

        var added = save.PartyData
            .Select(Gen4AddedSpecies.Upgrade)
            .OfType<ExpansionPK4>()
            .ToList();
        Assert.NotEmpty(added);

        foreach (var pk in added)
        {
            var label = $"#{pk.Species} lv{pk.Stat_Level}";
            // The level and stats the game stored are what this table gives.
            Assert.True(pk.Stat_Level == pk.CurrentLevel, $"{label}: level {pk.CurrentLevel}");
            var ours = pk.Clone();
            ours.ResetPartyStats();
            Assert.True(
                (pk.Stat_HPMax, pk.Stat_ATK, pk.Stat_DEF, pk.Stat_SPE, pk.Stat_SPA, pk.Stat_SPD)
                    == (ours.Stat_HPMax, ours.Stat_ATK, ours.Stat_DEF, ours.Stat_SPE, ours.Stat_SPA, ours.Stat_SPD),
                $"{label}: game {pk.Stat_HPMax}/{pk.Stat_ATK}/{pk.Stat_DEF}/{pk.Stat_SPE}/{pk.Stat_SPA}/{pk.Stat_SPD}"
                    + $" vs {ours.Stat_HPMax}/{ours.Stat_ATK}/{ours.Stat_DEF}/{ours.Stat_SPE}/{ours.Stat_SPA}/{ours.Stat_SPD}");
        }
    }
}
