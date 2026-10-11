using System.Text.Json;

namespace PKConnect.Core.champions;

public class ChampionBattleConfigFile
{
    /// <summary>Added to the travelling party's average level when a stored champion team is re-centered on it.</summary>
    public int ChampionLevelBonus { get; set; } = 4;

    /// <summary>
    /// The League champion's gTrainers[] ids in the retail GBA ROMs (from the pret decomps'
    /// include/constants/opponents.h). Every id listed for a game gets the same team, which covers
    /// FireRed's one battle per rival starter.
    /// </summary>
    public Dictionary<string, int[]> GbaChampionTrainerIds { get; set; } = [];
}

/// <summary>
/// Loads (and caches) connect-data/champion-battle-config.json, beside the program. Editing the
/// file only needs a restart.
/// </summary>
public static class ChampionBattleConfigLoader
{
    private static ChampionBattleConfigFile? cachedConfig;
    private static readonly Lock gate = new();

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "connect-data", "champion-battle-config.json");

    public static ChampionBattleConfigFile Load()
    {
        lock (gate)
        {
            cachedConfig ??= File.Exists(ConfigPath)
                ? JsonSerializer.Deserialize<ChampionBattleConfigFile>(File.ReadAllText(ConfigPath), Options) ?? new()
                : new();
            return cachedConfig;
        }
    }
}