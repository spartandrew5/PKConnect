using System.Text.Json;

namespace PKConnect.Core.champions;

public interface IChampionsService
{
    /// <summary>
    /// Current champion for every region: bundled defaults, with any
    /// player-won titles overlaid on top.
    /// </summary>
    Task<ChampionsDTO> GetChampions();

    /// <summary>
    /// Promote a team to "current champion" of <paramref name="region"/> and
    /// persist it to the mutable store. When <paramref name="saveId"/> and
    /// <paramref name="hallOfFameFingerprint"/> are given they are recorded as
    /// that save's Hall-of-Fame snapshot in the same write, so the caller can
    /// later tell a brand-new induction apart from one it has already crowned.
    /// Returns the merged view (same shape as <see cref="GetChampions"/>).
    /// </summary>
    Task<ChampionsDTO> SetChampion(
        string region,
        ChampionTeamDTO champion,
        string? saveId = null,
        string? hallOfFameFingerprint = null);
}

/**
 * See ChampionsDTO for the two-layer model. Defaults are read once from the
 * bundled JSON next to the binary (same approach as GbGymTeamPoolConfigLoader);
 * the mutable store lives under the app data directory alongside
 * trainer-profiles.json and is guarded by a process-wide semaphore.
 */
public class ChampionsService(PKVault.Core.IFileIOService fileIOService) : IChampionsService
{
    private const string StorePath = "./config/current-champions.json";

    private static readonly string DefaultsPath =
        Path.Combine(AppContext.BaseDirectory, "connect-data", "default-champions.json");

    private static readonly SemaphoreSlim Semaphore = new(1);

    private static ChampionsDTO? cachedDefaults;
    private static readonly Lock defaultsGate = new();

    public async Task<ChampionsDTO> GetChampions()
    {
        await Semaphore.WaitAsync();
        try
        {
            return Merge(ReadDefaults(), ReadStore());
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<ChampionsDTO> SetChampion(
        string region,
        ChampionTeamDTO champion,
        string? saveId = null,
        string? hallOfFameFingerprint = null)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            throw new ArgumentException("Region is required");
        }

        var key = region.Trim().ToLowerInvariant();

        await Semaphore.WaitAsync();
        try
        {
            var store = ReadStore();

            // Whoever held the title until now goes into its history - unless the same trainer
            // is only replacing their own team.
            if (Merge(ReadDefaults(), store).Regions.TryGetValue(key, out var previous)
                && (previous.Source != champion.Source || previous.ChampionName != champion.ChampionName))
            {
                if (!store.PastHolders!.TryGetValue(key, out var past))
                    store.PastHolders[key] = past = [];
                past.Add(previous.ChampionName);
            }
            store.Regions[key] = champion;

            if (!string.IsNullOrWhiteSpace(saveId) && hallOfFameFingerprint is not null)
            {
                store.SaveSnapshots![saveId] = hallOfFameFingerprint;
            }

            await fileIOService.WriteJSONFile(StorePath, ChampionsStoreJsonContext.Default.ChampionsDTO, store);

            return Merge(ReadDefaults(), store);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    private ChampionsDTO ReadStore()
    {
        var stored = fileIOService.ReadJSONFileSync(
            StorePath,
            ChampionsStoreJsonContext.Default.ChampionsDTO,
            new ChampionsDTO([])
        );

        // Defensive: never hand callers a null dictionary (a store written
        // before SaveSnapshots existed has none).
        return stored with
        {
            Regions = stored.Regions ?? [],
            SaveSnapshots = stored.SaveSnapshots ?? [],
            PastHolders = stored.PastHolders ?? [],
        };
    }

    private static ChampionsDTO ReadDefaults()
    {
        lock (defaultsGate)
        {
            if (cachedDefaults is not null)
            {
                return cachedDefaults;
            }

            if (!File.Exists(DefaultsPath))
            {
                cachedDefaults = new ChampionsDTO([]);
                return cachedDefaults;
            }

            var json = File.ReadAllText(DefaultsPath);
            cachedDefaults = JsonSerializer.Deserialize(json, ChampionsStoreJsonContext.Default.ChampionsDTO)
                ?? new ChampionsDTO([]);
            return cachedDefaults;
        }
    }

    private static ChampionsDTO Merge(ChampionsDTO defaults, ChampionsDTO overrides)
    {
        var merged = new Dictionary<string, ChampionTeamDTO>(
            defaults.Regions ?? [],
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var (region, champion) in overrides.Regions ?? [])
        {
            merged[region] = champion;
        }

        // Snapshots only ever live in the mutable store.
        return new ChampionsDTO(merged, overrides.SaveSnapshots ?? [], overrides.PastHolders ?? []);
    }
}
