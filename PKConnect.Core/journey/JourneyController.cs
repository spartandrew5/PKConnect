using PKConnect.Core.profiles;
using PKVault.Core;

namespace PKConnect.Core.journey;

/// <summary>What the app's screens show: one call per screen.</summary>
[Route("api/[controller]")]
public class JourneyController(
    IJourneyService journeyService, IJournalService journalService, ITrainerProfilesService profilesService,
    ITravelService travelService)
{
    /// <summary>
    /// Takes the active trainer and their party to another game: the party is moved into that
    /// game's save (started from the base save when the trainer has none there) and that save
    /// becomes the one they are in. Refused while there are changes waiting to be saved.
    /// </summary>
    [HttpPost("travel/{gameId}")]
    public async Task<BoardingResultDTO> Board(string gameId)
    {
        return await travelService.Board(gameId);
    }

    /// <summary>Every trainer, as the trainer card, the passport and the roster show them.</summary>
    [HttpGet()]
    public async Task<JourneyDTO> GetTrainers()
    {
        return await journeyService.GetTrainers();
    }

    /// <summary>Each region's champion, and who held the title before.</summary>
    [HttpGet("league")]
    public async Task<LeagueDTO> GetLeague()
    {
        return await journeyService.GetLeague();
    }

    /// <summary>A trainer's journal, newest entry first.</summary>
    [HttpGet("journal/{profileId}")]
    public async Task<List<JournalEntryDTO>> GetJournal(string profileId)
    {
        return await journalService.Get(profileId);
    }

    /// <summary>
    /// Where the active trainer could travel, and for each place which of the party could come
    /// and which moves they would have to leave behind.
    /// </summary>
    [HttpGet("travel")]
    public async Task<TravelPreviewDTO> GetTravelPreview()
    {
        return await journeyService.GetTravelPreview();
    }

    /// <summary>Sets how a trainer looks. An empty body gives them each game's own protagonist back.</summary>
    [HttpPut("appearance/{profileId}")]
    public async Task<JourneyDTO> SetAppearance(string profileId, TrainerAppearanceDTO appearance)
    {
        await profilesService.SetAppearance(profileId, string.IsNullOrWhiteSpace(appearance.Character) && appearance.Styles is not { Count: > 0 } ? null : appearance);
        return await journeyService.GetTrainers();
    }
}