using PKConnect.Core.champions;
using PKConnect.Core.regions;
using PKVault.Core;

namespace PKConnect.Core.launch;

[Route("api/[controller]")]
public class PatchController(IChampionsService championsService)
{
    /// <summary>
    /// Writes the launch patch for a game beside its ROM. To call before every launch.
    /// </summary>
    /// <param name="gameId">The game, e.g. "emerald".</param>
    /// <param name="romPath">The unmodified ROM on this machine.</param>
    /// <param name="averageLevel">The travelling party's average level; gym teams and the champion are scaled to it.</param>
    /// <param name="homeRegion">The trainer's home region slug; a game of another region is a visit.</param>
    [HttpPost("launch")]
    public async Task<PatchResultDTO> Launch(string gameId, string romPath, double? averageLevel = null, string? homeRegion = null)
    {
        gameId = gameId.Trim().ToLowerInvariant();

        var champions = await championsService.GetChampions();
        var champion = ConnectRegions.ForGameId(gameId) is { } region && champions.Regions.TryGetValue(region, out var current)
            ? current
            : null;

        return LaunchPatchService.WriteFor(gameId, romPath, averageLevel, homeRegion, champion);
    }
}