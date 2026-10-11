using PKVault.Core;

namespace PKConnect.Core.play;

/// <summary>Starting the trainer's game, and where the games and emulators are.</summary>
[Route("api/[controller]")]
public class PlayController(IPlayService playService)
{
    /// <summary>The game being played, or how the last one ended.</summary>
    [HttpGet()]
    public PlayStatusDTO GetStatus()
    {
        return playService.GetStatus();
    }

    /// <summary>
    /// Starts the active trainer's game in its emulator and answers at once. A trainer without a
    /// save starts a new game; the first save made in it becomes theirs.
    /// </summary>
    [HttpPost()]
    public async Task<PlayStatusDTO> Start()
    {
        return await playService.Start();
    }

    [HttpGet("settings")]
    public PlaySettingsDTO GetSettings()
    {
        return playService.GetSettings();
    }

    /// <summary>Replaces the settings. A path left empty is forgotten.</summary>
    [HttpPut("settings")]
    public async Task<PlaySettingsDTO> SetSettings(PlaySettingsDTO settings)
    {
        return await playService.SetSettings(settings);
    }
}