namespace PKConnect.Core.status;

/// <summary>What a client needs to know it is talking to a Connect build.</summary>
public record ConnectStatusDTO(
    string Version,
    string PKVaultVersion
);