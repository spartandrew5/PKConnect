using PKConnect.Core.champions;
using PKConnect.Core.profiles;
using PKConnect.Core.regions;

namespace PKConnect.Core.journey;

public record TrainerPartyMonDTO(int Species, string Nickname, int Level, bool IsShiny, bool IsEgg);

/// <summary>One page of a trainer's passport.</summary>
/// <param name="Visited">The trainer has a save of a game set in the region.</param>
/// <param name="Badges">The region's eight badges, in gym order.</param>
/// <param name="HoldsTitle">The trainer is the region's champion right now.</param>
/// <param name="Conquered">The trainer has been the region's champion at least once.</param>
public record PassportRegionDTO(string Region, bool Visited, bool[] Badges, int BadgeCount, bool HoldsTitle, bool Conquered);

/// <summary>One of a trainer's saves.</summary>
/// <param name="SaveId">The id PKVault's own routes know the save by.</param>
/// <param name="GameId">The game (see the catalog), or null for a game Connect doesn't know.</param>
public record TrainerSaveDTO(uint SaveId, string? GameId, string? Region, int PlayedSeconds, string FileName);

/// <summary>
/// A trainer as the trainer card and the roster show them.
/// </summary>
/// <param name="TrainerName">The name in their current save; the profile's <paramref name="Name"/> until there is one.</param>
/// <param name="TrainerId">The ID number in their current save.</param>
/// <param name="CurrentGameId">The game they are in now: their active save's, else the game they are about to start.</param>
/// <param name="Party">The party of their current save - who travels with them.</param>
public record TrainerSummaryDTO(
    string Id,
    string Name,
    bool IsActive,
    string TrainerName,
    uint? TrainerId,
    string? HomeRegion,
    string? CurrentGameId,
    string? CurrentRegion,
    uint? CurrentSaveId,
    int HoursPlayed,
    List<TrainerPartyMonDTO> Party,
    List<PassportRegionDTO> Passport,
    List<TrainerSaveDTO> Saves,
    TrainerAppearanceDTO? Appearance
);

public record JourneyDTO(string? ActiveTrainerId, List<TrainerSummaryDTO> Trainers);

/// <summary>A region's League: who the champion is and who came before.</summary>
/// <param name="IsOriginal">The game's own champion still holds the title.</param>
/// <param name="HolderTrainerId">The trainer who holds it, when it is one of this player's.</param>
public record LeagueRegionDTO(string Region, ChampionTeamDTO Champion, bool IsOriginal, string? HolderTrainerId, List<string> PastHolders);

public record LeagueDTO(List<LeagueRegionDTO> Regions);

/// <summary>What happens to one party member on a trip.</summary>
/// <param name="CanTravel">Its species exists in the destination's game.</param>
/// <param name="MovesLost">The moves it knows that don't exist there.</param>
public record TravelMonDTO(int Species, string Nickname, int Level, bool CanTravel, List<int> MovesLost);

/// <param name="HasSave">The trainer already has a save there; otherwise the trip starts one.</param>
/// <param name="Conquered">The trainer has already been champion of its region.</param>
public record TravelDestinationDTO(GameDTO Game, bool HasSave, bool Conquered, List<TravelMonDTO> Party);

/// <summary>Where the active trainer could go from where they are, and who could come along.</summary>
public record TravelPreviewDTO(string? FromGameId, string? FromRegion, List<TravelDestinationDTO> Destinations);