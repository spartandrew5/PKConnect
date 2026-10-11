using PKVault.Core;

namespace PKConnect.Core.profiles;

/// <summary>
/// Trainers. Each has their own storage, saves and backups; one is active at a time, and
/// switching points the whole app at that trainer's data.
/// </summary>
[Route("api/[controller]")]
public class TrainerProfilesController(DataService dataService, ITrainerProfilesService trainerProfilesService)
{
    [HttpGet()]
    public async Task<TrainerProfilesDTO> Get()
    {
        return await trainerProfilesService.GetProfiles();
    }

    /// <param name="pendingGameVersion">The game the trainer is about to start in, when there is no save yet.</param>
    /// <param name="homeRegion">The trainer's home region; taken from the game when left out.</param>
    [HttpPost()]
    public async Task<TrainerProfilesDTO> Create(string name, string? pendingGameVersion = null, string? homeRegion = null)
    {
        return await trainerProfilesService.CreateProfile(name, pendingGameVersion, homeRegion);
    }

    [HttpPut("{profileId}")]
    public async Task<TrainerProfilesDTO> Rename(string profileId, string name)
    {
        return await trainerProfilesService.RenameProfile(profileId, name);
    }

    [HttpDelete("{profileId}")]
    public async Task<TrainerProfilesDTO> Delete(string profileId)
    {
        return await trainerProfilesService.DeleteProfile(profileId);
    }

    /// <summary>Makes a trainer the active one. Refused while there are unsaved actions.</summary>
    [HttpPut("{profileId}/switch")]
    public async Task<DataDTO> Switch(string profileId)
    {
        var flags = await trainerProfilesService.SwitchProfile(profileId);

        return await dataService.CreateDataFromUpdateFlags(flags);
    }

    [HttpPut("{profileId}/save-globs")]
    public async Task<TrainerProfilesDTO> UpdateSaveGlobs(string profileId, string[] saveGlobs)
    {
        return await trainerProfilesService.UpdateProfileSaveGlobs(profileId, saveGlobs, new DataUpdateFlags());
    }

    [HttpPut("{profileId}/active-save")]
    public async Task<TrainerProfilesDTO> SetActiveSave(string profileId, uint? saveId = null)
    {
        return await trainerProfilesService.SetActiveSave(profileId, saveId);
    }

    /// <summary>Adds a save file of the player's own to a trainer.</summary>
    [HttpPost("{profileId}/import-save")]
    public async Task<TrainerProfilesDTO> ImportSave(string profileId, CoreFile file)
    {
        using var memoryStream = new MemoryStream();
        await file.Stream.CopyToAsync(memoryStream);

        return await trainerProfilesService.ImportProfileSave(profileId, file.FileName, memoryStream.ToArray());
    }

    /// <summary>
    /// Gives a trainer the save they visit a game with: the game's base save, under the trainer's
    /// own name and ID, marked with their home region.
    /// </summary>
    [HttpPost("{profileId}/activate-base-save")]
    public async Task<TrainerProfilesDTO> ActivateBaseSave(string profileId, string gameVersion)
    {
        return await trainerProfilesService.ActivateBaseSave(profileId, gameVersion);
    }

    [HttpGet("{profileId}/region-badges")]
    public async Task<RegionBadgesDTO> GetRegionBadges(string profileId)
    {
        return await trainerProfilesService.GetRegionBadges(profileId);
    }

    [HttpPut("{profileId}/region-badges")]
    public async Task<RegionBadgesDTO> SetRegionBadges(string profileId, RegionBadgesDTO payload)
    {
        return await trainerProfilesService.SetRegionBadges(profileId, payload);
    }

    [HttpPut("{profileId}/conquered-regions")]
    public async Task<TrainerProfilesDTO> AddConqueredRegions(string profileId, string[] regions)
    {
        return await trainerProfilesService.AddConqueredRegions(profileId, regions);
    }
}