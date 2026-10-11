using System.Text.Json;

namespace PKConnect.Core.launch;

public class GbGymTeamPoolsFile
{
    public Dictionary<string, GbGameConfig> Games { get; set; } = [];
}

public class GbGameConfig
{
    public bool HasMoves { get; set; }
    public List<GbLeaderPool> Leaders { get; set; } = [];
}

public class GbLeaderPool
{
    /// <summary>Matched (case/punctuation-insensitive) against the gym <c>leader</c> in the base patch manifest.</summary>
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>How many Pokemon are drawn from <see cref="Pool"/>, capped at the landing zone's capacity.</summary>
    public int TeamSize { get; set; }

    public List<GbPoolMon> Pool { get; set; } = [];
}

public class GbPoolMon
{
    public string? SpeciesName { get; set; }
    public int Species { get; set; }
    public int[]? Moves { get; set; }
}

/// <summary>
/// Loads (and caches) the Gen 1 (Yellow) / Gen 2 (Crystal) gym leader team
/// pool config from connect-data/gb-gym-team-pools.json.
/// </summary>
public static class GbGymTeamPoolConfigLoader
{
    private static GbGymTeamPoolsFile? cached;
    private static readonly Lock gate = new();

    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "connect-data", "gb-gym-team-pools.json");

    public static GbGymTeamPoolsFile Load()
    {
        lock (gate)
        {
            cached ??= LoadFromDisk();
            return cached;
        }
    }

    private static GbGymTeamPoolsFile LoadFromDisk()
    {
        var json = File.ReadAllText(ConfigPath);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<GbGymTeamPoolsFile>(json, options)
            ?? throw new InvalidOperationException($"Failed to parse GB gym team pool config at {ConfigPath}");
    }
}
