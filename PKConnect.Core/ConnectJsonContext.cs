using System.Text.Json.Serialization;
using PKConnect.Core.status;

namespace PKConnect.Core;

/// <summary>Every type Connect's routes take or return (PKVault's routes serialize through generated contexts only).</summary>
[JsonSerializable(typeof(ConnectStatusDTO))]
public partial class ConnectJsonContext : JsonSerializerContext
{
}