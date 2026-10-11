namespace PKConnect.Core.launch;

/// <summary>What a launch patch did, for the client to show.</summary>
public record PatchResultDTO(
    bool Success,
    int EditsApplied,
    string PatchPath,
    List<string> Warnings,
    int TeamsGenerated = 0,
    int TeamsSkipped = 0,
    // The Connect base patch was layered in (a visiting launch).
    bool BasePatchApplied = false,
    // The game is in the trainer's home region: it launches as itself.
    bool HomeRegionLaunch = false
);