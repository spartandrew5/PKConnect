using System.Text.Json.Serialization;

namespace PKConnect.Core.champions;

/// <summary>How the champions are written to disk (the bundled defaults and the store of titles won).</summary>
[JsonSerializable(typeof(ChampionsDTO))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true
)]
public partial class ChampionsStoreJsonContext : JsonSerializerContext
{
}