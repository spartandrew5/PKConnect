using PKVault.Core;

namespace PKConnect.Core.champions;

[Route("api/[controller]")]
public class ChampionsController(IChampionsService championsService, ISavesLoadersService savesLoadersService)
{
    /// <summary>
    /// The teams a loaded save has entered into its Hall of Fame, oldest first. The newest one is
    /// what a trainer becomes champion with.
    /// </summary>
    [HttpGet("hall-of-fame/{saveId}")]
    public List<HallOfFameTeamDTO> GetHallOfFame(uint saveId)
    {
        return HallOfFameReader.Read(savesLoadersService.GetLoadersRequired(saveId).Save.GetSave());
    }

    /// <summary>Every region's current champion.</summary>
    [HttpGet()]
    public async Task<ChampionsDTO> Get()
    {
        return await championsService.GetChampions();
    }

    /// <summary>Crowns a new champion of a region.</summary>
    [HttpPut("{region}")]
    public async Task<ChampionsDTO> SetChampion(string region, SetChampionRequestDTO payload)
    {
        return await championsService.SetChampion(
            region,
            payload.Champion,
            payload.SaveId,
            payload.HallOfFameFingerprint);
    }
}