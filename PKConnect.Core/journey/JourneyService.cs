using PKConnect.Core.formats;
using PKConnect.Core.champions;
using PKConnect.Core.profiles;
using PKConnect.Core.regions;
using PKHeX.Core;
using PKVault.Core;

namespace PKConnect.Core.journey;

public interface IJourneyService
{
    Task<JourneyDTO> GetTrainers();
    Task<LeagueDTO> GetLeague();
    Task<TravelPreviewDTO> GetTravelPreview();
    Task<TrainerDexDTO> GetDex(string scope);
}

/// <summary>
/// What the app's screens show, put together from what the other services keep: each trainer
/// with where they are and what they have earned, the Leagues, and what a trip would mean for
/// the party. It only reads.
/// </summary>
public class JourneyService(
    ITrainerProfilesService profilesService,
    IChampionsService championsService,
    ISavesLoadersService savesLoadersService,
    DexService dexService,
    StaticDataService staticDataService
) : IJourneyService
{
    // The regional Pokédex of each game, by the name PKVault's species data lists it under.
    private static readonly Dictionary<string, string> RegionalDex = new()
    {
        ["yellow"] = "kanto", ["firered"] = "kanto", ["leafgreen"] = "kanto",
        ["fireredexpansion"] = "kanto", ["leafgreenexpansion"] = "kanto",
        ["crystal"] = "original-johto", ["heartgold"] = "updated-johto", ["soulsilver"] = "updated-johto",
        ["emerald"] = "hoenn", ["emeraldexpansion"] = "hoenn",
        ["platinum"] = "extended-sinnoh",
        ["black"] = "original-unova", ["white"] = "original-unova",
        ["black2"] = "updated-unova", ["white2"] = "updated-unova",
    };

    /// <summary>The newest species a trainer can meet in any game there is to travel to.</summary>
    private const int NationalCount = 649;

    public async Task<TrainerDexDTO> GetDex(string scope)
    {
        var national = string.Equals(scope, "national", StringComparison.OrdinalIgnoreCase);
        var store = await profilesService.GetProfiles();
        var profile = store.Profiles.Find(p => p.Id == store.ActiveProfileId)
            ?? throw new InvalidOperationException("No active trainer");

        var saves = SavesOf(profile, isActive: true);
        var current = CurrentSave(profile, saves);
        var gameId = current is null ? profile.PendingGameVersion : ConnectCatalog.GameIdFor(current.Save.GetSave());
        var region = ConnectRegions.ForGameId(gameId);

        // Which species, under which number.
        List<(int Number, ushort Species)> wanted;
        if (national || gameId is null || !RegionalDex.TryGetValue(gameId, out var dexName))
        {
            national = true;
            wanted = [.. Enumerable.Range(1, NationalCount).Select(n => (n, (ushort)n))];
        }
        else
        {
            var species = await staticDataService.GetStaticSpecies();
            wanted = [.. species
                .Where(entry => entry.Value.PokedexIndexes.ContainsKey(dexName))
                .Select(entry => (entry.Value.PokedexIndexes[dexName], entry.Key))
                .OrderBy(entry => entry.Item1)];
        }

        // What the trainer knows of them, in any of their saves and in storage: the regional
        // Pokédex is the region's list, not one save's memory.
        var known = await dexService.GetDex([.. wanted.Select(w => w.Species)]);

        var entries = wanted.Select(w =>
        {
            var forms = known.TryGetValue(w.Species, out var bySave) ? bySave.Values.SelectMany(item => item.Forms).ToList() : [];
            var caught = forms.Any(form => form.IsCaught || form.IsOwned);
            return new DexEntryDTO(w.Number, w.Species, caught || forms.Any(form => form.IsSeen), caught);
        }).ToList();

        return new(national ? "national" : "regional", region, gameId, entries.Count(e => e.Seen), entries.Count(e => e.Caught), entries);
    }

    private sealed record TrainerSave(uint Id, SaveWrapper Save, string Path, DateTime WrittenAt);

    public async Task<JourneyDTO> GetTrainers()
    {
        var store = await profilesService.GetProfiles();
        var champions = await championsService.GetChampions();
        return new(store.ActiveProfileId, [.. store.Profiles.Select(profile => Summarize(profile, profile.Id == store.ActiveProfileId, champions))]);
    }

    private TrainerSummaryDTO Summarize(TrainerProfileDTO profile, bool isActive, ChampionsDTO champions)
    {
        var saves = SavesOf(profile, isActive);
        var current = CurrentSave(profile, saves);
        var currentGame = current is null ? profile.PendingGameVersion : ConnectCatalog.GameIdFor(current.Save.GetSave());

        var conquered = (profile.ConqueredRegions ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visited = saves
            .Select(save => ConnectRegions.ForSave(save.Save.GetSave()))
            .Append(ConnectRegions.ForGameId(profile.PendingGameVersion))
            .OfType<string>()
            .ToHashSet();

        var passport = ConnectCatalog.Regions.Select(region =>
        {
            // The ledger is what the trainer has; a save not reconciled yet may know of more.
            var badges = new bool[RegionBadges.Count];
            if (profile.RegionBadges is { } ledger && ledger.TryGetValue(region.Id, out var recorded))
                for (var i = 0; i < badges.Length && i < recorded.Length; i++)
                    badges[i] = recorded[i];
            foreach (var save in saves)
                if (RegionBadges.Read(save.Save.GetSave(), region.Id) is { } own)
                    for (var i = 0; i < badges.Length; i++)
                        badges[i] |= own[i];

            var holdsTitle = champions.Regions.TryGetValue(region.Id, out var champion) && IsHeldBy(champion, profile.Id);
            return new PassportRegionDTO(
                region.Id, visited.Contains(region.Id) || badges.Any(b => b), badges, badges.Count(b => b),
                holdsTitle, conquered.Contains(region.Id) || holdsTitle);
        }).ToList();

        return new(
            profile.Id,
            profile.Name,
            isActive,
            current is null || string.IsNullOrWhiteSpace(current.Save.OT) ? profile.Name : current.Save.OT,
            current?.Save.TID,
            profile.HomeRegion,
            currentGame,
            ConnectRegions.ForGameId(currentGame),
            current?.Id,
            saves.Sum(save => save.Save.PlayTimeInSeconds) / 3600,
            current is null ? [] : [.. current.Save.GetPartyData()
                .Where(pk => pk.Species > 0)
                .Select(pk => new TrainerPartyMonDTO(pk.Species, pk.Nickname, pk.CurrentLevel, pk.IsShiny, pk.IsEgg))],
            passport,
            [.. saves.Select(save => new TrainerSaveDTO(
                save.Id,
                ConnectCatalog.GameIdFor(save.Save.GetSave()),
                ConnectRegions.ForSave(save.Save.GetSave()),
                save.Save.PlayTimeInSeconds,
                Path.GetFileName(save.Path)))],
            profile.Appearance);
    }

    // The active trainer's saves are the ones the session holds (with whatever is waiting to be
    // saved); another trainer's are read from their folder.
    private List<TrainerSave> SavesOf(TrainerProfileDTO profile, bool isActive)
    {
        if (isActive)
        {
            try
            {
                return [.. savesLoadersService.GetSaveById()
                    .Where(entry => entry.Value.Metadata.FilePath is not null)
                    .Select(entry => new TrainerSave(entry.Key, entry.Value, entry.Value.Metadata.FilePath!, WrittenAt(entry.Value.Metadata.FilePath!)))];
            }
            catch (Exception)
            {
                // The session isn't up (it is being restarted): fall through to the files.
            }
        }

        // A profile without a folder of its own (the default one) points at saves anywhere.
        if (profile.RootPath == ".")
            return [];

        var folder = MatcherUtil.NormalizePath(Path.Combine(SettingsService.AppDirectory, profile.RootPath, "saves"));
        if (!Directory.Exists(folder))
            return [];

        List<TrainerSave> saves = [];
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            try
            {
                if (SaveFiles.TryLoad(File.ReadAllBytes(path), out var save, path) && save is not null)
                {
                    var wrapper = new SaveWrapper(save);
                    saves.Add(new(wrapper.Id, wrapper, path, WrittenAt(path)));
                }
            }
            catch (IOException)
            {
                // Being written by the game right now; it will be there next time.
            }
        }
        return saves;
    }

    private static DateTime WrittenAt(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

    // The save the trainer marked as theirs, else the one played last.
    private static TrainerSave? CurrentSave(TrainerProfileDTO profile, List<TrainerSave> saves) =>
        saves.FirstOrDefault(save => save.Id == profile.ActiveSaveId) ?? saves.OrderByDescending(save => save.WrittenAt).FirstOrDefault();

    // A game's own champion was never beaten by anyone here; its source reads "default", which is
    // also the id of the first trainer profile.
    private static bool IsOriginal(ChampionTeamDTO champion) => champion.BeatenAt is null;

    private static bool IsHeldBy(ChampionTeamDTO champion, string profileId) => !IsOriginal(champion) && champion.Source == profileId;

    public async Task<LeagueDTO> GetLeague()
    {
        var store = await profilesService.GetProfiles();
        var champions = await championsService.GetChampions();

        return new([.. ConnectCatalog.Regions
            .Where(region => champions.Regions.ContainsKey(region.Id))
            .Select(region =>
            {
                var champion = champions.Regions[region.Id];
                var past = champions.PastHolders is { } history && history.TryGetValue(region.Id, out var names) ? names : [];
                return new LeagueRegionDTO(
                    region.Id, champion,
                    IsOriginal: IsOriginal(champion),
                    HolderTrainerId: store.Profiles.Find(profile => IsHeldBy(champion, profile.Id))?.Id,
                    past);
            })]);
    }

    public async Task<TravelPreviewDTO> GetTravelPreview()
    {
        var store = await profilesService.GetProfiles();
        var profile = store.Profiles.Find(p => p.Id == store.ActiveProfileId)
            ?? throw new InvalidOperationException("No active trainer");

        var saves = SavesOf(profile, isActive: true);
        var current = CurrentSave(profile, saves);
        var fromGame = current is null ? profile.PendingGameVersion : ConnectCatalog.GameIdFor(current.Save.GetSave());
        var party = current is null ? [] : current.Save.GetPartyData().Where(pk => pk.Species > 0 && !pk.IsEgg).ToList();
        var owned = saves.Select(save => ConnectCatalog.GameIdFor(save.Save.GetSave())).OfType<string>().ToHashSet();
        var conquered = (profile.ConqueredRegions ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var destinations = ConnectCatalog.Get().Games
            .Where(game => game.Playable && game.Id != fromGame)
            .Select(game => new TravelDestinationDTO(
                game,
                owned.Contains(game.Id),
                conquered.Contains(game.Region),
                [.. party.Select(pk => new TravelMonDTO(
                    pk.Species, pk.Nickname, pk.CurrentLevel,
                    CanTravel: pk.Species <= game.MaxSpecies,
                    MovesLost: [.. pk.Moves.Where(move => move > game.MaxMove).Select(move => (int)move)]))]))
            .ToList();

        return new(fromGame, ConnectRegions.ForGameId(fromGame), destinations);
    }
}