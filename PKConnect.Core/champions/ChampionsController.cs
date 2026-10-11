using PKVault.Core;

namespace PKConnect.Core.champions;

[Route("api/[controller]")]
public class ChampionsController(IChampionsService championsService)
{
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