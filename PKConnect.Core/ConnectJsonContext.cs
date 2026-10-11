using System.Text.Json.Serialization;
using PKConnect.Core.champions;
using PKConnect.Core.launch;
using PKConnect.Core.profiles;
using PKConnect.Core.status;

namespace PKConnect.Core;

/// <summary>Every type Connect's routes take or return (PKVault's routes serialize through generated contexts only).</summary>
[JsonSerializable(typeof(ConnectStatusDTO))]
[JsonSerializable(typeof(PatchResultDTO))]
[JsonSerializable(typeof(ChampionsDTO))]
[JsonSerializable(typeof(SetChampionRequestDTO))]
[JsonSerializable(typeof(TrainerProfilesDTO))]
[JsonSerializable(typeof(RegionBadgesDTO))]
public partial class ConnectJsonContext : JsonSerializerContext
{
}