using PKVault.Core;

namespace PKConnect.Core.launch;

[Route("api/[controller]")]
public class PatchController
{
    /// <summary>
    /// Writes the launch patch for a game beside its ROM. To call before every launch.
    /// </summary>
    /// <param name="gameId">The game, e.g. "emerald".</param>
    /// <param name="romPath">The unmodified ROM on this machine.</param>
    /// <param name="averageLevel">The travelling party's average level; gym teams are built around it.</param>
    /// <param name="homeRegion">The trainer's home region slug; a game of another region is a visit.</param>
    [HttpPost("launch")]
    public PatchResultDTO Launch(string gameId, string romPath, double? averageLevel = null, string? homeRegion = null)
    {
        return LaunchPatchService.WriteFor(gameId.Trim().ToLowerInvariant(), romPath, averageLevel, homeRegion);
    }
}