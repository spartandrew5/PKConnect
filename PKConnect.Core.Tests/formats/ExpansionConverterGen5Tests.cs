using PKHeX.Core;
using PKConnect.Core.formats;
using PKVault.Core;

namespace PKConnect.Core.Tests.formats;

/// <summary>
/// Gen 5 <-> pokeemerald-expansion travel: a PK5 goes into the expansion format and comes back as
/// the same Pokémon. Runs over every Pokémon in the Gen 5 base saves, skipping when they aren't there.
/// </summary>
public class ExpansionConverterGen5Tests
{
    private static readonly string BaseSaves = Path.Combine(AppContext.BaseDirectory, "assets", "saves");

    private static List<PK5> LoadGen5(string file)
    {
        var path = Path.Combine(BaseSaves, file);
        if (!File.Exists(path) || !SaveFiles.TryLoad(File.ReadAllBytes(path), out var save, path))
        {
            return [];
        }
        return [.. save.GetAllPKM().OfType<PK5>().Where(pk => pk.Species != 0 && !pk.IsEgg)];
    }

    [Theory]
    [InlineData("white.sav")]
    [InlineData("white2.sav")]
    public void Pk5_RoundTripsThroughExpansion(string file)
    {
        var mons = LoadGen5(file);
        if (mons.Count == 0)
        {
            return; // local-only fixture
        }

        foreach (var source in mons)
        {
            var label = $"{file} #{source.Species} '{source.Nickname}'";
            Assert.True(ExpansionConverter.CanConvertFromOfficial(source), label);

            var expansion = ExpansionConverter.FromOfficial(source);
            Assert.True(ExpansionConverter.CanConvertToOfficial(expansion), label);
            // Stats are computed with the Pokémon's real nature, not the one its PID happens to give.
            Assert.True(source.Nature == expansion.StatAlignment, $"{label}: stat nature {source.Nature} -> {expansion.StatAlignment}");
            // Stored to disk and read back, like a save slot.
            var stored = new byte[expansion.SIZE_STORED];
            expansion.WriteEncryptedDataStored(stored);
            expansion = new ExpansionPK3(stored);
            var back = Assert.IsType<PK5>(ExpansionConverter.ToOfficial(expansion, preferGen3: false, preferGen5: true));

            Assert.True(source.Species == back.Species, $"{label}: species {source.Species} -> {back.Species}");
            Assert.True(source.PID == back.PID, $"{label}: PID");
            Assert.True(source.ID32 == back.ID32, $"{label}: trainer id");
            Assert.True(source.Nature == back.Nature, $"{label}: nature {source.Nature} -> {back.Nature}");
            Assert.True(source.Gender == back.Gender, $"{label}: gender {source.Gender} -> {back.Gender}");
            Assert.True(source.IsShiny == back.IsShiny, $"{label}: shiny");
            Assert.True(source.EXP == back.EXP, $"{label}: exp");
            Assert.True(source.CurrentLevel == back.CurrentLevel, $"{label}: level");
            Assert.True(source.Moves.SequenceEqual(back.Moves),
                $"{label}: moves {string.Join(",", source.Moves)} -> {string.Join(",", back.Moves)}");
            int[] ivs = [source.IV_HP, source.IV_ATK, source.IV_DEF, source.IV_SPA, source.IV_SPD, source.IV_SPE];
            int[] ivsBack = [back.IV_HP, back.IV_ATK, back.IV_DEF, back.IV_SPA, back.IV_SPD, back.IV_SPE];
            Assert.True(ivs.SequenceEqual(ivsBack), $"{label}: IVs");
            int[] evs = [source.EV_HP, source.EV_ATK, source.EV_DEF, source.EV_SPA, source.EV_SPD, source.EV_SPE];
            int[] evsBack = [back.EV_HP, back.EV_ATK, back.EV_DEF, back.EV_SPA, back.EV_SPD, back.EV_SPE];
            Assert.True(evs.SequenceEqual(evsBack), $"{label}: EVs");
            Assert.True(source.OriginalTrainerName == back.OriginalTrainerName, $"{label}: OT");
            if (source.IsNicknamed)
            {
                Assert.True(source.Nickname == back.Nickname, $"{label}: nickname -> '{back.Nickname}'");
            }
            // Forms collapse to the base form for now, and a form's ability can differ from the base's.
            if (source.Form == 0)
            {
                Assert.True(source.Ability == back.Ability, $"{label}: ability {source.Ability} -> {back.Ability}");
            }
            // An item the expansion doesn't have is dropped; one it has must come back unchanged.
            Assert.True(back.HeldItem == source.HeldItem || back.HeldItem == 0, $"{label}: item {source.HeldItem} -> {back.HeldItem}");
        }
    }

    /// <summary>The saves actually exercise what Gen 4's format can't hold, so the test above means something.</summary>
    [Fact]
    public void BaseSaves_CoverGen5OnlyData()
    {
        var mons = LoadGen5("white.sav").Concat(LoadGen5("white2.sav")).ToList();
        if (mons.Count == 0)
        {
            return;
        }

        Assert.Contains(mons, pk => pk.Species > 493);
        Assert.Contains(mons, pk => pk.Moves.Any(move => move > 467));
        Assert.Contains(mons, pk => (int)pk.Nature != (int)(pk.PID % 25));
    }
}
