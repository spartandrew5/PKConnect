using System.Text.Json.Serialization;
using PKConnect.Core.champions;
using PKConnect.Core.journey;
using PKConnect.Core.regions;
using PKConnect.Core.launch;
using PKConnect.Core.play;
using PKConnect.Core.profiles;
using PKConnect.Core.status;

namespace PKConnect.Core;

/// <summary>Every type Connect's routes take or return (PKVault's routes serialize through generated contexts only).</summary>
[JsonSerializable(typeof(ConnectStatusDTO))]
[JsonSerializable(typeof(PatchResultDTO))]
[JsonSerializable(typeof(ChampionsDTO))]
[JsonSerializable(typeof(SetChampionRequestDTO))]
[JsonSerializable(typeof(List<HallOfFameTeamDTO>))]
[JsonSerializable(typeof(TrainerProfilesDTO))]
[JsonSerializable(typeof(RegionBadgesDTO))]
[JsonSerializable(typeof(SaveSyncResultDTO))]
[JsonSerializable(typeof(CatalogDTO))]
[JsonSerializable(typeof(PlayStatusDTO))]
[JsonSerializable(typeof(PlaySettingsDTO))]
[JsonSerializable(typeof(NamesDTO))]
[JsonSerializable(typeof(JourneyDTO))]
[JsonSerializable(typeof(LeagueDTO))]
[JsonSerializable(typeof(TravelPreviewDTO))]
[JsonSerializable(typeof(BoardingResultDTO))]
[JsonSerializable(typeof(List<JournalEntryDTO>))]
[JsonSerializable(typeof(TrainerAppearanceDTO))]
public partial class ConnectJsonContext : JsonSerializerContext
{
}