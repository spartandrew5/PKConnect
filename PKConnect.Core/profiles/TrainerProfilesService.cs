using System.Text.RegularExpressions;
using PKConnect.Core.regions;
using PKHeX.Core;
using PKVault.Core;

namespace PKConnect.Core.profiles;

public interface ITrainerProfilesService
{
    Task<TrainerProfilesDTO> GetProfiles();
    Task<TrainerProfilesDTO> CreateProfile(string name, string? pendingGameVersion = null, string? homeRegion = null);
    Task<TrainerProfilesDTO> RenameProfile(string profileId, string name);
    Task<TrainerProfilesDTO> DeleteProfile(string profileId);
    Task<DataUpdateFlags> SwitchProfile(string profileId);
    Task<TrainerProfilesDTO> UpdateProfileSaveGlobs(string profileId, string[] saveGlobs, DataUpdateFlags flags);
    Task<TrainerProfilesDTO> SetActiveSave(string profileId, uint? saveId);
    Task<TrainerProfilesDTO> ImportProfileSave(string profileId, string fileName, byte[] fileContent);
    Task<TrainerProfilesDTO> ActivateBaseSave(string profileId, string gameVersion);
    Task<RegionBadgesDTO> GetRegionBadges(string profileId);
    Task<RegionBadgesDTO> SetRegionBadges(string profileId, RegionBadgesDTO payload);
    Task<TrainerProfilesDTO> AddConqueredRegions(string profileId, IEnumerable<string> regions);
}

/**
 * Trainer profiles: each profile owns its own DB/storage/save-globs, backed by
 * the existing single-active-session backend (see docs/SESSION.md). Switching
 * the active profile swaps SettingsService's mutable settings (DB_PATH,
 * STORAGE_PATH, BACKUP_PATH, SAVE_GLOBS) then restarts the backend session to
 * point at that profile's own SQLite DB + save files.
 *
 * The first-ever read auto-creates a "Default" profile pointing at whatever
 * settings are currently active, so existing installs keep working with zero
 * data migration.
 */
