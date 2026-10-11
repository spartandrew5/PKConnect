using System.Text.Json;

namespace PKConnect.Core.launch;

public class GymTeamPoolsFile
{
    public double DefaultLevelVariance { get; set; } = 2;
    public List<GymLeaderPool> Leaders { get; set; } = [];
}

public class GymLeaderPool
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public Dictionary<string, int> TrainerIds { get; set; } = [];
    public int TeamSize { get; set; }
    public List<GymPoolMon> Pool { get; set; } = [];
}

public class GymPoolMon
{
    public string? SpeciesName { get; set; }
    public int Species { get; set; }
    public int Item { get; set; }
    public int[] Moves { get; set; } = [];
}

/// <summary>
/// Loads (and caches) the user-customizable gym leader team pool config from
/// connect-data/gym-team-pools.json, shipped next to the backend executable.
/// Editing that file (species/item/move ids, team sizes, pool contents) does
/// NOT require a rebuild - only a backend restart to bust the in-memory cache.
/// </summary>
public static class GymTeamPoolConfigLoader
{
    private static GymTeamPoolsFile? cached;
    private static readonly Lock gate = new();

    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "connect-data", "gym-team-pools.json");

    public static GymTeamPoolsFile Load()
    {
        lock (gate)
        {
            cached ??= LoadFromDisk();
            return cached;
        }
    }

    private static GymTeamPoolsFile LoadFromDisk()
    {
        var json = File.ReadAllText(ConfigPath);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<GymTeamPoolsFile>(json, options)
            ?? throw new InvalidOperationException($"Failed to parse gym team pool config at {ConfigPath}");
    }
}
