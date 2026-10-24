using PKConnect.Core.formats;
using PKHeX.Core;
using PKVault.Core;

namespace PKConnect.Core.profiles;

/// <summary>
/// A trainer's Pokédex is one, whichever game they are in: what they saw and caught in FireRed is
/// seen and caught when they arrive in Yellow. This reads a save's Pokédex and writes into a save
/// what the trainer's other saves know.
///
/// It only ever adds. A game is told about the species it has a Pokédex entry for and no others:
/// Yellow hears nothing of Johto, and the Generation 4 Connect builds, whose Pokédex wasn't
/// widened for the species they gained, hear nothing past #493.
/// </summary>
public static class PokedexSync
{
    /// <summary>What a save's Pokédex has, by National Dex number.</summary>
    public sealed record Known(HashSet<ushort> Seen, HashSet<ushort> Caught)
    {
        public static Known Nothing() => new([], []);

        public void Add(Known other)
        {
            Seen.UnionWith(other.Seen);
            Caught.UnionWith(other.Caught);
        }
    }

    public static Known Read(SaveFile save)
    {
        var known = Known.Nothing();

        if (SaveExtension.For(save) is ExpansionSaveExtension expansion)
        {
            if (expansion.DexFlags is not { } flags)
                return known;
            for (ushort species = 1; species <= ExpansionDexFlags.MaxDexSpecies; species++)
            {
                if (flags.GetCaught(species)) known.Caught.Add(species);
                if (flags.GetSeen(species)) known.Seen.Add(species);
            }
        }
        else
        {
            for (ushort species = 1; species <= save.MaxSpeciesID; species++)
            {
                if (save.GetCaught(species)) known.Caught.Add(species);
                if (save.GetSeen(species)) known.Seen.Add(species);
            }
        }

        // Caught is seen, whatever the flags say.
        known.Seen.UnionWith(known.Caught);
        return known;
    }

    /// <summary>
    /// Adds what is <paramref name="known"/> to the save's Pokédex, for the species it has entries
    /// for. Returns how many entries gained something.
    /// </summary>
    public static int Apply(SaveFile save, Known known)
    {
        var changed = 0;

        if (SaveExtension.For(save) is ExpansionSaveExtension expansion)
        {
            // Only where the flags were found for certain: a guess would write over something else.
            if (expansion.DexFlags is not { IsExact: true } flags)
                return 0;
            foreach (var species in known.Seen.Where(s => s is > 0 and <= ExpansionDexFlags.MaxDexSpecies))
            {
                var caught = known.Caught.Contains(species);
                if (flags.GetSeen(species) && (!caught || flags.GetCaught(species)))
                    continue;
                flags.SetSeen(species);
                if (caught) flags.SetCaught(species);
                changed++;
            }
            return changed;
        }

        foreach (var species in known.Seen.Where(s => s > 0 && s <= save.MaxSpeciesID && save.Personal.IsSpeciesInGame(s)))
        {
            var caught = known.Caught.Contains(species);
            var newlySeen = !save.GetSeen(species);
            var newlyCaught = caught && !save.GetCaught(species);
            if (!newlySeen && !newlyCaught)
                continue;

            if (newlySeen) MarkSeen(save, species);
            if (newlyCaught) MarkCaught(save, species);
            changed++;
        }
        return changed;
    }

    /// <summary>The gender a species is shown as when nothing more is known: male unless there are none.</summary>
    private static byte ShownGender(SaveFile save, ushort species)
    {
        var info = save.Personal[species];
        return info.Genderless ? (byte)2 : info.OnlyFemale ? (byte)1 : (byte)0;
    }

    // The DS games don't show an entry on the seen flag alone: they also want to know which
    // gender was seen, and Generation 5 which picture to show.
    private static void MarkSeen(SaveFile save, ushort species)
    {
        switch (save)
        {
            case SAV4 sav4:
                sav4.Dex.SetSeen(species, true);
                var gender = ShownGender(save, species);
                if (gender == 2) sav4.Dex.SetSeenGenderNeither(species);
                else sav4.Dex.SetSeenGender(species, gender);
                break;
            case SAV5 sav5:
                var shown = ShownGender(save, species);
                sav5.Zukan.SetSeen(species, shown, false, true);
                if (!sav5.Zukan.GetDisplayedAny(species))
                    sav5.Zukan.SetDisplayed(species, shown, false, true);
                break;
            default:
                save.SetSeen(species, true);
                break;
        }
    }

    // A caught entry in the DS games has its text in the language(s) it was caught in.
    private static void MarkCaught(SaveFile save, ushort species)
    {
        switch (save)
        {
            case SAV4 sav4:
                sav4.Dex.SetCaught(species, true);
                sav4.Dex.SetLanguage(species, save.Language, true);
                break;
            case SAV5 sav5:
                sav5.Zukan.SetCaught(species, true);
                sav5.Zukan.SetLanguageFlag(species, (LanguageID)save.Language, true);
                break;
            default:
                save.SetCaught(species, true);
                break;
        }
    }
}
