using System.Text.Json.Serialization;

namespace PKConnect.Core.profiles;

/// <summary>How the trainer profiles are written to disk.</summary>
[JsonSerializable(typeof(TrainerProfilesDTO))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
public partial class TrainerProfilesStoreJsonContext : JsonSerializerContext
{
}
