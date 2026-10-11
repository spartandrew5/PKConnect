using PKVault.Core;

namespace PKConnect.Core.profiles;

public record TrainerProfileDTO(
    string Id,
    string Name,
    DateTime CreatedAt,
    DateTime LastPlayedAt,
    string RootPath,
    SettingsMutableDTO SettingsMutable,
    uint? ActiveSaveId = null,
    string? PendingGameVersion = null,
    // Per-region gym-badge "ledger": region key ("kanto"/"johto"/"hoenn") ->
    // 8-slot earned array. Authoritative cross-save badge state for this
    // trainer. All flag-offset knowledge lives in the frontend; the backend
    // just stores/returns these arrays. Null on legacy profiles.
    Dictionary<string, bool[]>? RegionBadges = null,
    // Per-save snapshot of what each save's region badges were the last time
    // that save was synced: saveId -> (region -> 8-slot array). Lets the client
    // tell "earned in-game since last sync" apart from "stale, should be
    // removed to match a sibling's edit".
    Dictionary<string, Dictionary<string, bool[]>>? RegionBadgeSnapshots = null,
    // Region slug ("kanto"/"johto"/"hoenn") the trainer started in: picked at
    // creation, or taken from the imported save's game. Games in this region
    // launch vanilla; every other region launches with the Connect base patch.
    string? HomeRegion = null,
    // Region slugs this trainer has become champion of at least once, ever —
    // append-only (never cleared even if another trainer later takes the
    // title). Drives the FLAG_CONNECT_CHAMPION_<REGION> save flags and the
    // VAR_CONNECT_REGIONS_CLEARED count a Connect base patch reads for gym
    // dialogue. All flag/var-offset knowledge lives in the frontend; the
    // backend just stores/returns this list. Null on legacy profiles.
    List<string>? ConqueredRegions = null,
    // How the trainer looks in the games. Null = each game's own protagonist.
    TrainerAppearanceDTO? Appearance = null
);

/// <summary>
/// A trainer's look. Cosmetic only.
/// </summary>
/// <param name="Character">The character they appear as in every game (a sprite set's id, e.g. "hilda").</param>
/// <param name="Styles">A different sprite set for one generation: "gen3" -> "brendan".</param>
public record TrainerAppearanceDTO(string? Character, Dictionary<string, string>? Styles = null);

public record RegionBadgesDTO(
    Dictionary<string, bool[]> Regions,
    Dictionary<string, Dictionary<string, bool[]>>? Snapshots = null
);

public record TrainerProfilesDTO(
    List<TrainerProfileDTO> Profiles,
    string? ActiveProfileId = null,
    string? CreatedProfileId = null
);
