using PKConnect.Core.champions;
using PKHeX.Core;
using PKVault.Core;

namespace PKConnect.Core.profiles;

/// <summary>What bringing a save into step with its trainer changed.</summary>
/// <param name="SaveWritten">The save file was rewritten.</param>
/// <param name="BadgesChanged">The save's gym badges were changed to match the trainer's.</param>
/// <param name="ConnectStateChanged">The home region or conquered regions in the save were updated.</param>
/// <param name="CrownedRegion">The region the trainer just became champion of, if any.</param>
/// <param name="RegionBadges">The trainer's badges, by region, after the sync.</param>
public record SaveSyncResultDTO(
    bool SaveWritten,
    bool BadgesChanged,
    bool ConnectStateChanged,
    string? CrownedRegion,
    Dictionary<string, bool[]> RegionBadges
);

public interface ISaveSyncService
{
    /// <summary>
    /// Brings one of the active trainer's saves into step with the trainer. To call when a save
    /// becomes the one in use, and with <paramref name="afterPlay"/> when the player comes back
    /// from the game or brings in a save of their own - the only moments a title can change hands.
    /// </summary>
    Task<SaveSyncResultDTO> SyncSave(uint saveId, bool afterPlay);
}

/// <summary>
/// A trainer is more than one save: their badges belong to regions, the regions they have
/// conquered follow them everywhere, and beating a League makes them its champion. This is where
/// a save and its trainer are reconciled:
///  1. gym badges, against the trainer's ledger (see <see cref="RegionBadges.Reconcile"/>);
///  2. the title, when the save's Hall of Fame has a team it didn't have before
///     (see <see cref="ChampionCrowning"/>);
///  3. the Connect state the game reads from the save (see <see cref="ConnectSaveState"/>).
///
/// It works on the file, not on the copy the session holds: the game may just have written it.
/// The session's saves are read again afterwards, unless there are actions waiting to be saved -
/// then the file is left for the next sync, which finds the same things to do.
/// </summary>
public class SaveSyncService(
    ITrainerProfilesService profilesService,
    IChampionsService championsService,
    ISavesLoadersService savesLoadersService,
    ISessionService sessionService,
    IFileIOService fileIOService,
    TimeProvider timeProvider
) : ISaveSyncService
{
    public async Task<SaveSyncResultDTO> SyncSave(uint saveId, bool afterPlay)
    {
        var store = await profilesService.GetProfiles();
        var profile = store.Profiles.Find(p => p.Id == store.ActiveProfileId)
            ?? throw new InvalidOperationException("No active trainer");

        var path = savesLoadersService.GetLoadersRequired(saveId).Save.Metadata.FilePath
            ?? throw new InvalidOperationException($"Save {saveId} has no file");
        var canWrite = sessionService.HasEmptyActionList();

        if (!SaveUtil.TryGetSaveFile(await fileIOService.ReadBytes(path), out var save, path) || save is null)
            throw new InvalidOperationException($"Not a save file: {path}");

        var saveKey = saveId.ToString();

        // 1. Badges.
        var badges = await profilesService.GetRegionBadges(profile.Id);
        Dictionary<string, bool[]>? snapshot = null;
        badges.Snapshots?.TryGetValue(saveKey, out snapshot);
        var reconciled = RegionBadges.Reconcile(save, badges.Regions, snapshot);
        var badgesChanged = reconciled.SaveChanged;
        // Without the right to write the save, what was decided for it isn't recorded as done.
        if (reconciled.Ledger.Count > 0 || (canWrite && reconciled.Snapshot.Count > 0))
        {
            badges = await profilesService.SetRegionBadges(profile.Id, new(
                reconciled.Ledger,
                canWrite && reconciled.Snapshot.Count > 0 ? new() { [saveKey] = reconciled.Snapshot } : null));
        }

        // 2. The title.
        string? crownedRegion = null;
        if (afterPlay)
        {
            crownedRegion = await TryCrown(save, saveKey, profile);
            if (crownedRegion is not null)
            {
                store = await profilesService.AddConqueredRegions(profile.Id, [crownedRegion]);
                profile = store.Profiles.Find(p => p.Id == profile.Id) ?? profile;
            }
        }

        // 3. What the game reads about its player.
        var stateChanged = ConnectSaveState.Apply(save, profile.HomeRegion, profile.ConqueredRegions ?? []);

        var written = false;
        if (canWrite && (badgesChanged || stateChanged))
        {
            await fileIOService.WriteBytes(path, save.Write().ToArray());
            written = true;
        }
        // The session holds what the file was when it was last read.
        if (canWrite && (written || afterPlay))
            await savesLoadersService.Setup(new DataUpdateFlags());

        return new(written, badgesChanged, stateChanged, crownedRegion, badges.Regions);
    }

    private async Task<string?> TryCrown(SaveFile save, string saveKey, TrainerProfileDTO profile)
    {
        var hallOfFame = HallOfFameReader.Read(save);
        if (hallOfFame.Count == 0 || ChampionCrowning.RegionOf(save) is not { } region || !ChampionCrowning.HasEarnedTheTitle(save))
            return null;

        var fingerprint = ChampionCrowning.Fingerprint(hallOfFame);
        var champions = await championsService.GetChampions();
        if (champions.SaveSnapshots is { } snapshots && snapshots.TryGetValue(saveKey, out var known) && known == fingerprint)
            return null; // already champion for exactly this Hall of Fame

        if (ChampionCrowning.BuildChampion(save, hallOfFame, profile.Id, profile.Name, timeProvider.GetUtcNow().UtcDateTime) is not { } champion)
            return null;

        // The title and the snapshot it was taken with are stored in one write, so they can't drift apart.
        await championsService.SetChampion(region, champion, saveKey, fingerprint);
        return region;
    }
}