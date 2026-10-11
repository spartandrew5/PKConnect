
namespace PKConnect.Core.champions;
/**
 * "Current champion" of a region — the team any newly-created trainer faces
 * when they challenge that region's Pokemon League.
 *
 * Two layers feed the value the API returns (see ChampionsService):
 *   1. Bundled defaults  -> champions/data/default-champions.json (editable,
 *      canonical Gen-3 champion teams; shared by every game in a region, e.g.
 *      Ruby and Emerald both use the Hoenn entry).
 *   2. Mutable overrides  -> {app-dir}/config/current-champions.json, written
 *      whenever a trainer beats a region and their newest Hall of Fame team
 *      takes the title.
 *
 * Region keys are the same lowercase slugs used by the per-trainer badge
 * ledger: "kanto" / "johto" / "hoenn". As with RegionBadges, the backend is a
 * dumb store — the frontend decides which region slug a save belongs to.
 */

public record ChampionMonDTO(
    // National Dex id.
    int Species,
    int Level,
    string? Nickname = null,
    // Move names (e.g. "Flamethrower"). Null/empty for player-derived champions
    // — the in-game Hall of Fame only records species + level + nickname.
    string[]? Moves = null,
    // Held item name (e.g. "Sitrus Berry"). Null when none.
    string? HeldItem = null
);

public record ChampionTeamDTO(
    string ChampionName,
    List<ChampionMonDTO> Team,
    // "default" = the bundled base champion; otherwise the trainer-profile id
    // whose Hall of Fame produced this team.
    string Source = "default",
    // null for the bundled default; set when a player took the title.
    DateTime? BeatenAt = null,
    // Backend GameVersion name the title was won in (informational). Null for
    // defaults.
    string? GameVersion = null,
    // Protagonist id (a folder of the protagonists sprite sets, e.g. "nate") the champion
    // looks like when no trainer profile's appearance is sent with the launch. Null = the game's
    // own champion sprite. Only the Gen 5 Connect launch reads it so far.
    string? Appearance = null
);

// region slug ("kanto" / "johto" / "hoenn") -> current champion for that region
public record ChampionsDTO(
    Dictionary<string, ChampionTeamDTO> Regions,
    // saveId -> a fingerprint of that save's Hall of Fame as of the last time
    // it took the title. This is what makes the title change on EVENTS rather
    // than on viewing: a save is only crowned when its Hall of Fame differs
    // from its recorded snapshot (it just gained an induction), or when it has
    // no snapshot at all (a freshly imported save that already had one).
    // Merely switching to a trainer whose save has long had an induction
    // matches its snapshot and never steals the title.
    // Only ever written by the mutable store; the bundled defaults have none.
    Dictionary<string, string>? SaveSnapshots = null,
    // region slug -> the names of those who held the title before the current champion, oldest
    // first. The bundled champion a trainer took it from is the first of them.
    Dictionary<string, List<string>>? PastHolders = null
);

// PUT /api/champions/{region} body. SaveId + HallOfFameFingerprint are recorded
// together with the new champion so the crown and its snapshot can never drift.
public record SetChampionRequestDTO(
    ChampionTeamDTO Champion,
    string? SaveId = null,
    string? HallOfFameFingerprint = null
);