public class TrainerProfilesService(
    IFileIOService fileIOService,
    ISettingsService settingsService,
    ISessionService sessionService,
    ISavesLoadersService savesLoadersService,
    TimeProvider timeProvider
) : ITrainerProfilesService
{
    private static readonly string FilePath = MatcherUtil.NormalizePath(Path.Combine(SettingsService.AppDirectory, "./config/trainer-profiles.json"));
    private static readonly string RuntimeBaseSavesDirectoryPath = MatcherUtil.NormalizePath(Path.Combine(SettingsService.AppDirectory, "./profiles/base-saves"));
    private static readonly SemaphoreSlim Semaphore = new(1);
    private const string DefaultProfileId = "default";

    public async Task<TrainerProfilesDTO> GetProfiles()
    {
        EnsureBaseSavesDirectoryExists();

        await Semaphore.WaitAsync();
        try
        {
            var store = ReadStore();
            store = EnsureDefaultProfile(store);
            store = EnsureHomeRegions(store);
            await WriteStore(store);

            return store;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<TrainerProfilesDTO> CreateProfile(string name, string? pendingGameVersion = null, string? homeRegion = null)
    {
        var cleanName = name.Trim();
        if (string.IsNullOrWhiteSpace(cleanName))
        {
            throw new ArgumentException("Profile name is required");
        }

        await Semaphore.WaitAsync();
        try
        {
            var store = ReadStore();
            store = EnsureDefaultProfile(store);

            var now = timeProvider.GetUtcNow().DateTime;
            var id = CreateProfileId(cleanName, store.Profiles.Select(p => p.Id));
            var rootPath = $"./profiles/{id}";
            var language = settingsService.GetSettings().GetLanguageOrDefault();

            var profile = new TrainerProfileDTO(
                Id: id,
                Name: cleanName,
                CreatedAt: now,
                LastPlayedAt: now,
                RootPath: rootPath,
                SettingsMutable: new(
                    DB_PATH: $"{rootPath}/db",
                    SAVE_GLOBS: [],
                    PKM_EXTERNAL_GLOBS: [],
                    STORAGE_PATH: $"{rootPath}/storage",
                    BACKUP_PATH: $"{rootPath}/backup",
                    HIDE_CHEATS: false,
                    SKIP_LEGALITY_CHECKS: false,
                    LANGUAGE: language
                ),
                PendingGameVersion: string.IsNullOrWhiteSpace(pendingGameVersion) ? null : pendingGameVersion,
                HomeRegion: ConnectRegions.Normalize(homeRegion) ?? ConnectRegions.ForGameId(pendingGameVersion)
            );

            store.Profiles.Add(profile);
            await WriteStore(store);

            return store with { CreatedProfileId = profile.Id };
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<TrainerProfilesDTO> ImportProfileSave(string profileId, string fileName, byte[] fileContent)
    {
        if (fileContent.Length == 0)
        {
            throw new ArgumentException("Uploaded save file is empty");
        }

        string relativeGlob;

        await Semaphore.WaitAsync();
        try
        {
            var store = ReadStore();
            store = EnsureDefaultProfile(store);

            var targetIndex = store.Profiles.FindIndex(p => p.Id == profileId);
            if (targetIndex < 0)
            {
                throw new KeyNotFoundException($"Profile not found: {profileId}");
            }

            var targetProfile = store.Profiles[targetIndex];

            var target = store.Profiles[targetIndex];
            var safeFileName = string.IsNullOrWhiteSpace(fileName) ? "imported.sav" : Path.GetFileName(fileName);
            var relativeSavePath = $"{target.RootPath}/saves/{safeFileName}";
            var absoluteSavePath = MatcherUtil.NormalizePath(Path.Combine(SettingsService.AppDirectory, relativeSavePath));

            await fileIOService.WriteBytes(absoluteSavePath, fileContent);

            // Import mode never picks a region up front, so the imported save's own
            // game decides the home region.
            var homeRegion = target.HomeRegion
                ?? (SaveUtil.TryGetSaveFile(fileContent, out var importedSave, safeFileName) && importedSave is not null
                    ? ConnectRegions.ForSave(importedSave, safeFileName)
                    : null);

            // Clear pending-game-version metadata now that a real save exists.
            store.Profiles[targetIndex] = target with { PendingGameVersion = null, HomeRegion = homeRegion };
            await WriteStore(store);

            relativeGlob = $"{target.RootPath}/saves/*";
        }
        finally
        {
            Semaphore.Release();
        }

        var flags = new DataUpdateFlags();

        return await UpdateProfileSaveGlobs(profileId, [relativeGlob], flags);
    }

    public async Task<TrainerProfilesDTO> ActivateBaseSave(string profileId, string gameVersion)
    {
        var normalizedGameVersion = gameVersion.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedGameVersion))
        {
            throw new ArgumentException("Game version is required");
        }

        EnsureBaseSavesDirectoryExists();

        await Semaphore.WaitAsync();
        try
        {
            var store = ReadStore();
            store = EnsureDefaultProfile(store);

            var targetIndex = store.Profiles.FindIndex(p => p.Id == profileId);
            if (targetIndex < 0)
            {
                throw new KeyNotFoundException($"Profile not found: {profileId}");
            }

            var sourceSavePath = ResolveBaseSavePath(normalizedGameVersion);
            var targetProfile = store.Profiles[targetIndex];
            var destinationRelativeSavePath = $"{targetProfile.RootPath}/saves/{normalizedGameVersion}.sav";
            var destinationAbsoluteSavePath = MatcherUtil.NormalizePath(Path.Combine(SettingsService.AppDirectory, destinationRelativeSavePath));

            fileIOService.CreateDirectoryIfAny(destinationAbsoluteSavePath);
            fileIOService.Copy(sourceSavePath, destinationAbsoluteSavePath, overwrite: true);

            var isActiveProfileForTrainerInfoCopy = store.ActiveProfileId == profileId;
            if (isActiveProfileForTrainerInfoCopy && targetProfile.ActiveSaveId is uint referenceSaveId)
            {
                await TryCopyTrainerInfoFromActiveSave(referenceSaveId, destinationAbsoluteSavePath);
            }

            // The game's Connect changes read who the trainer is from the save itself.
            await WriteConnectState(destinationAbsoluteSavePath, targetProfile);

            var requiredGlob = $"{targetProfile.RootPath}/saves/*";
            var currentGlobs = targetProfile.SettingsMutable.SAVE_GLOBS;
            var needsGlobUpdate = !currentGlobs.Any(glob =>
                string.Equals(glob.Trim(), requiredGlob, StringComparison.OrdinalIgnoreCase)
            );

            var nextGlobs = needsGlobUpdate
                ? [.. currentGlobs, requiredGlob]
                : currentGlobs;

            var updatedMutable = targetProfile.SettingsMutable with { SAVE_GLOBS = nextGlobs };
            store.Profiles[targetIndex] = targetProfile with
            {
                PendingGameVersion = null,
                SettingsMutable = updatedMutable,
            };

            var isActiveProfile = store.ActiveProfileId == profileId;
            if (isActiveProfile)
            {
                var flags = new DataUpdateFlags();
                var (restartSession, persistSession) = settingsService.GetUpdateDiff(updatedMutable, flags);

                if (restartSession && !sessionService.HasEmptyActionList())
                {
                    throw new InvalidOperationException("Empty action list is required before changing save paths");
                }

                await settingsService.UpdateSettings(updatedMutable, restartSession, persistSession, flags);
            }

            await WriteStore(store);
            return store;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<TrainerProfilesDTO> RenameProfile(string profileId, string name)
    {
        var cleanName = name.Trim();
        if (string.IsNullOrWhiteSpace(cleanName))
        {
            throw new ArgumentException("Profile name is required");
        }

        await Semaphore.WaitAsync();
        try
        {
            var store = ReadStore();
            store = EnsureDefaultProfile(store);

            var targetIndex = store.Profiles.FindIndex(p => p.Id == profileId);
            if (targetIndex < 0)
            {
                throw new KeyNotFoundException($"Profile not found: {profileId}");
            }

            store.Profiles[targetIndex] = store.Profiles[targetIndex] with
            {
                Name = cleanName,
            };

            await WriteStore(store);
            return store;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<TrainerProfilesDTO> DeleteProfile(string profileId)
    {
        if (!sessionService.HasEmptyActionList())
        {
            throw new InvalidOperationException("Empty action list is required before deleting a trainer profile");
        }

        await Semaphore.WaitAsync();
        try
        {
            var store = ReadStore();
            store = EnsureDefaultProfile(store);

            var targetIndex = store.Profiles.FindIndex(p => p.Id == profileId);
            if (targetIndex < 0)
            {
                throw new KeyNotFoundException($"Profile not found: {profileId}");
            }

            if (store.Profiles.Count <= 1)
            {
                throw new InvalidOperationException("At least one trainer profile is required");
            }

            var targetProfile = store.Profiles[targetIndex];

            var deletingActive = store.ActiveProfileId == profileId;
            store.Profiles.RemoveAt(targetIndex);

            if (deletingActive)
            {
                var nextActive = store.Profiles.OrderByDescending(p => p.LastPlayedAt).First();
                store = store with
                {
                    ActiveProfileId = nextActive.Id,
                };

                var flags = new DataUpdateFlags();
                var (restartSession, persistSession) = settingsService.GetUpdateDiff(nextActive.SettingsMutable, flags);
                await settingsService.UpdateSettings(nextActive.SettingsMutable, restartSession, persistSession, flags);
            }

            DeleteManagedProfileDirectories(targetProfile.RootPath);

            await WriteStore(store);
            return store;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<DataUpdateFlags> SwitchProfile(string profileId)
    {
        if (!sessionService.HasEmptyActionList())
        {
            throw new InvalidOperationException("Empty action list is required before switching trainer profile");
        }

        await Semaphore.WaitAsync();
        try
        {
            var store = ReadStore();
            store = EnsureDefaultProfile(store);

            var targetIndex = store.Profiles.FindIndex(p => p.Id == profileId);
            if (targetIndex < 0)
            {
                throw new KeyNotFoundException($"Profile not found: {profileId}");
            }

            var now = timeProvider.GetUtcNow().DateTime;

            var activeIndex = store.Profiles.FindIndex(p => p.Id == store.ActiveProfileId);
            if (activeIndex >= 0)
            {
                store.Profiles[activeIndex] = store.Profiles[activeIndex] with
                {
                    LastPlayedAt = now,
                };
            }

            store.Profiles[targetIndex] = store.Profiles[targetIndex] with
            {
                LastPlayedAt = now,
            };
            store = store with
            {
                ActiveProfileId = profileId,
            };

            await WriteStore(store);

            var flags = new DataUpdateFlags();
            var (restartSession, persistSession) = settingsService.GetUpdateDiff(store.Profiles[targetIndex].SettingsMutable, flags);

            await settingsService.UpdateSettings(store.Profiles[targetIndex].SettingsMutable, restartSession, persistSession, flags);

            return flags;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<TrainerProfilesDTO> UpdateProfileSaveGlobs(string profileId, string[] saveGlobs, DataUpdateFlags flags)
    {
        await Semaphore.WaitAsync();
        try
        {
            var store = ReadStore();
            store = EnsureDefaultProfile(store);

            var targetIndex = store.Profiles.FindIndex(p => p.Id == profileId);
            if (targetIndex < 0)
            {
                throw new KeyNotFoundException($"Profile not found: {profileId}");
            }

            var trimmedGlobs = saveGlobs.Select(glob => glob.Trim()).Where(glob => glob.Length > 0).ToArray();

            var target = store.Profiles[targetIndex];
            var updatedMutable = target.SettingsMutable with { SAVE_GLOBS = trimmedGlobs };
            store.Profiles[targetIndex] = target with { SettingsMutable = updatedMutable };

            var isActiveProfile = store.ActiveProfileId == profileId;

            if (isActiveProfile)
            {
                var (restartSession, persistSession) = settingsService.GetUpdateDiff(updatedMutable, flags);

                if (restartSession && !sessionService.HasEmptyActionList())
                {
                    throw new InvalidOperationException("Empty action list is required before changing save paths");
                }

                await settingsService.UpdateSettings(updatedMutable, restartSession, persistSession, flags);
            }

            await WriteStore(store);
            return store;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<TrainerProfilesDTO> SetActiveSave(string profileId, uint? saveId)
    {
        await Semaphore.WaitAsync();
        try
        {
            var store = ReadStore();
            store = EnsureDefaultProfile(store);

            var targetIndex = store.Profiles.FindIndex(p => p.Id == profileId);
            if (targetIndex < 0)
            {
                throw new KeyNotFoundException($"Profile not found: {profileId}");
            }

            store.Profiles[targetIndex] = store.Profiles[targetIndex] with
            {
                ActiveSaveId = saveId,
            };

            await WriteStore(store);
            return store;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<RegionBadgesDTO> GetRegionBadges(string profileId)
    {
        await Semaphore.WaitAsync();
        try
        {
            var store = ReadStore();
            store = EnsureDefaultProfile(store);

            var target = store.Profiles.Find(p => p.Id == profileId)
                ?? throw new KeyNotFoundException($"Profile not found: {profileId}");

            return new RegionBadgesDTO(target.RegionBadges ?? [], target.RegionBadgeSnapshots ?? []);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<RegionBadgesDTO> SetRegionBadges(string profileId, RegionBadgesDTO payload)
    {
        await Semaphore.WaitAsync();
        try
        {
            var store = ReadStore();
            store = EnsureDefaultProfile(store);

            var targetIndex = store.Profiles.FindIndex(p => p.Id == profileId);
            if (targetIndex < 0)
            {
                throw new KeyNotFoundException($"Profile not found: {profileId}");
            }

            var target = store.Profiles[targetIndex];

            // Replace exactly the regions / snapshot entries present in the
            // payload; leave everything else untouched. The client always sends
            // the authoritative post-merge state, so replace (not union) is
            // correct and lets a badge removal actually propagate.
            var ledger = target.RegionBadges is { } existingLedger
                ? existingLedger.ToDictionary(kv => kv.Key, kv => (bool[])kv.Value.Clone())
                : [];
            foreach (var (region, slots) in payload.Regions)
            {
                ledger[region] = (bool[])slots.Clone();
            }

            var snapshots = target.RegionBadgeSnapshots is { } existingSnaps
                ? existingSnaps.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.ToDictionary(r => r.Key, r => (bool[])r.Value.Clone()))
                : [];
            foreach (var (saveId, regions) in payload.Snapshots ?? [])
            {
                if (!snapshots.TryGetValue(saveId, out var saveSnap))
                {
                    saveSnap = [];
                    snapshots[saveId] = saveSnap;
                }
                foreach (var (region, slots) in regions)
                {
                    saveSnap[region] = (bool[])slots.Clone();
                }
            }

            store.Profiles[targetIndex] = target with
            {
                RegionBadges = ledger,
                RegionBadgeSnapshots = snapshots,
            };
            await WriteStore(store);

            return new RegionBadgesDTO(ledger, snapshots);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    /**
     * Records that this trainer has become champion of each region in
     * `regions` at least once. Union with whatever is already recorded —
     * conquering a region is a one-way fact about this trainer, so it is never
     * removed just because someone else later holds the title.
     */
    public async Task<TrainerProfilesDTO> AddConqueredRegions(string profileId, IEnumerable<string> regions)
    {
        var normalized = regions
            .Select(r => r?.Trim().ToLowerInvariant())
            .Where(r => !string.IsNullOrEmpty(r))
            .Select(r => r!)
            .ToList();

        await Semaphore.WaitAsync();
        try
        {
            var store = ReadStore();
            store = EnsureDefaultProfile(store);

            var targetIndex = store.Profiles.FindIndex(p => p.Id == profileId);
            if (targetIndex < 0)
            {
                throw new KeyNotFoundException($"Profile not found: {profileId}");
            }

            var target = store.Profiles[targetIndex];
            var merged = (target.ConqueredRegions ?? [])
                .Concat(normalized)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            store.Profiles[targetIndex] = target with { ConqueredRegions = merged };
            await WriteStore(store);

            return store;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    /**
     * First-ever read: no profiles registered yet. Create a "Default" profile
     * that simply points at whatever settings are currently active, so
     * existing DB/storage/saves keep working with zero migration.
     */
    private TrainerProfilesDTO EnsureDefaultProfile(TrainerProfilesDTO store)
    {
        if (store.Profiles.Count > 0)
        {
            return store;
        }

        var now = timeProvider.GetUtcNow().DateTime;
        var currentSettings = settingsService.GetSettings().SettingsMutable;

        var defaultProfile = new TrainerProfileDTO(
            Id: DefaultProfileId,
            Name: "Default",
            CreatedAt: now,
            LastPlayedAt: now,
            RootPath: ".",
            SettingsMutable: currentSettings
        );

        store.Profiles.Add(defaultProfile);

        return store with { ActiveProfileId = DefaultProfileId };
    }

    /**
     * Profiles created before the home region was recorded infer it from their
     * own saves folder: the oldest save is the one the profile was created or
     * imported with. The default profile (no managed saves folder) is left alone.
     */
    private static TrainerProfilesDTO EnsureHomeRegions(TrainerProfilesDTO store)
    {
        for (var i = 0; i < store.Profiles.Count; i++)
        {
            var profile = store.Profiles[i];
            if (profile.HomeRegion is not null || profile.RootPath == ".")
            {
                continue;
            }

            var inferred = ConnectRegions.ForGameId(profile.PendingGameVersion) ?? InferHomeRegionFromSaves(profile);
            if (inferred is not null)
            {
                store.Profiles[i] = profile with { HomeRegion = inferred };
            }
        }

        return store;
    }

    private static string? InferHomeRegionFromSaves(TrainerProfileDTO profile)
    {
        var savesDirectory = MatcherUtil.NormalizePath(Path.Combine(SettingsService.AppDirectory, profile.RootPath, "saves"));
        if (!Directory.Exists(savesDirectory))
        {
            return null;
        }

        foreach (var file in new DirectoryInfo(savesDirectory).GetFiles().OrderBy(f => f.CreationTimeUtc))
        {
            try
            {
                if (SaveUtil.TryGetSaveFile(File.ReadAllBytes(file.FullName), out var save, file.FullName)
                    && save is not null
                    && ConnectRegions.ForSave(save, file.FullName) is { } region)
                {
                    return region;
                }
            }
            catch (IOException)
            {
                // Unreadable file - try the next oldest.
            }
        }

        return null;
    }

    private static string CreateProfileId(string name, IEnumerable<string> existingIds)
    {
        var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrEmpty(slug))
        {
            slug = "profile";
        }

        var existingIdsSet = existingIds.ToHashSet();
        if (!existingIdsSet.Contains(slug))
        {
            return slug;
        }

        var suffix = 2;
        while (existingIdsSet.Contains($"{slug}-{suffix}"))
        {
            suffix++;
        }

        return $"{slug}-{suffix}";
    }

    /**
     * Copies trainer identity (OT name, TID, SID when applicable, and money)
     * from the trainer's currently active save into a freshly imported base
     * save, so the newly activated game reflects the same trainer "traveling"
     * to that region rather than showing default base-save placeholder data.
     *
     * Requires the reference save to currently be loaded in the active
     * session (only true when this profile is the app's active profile,
     * which the caller already verifies before invoking this).
     */
    private async Task TryCopyTrainerInfoFromActiveSave(uint referenceSaveId, string destinationAbsoluteSavePath)
    {
        IDictionary<uint, SaveWrapper> saveById;
        try
        {
            saveById = savesLoadersService.GetSaveById();
        }
        catch (Exception)
        {
            // Session not initialized (e.g. mid-restart) - nothing to copy from.
            return;
        }

        if (!saveById.TryGetValue(referenceSaveId, out var referenceSaveWrapper))
        {
            return;
        }

        var referenceSave = referenceSaveWrapper.GetSave();

        var destinationBytes = await fileIOService.ReadBytes(destinationAbsoluteSavePath);
        if (!SaveUtil.TryGetSaveFile(destinationBytes, out var destinationSave, destinationAbsoluteSavePath))
        {
            return;
        }

        destinationSave.OT = referenceSave.OT;
        destinationSave.TID16 = referenceSave.TID16;

        // SID only exists from Gen 3 onward - skip when either side lacks one.
        if (referenceSave.Generation > 2 && destinationSave.Generation > 2)
        {
            destinationSave.SID16 = referenceSave.SID16;
        }

        destinationSave.Money = (uint)Math.Min(referenceSave.Money, (uint)destinationSave.MaxMoney);

        await fileIOService.WriteBytes(destinationAbsoluteSavePath, destinationSave.Write().ToArray());
    }

    private TrainerProfilesDTO ReadStore()
    {
        return fileIOService.ReadJSONFileSync(
            FilePath,
            TrainerProfilesStoreJsonContext.Default.TrainerProfilesDTO,
            new TrainerProfilesDTO([])
        );
    }

    private async Task WriteStore(TrainerProfilesDTO store)
    {
        await fileIOService.WriteJSONFile(
            FilePath,
            TrainerProfilesStoreJsonContext.Default.TrainerProfilesDTO,
            store
        );
    }

    private void EnsureBaseSavesDirectoryExists()
    {
        var preferredDirectory = GetBaseSavesDirectories().First();
        fileIOService.CreateDirectory(preferredDirectory);
    }

    private string ResolveBaseSavePath(string normalizedGameVersion)
    {
        var baseSavesDirectories = GetBaseSavesDirectories();

        foreach (var directory in baseSavesDirectories)
        {
            var exactPath = MatcherUtil.NormalizePath(Path.Combine(directory, $"{normalizedGameVersion}.sav"));
            if (fileIOService.Exists(exactPath))
            {
                return exactPath;
            }

            if (!Directory.Exists(directory))
            {
                continue;
            }

            var matches = Directory
                .GetFiles(directory, $"{normalizedGameVersion}.*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path)
                .ToArray();

            if (matches.Length > 0)
            {
                return MatcherUtil.NormalizePath(matches[0]);
            }
        }

        var searchedDirectories = string.Join("', '", baseSavesDirectories);

        throw new FileNotFoundException(
            $"Base save not found for '{normalizedGameVersion}'. Place a file like '{normalizedGameVersion}.sav' in one of: '{searchedDirectories}'."
        );
    }

    private List<string> GetBaseSavesDirectories()
    {
        // The player's own folder first (a save put there replaces the bundled one), then the
        // saves that come with the program.
        return
        [
            RuntimeBaseSavesDirectoryPath,
            MatcherUtil.NormalizePath(Path.Combine(AppContext.BaseDirectory, "connect-data", "base-saves")),
        ];
    }

    private void DeleteManagedProfileDirectories(string rootPath)
    {
        foreach (var directory in GetManagedProfileDirectories(rootPath))
        {
            if (Directory.Exists(directory))
            {
                fileIOService.Delete(directory);
            }
        }
    }

    private IEnumerable<string> GetManagedProfileDirectories(string rootPath)
    {
        var normalizedRootPath = MatcherUtil.NormalizePath(rootPath).Trim();

        // Only delete trainer-owned subdirectories. Never delete app root/default profile ".".
        var isManagedRelativeProfilePath = normalizedRootPath.StartsWith("./profiles/", StringComparison.OrdinalIgnoreCase)
            || normalizedRootPath.StartsWith("profiles/", StringComparison.OrdinalIgnoreCase);

        if (!isManagedRelativeProfilePath)
        {
            return [];
        }

        var baseDirectories = new List<string> { SettingsService.AppDirectory };

        return baseDirectories
            .Select(baseDir => MatcherUtil.NormalizePath(Path.GetFullPath(Path.Combine(baseDir, normalizedRootPath))))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writes the trainer's home region and the regions they have conquered into a save of
    /// theirs (see <see cref="ConnectSaveState"/>). A save of a game without Connect state is left alone.
    /// </summary>
    private async Task WriteConnectState(string absoluteSavePath, TrainerProfileDTO profile)
    {
        var bytes = await fileIOService.ReadBytes(absoluteSavePath);
        if (!SaveUtil.TryGetSaveFile(bytes, out var save, absoluteSavePath) || save is null)
        {
            return;
        }

        if (ConnectSaveState.Apply(save, profile.HomeRegion, profile.ConqueredRegions ?? []))
        {
            await fileIOService.WriteBytes(absoluteSavePath, save.Write().ToArray());
        }
    }
}
