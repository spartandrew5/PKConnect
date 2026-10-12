using System.Diagnostics;
using System.Text.Json.Serialization;
using PKConnect.Core.champions;
using PKConnect.Core.graphics;
using PKConnect.Core.journey;
using PKConnect.Core.launch;
using PKConnect.Core.profiles;
using PKConnect.Core.regions;
using PKVault.Core;
using Serilog;

namespace PKConnect.Core.play;

/// <summary>An emulator on this machine.</summary>
/// <param name="Path">Its executable.</param>
/// <param name="CorePath">For a frontend like RetroArch: the core to load the game with.</param>
public record EmulatorDTO(string Path, string? CorePath = null);

/// <summary>
/// Where the games and the emulators are on this machine.
/// </summary>
/// <param name="Emulators">By platform: "gba" plays the Game Boy and GBA games, "nds" the DS ones.</param>
/// <param name="RomPaths">The unmodified ROM of each game, by game id.</param>
public record PlaySettingsDTO(Dictionary<string, EmulatorDTO> Emulators, Dictionary<string, string> RomPaths);

/// <summary>
/// The game being played, or how the last one ended.
/// </summary>
/// <param name="State">"idle", "running", "finished" or "failed".</param>
/// <param name="Message">What happened, for the player.</param>
/// <param name="CrownedRegion">The region the trainer became champion of while playing.</param>
/// <param name="NewSave">A new game's first save was taken in as the trainer's.</param>
public record PlayStatusDTO(
    string State, string? TrainerId = null, string? GameId = null, DateTime? StartedAt = null,
    string? Message = null, string? CrownedRegion = null, bool NewSave = false);

[JsonSerializable(typeof(PlaySettingsDTO))]
public partial class PlayStoreJsonContext : JsonSerializerContext
{
}

public interface IPlayService
{
    PlaySettingsDTO GetSettings();
    Task<PlaySettingsDTO> SetSettings(PlaySettingsDTO settings);
    PlayStatusDTO GetStatus();

    /// <summary>
    /// Starts the active trainer's game and returns at once; <see cref="GetStatus"/> says when
    /// the player is back. Refused while a game is running or changes are waiting to be saved.
    /// </summary>
    Task<PlayStatusDTO> Start();
}

/// <summary>
/// Playing: the trainer's game is started in its emulator with this launch's patch, on a copy
/// of their save put where the emulator looks for it (beside the ROM, under the ROM's name).
/// When the emulator closes, the save comes back, storage takes in what changed, and the save is
/// brought into step with the trainer - which is when a League win makes them champion.
///
/// A trainer about to start has no save: the game starts fresh, and the first save made in it
/// becomes the trainer's, name and all.
/// </summary>
public class PlayService(
    IFileIOService fileIOService,
    ITrainerProfilesService profilesService,
    IJourneyService journeyService,
    IChampionsService championsService,
    ISavesLoadersService savesLoadersService,
    ISessionService sessionService,
    ActionService actionService,
    ISaveSyncService saveSyncService
) : IPlayService
{
    public const string PlaceholderName = "New trainer";

    private const string StorePath = "./config/connect-play.json";
    private readonly Lock gate = new();
    private PlayStatusDTO status = new("idle");

    public PlaySettingsDTO GetSettings()
    {
        var stored = fileIOService.ReadJSONFileSync(StorePath, PlayStoreJsonContext.Default.PlaySettingsDTO, new PlaySettingsDTO([], []));
        return new(stored.Emulators ?? [], stored.RomPaths ?? []);
    }

    public async Task<PlaySettingsDTO> SetSettings(PlaySettingsDTO settings)
    {
        // Empty entries are how a path is cleared.
        var cleaned = new PlaySettingsDTO(
            (settings.Emulators ?? [])
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Value?.Path))
                .ToDictionary(
                    entry => entry.Key.Trim().ToLowerInvariant(),
                    entry => new EmulatorDTO(entry.Value.Path.Trim(), string.IsNullOrWhiteSpace(entry.Value.CorePath) ? null : entry.Value.CorePath.Trim())),
            (settings.RomPaths ?? [])
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
                .ToDictionary(entry => entry.Key.Trim().ToLowerInvariant(), entry => entry.Value.Trim()));
        await fileIOService.WriteJSONFile(StorePath, PlayStoreJsonContext.Default.PlaySettingsDTO, cleaned);
        return cleaned;
    }

    public PlayStatusDTO GetStatus()
    {
        lock (gate)
            return status;
    }

    /// <summary>Which emulator plays a game.</summary>
    public static string PlatformOf(GameDTO game) => game.Generation >= 4 ? "nds" : "gba";

    public async Task<PlayStatusDTO> Start()
    {
        lock (gate)
        {
            if (status.State == "running")
                throw new InvalidOperationException("A game is already running");
        }
        if (!sessionService.HasEmptyActionList())
            throw new InvalidOperationException("Save or undo the changes that are waiting before playing");

        var journey = await journeyService.GetTrainers();
        var trainer = journey.Trainers.Find(t => t.IsActive) ?? throw new InvalidOperationException("No active trainer");
        var game = ConnectCatalog.Game(trainer.CurrentGameId)
            ?? throw new InvalidOperationException($"{trainer.TrainerName} isn't in a game Connect can start");

        var settings = GetSettings();
        var platform = PlatformOf(game);
        if (!settings.Emulators.TryGetValue(platform, out var emulator))
            throw new InvalidOperationException($"No emulator is set for {game.Name}. Set one in Settings.");
        if (!File.Exists(emulator.Path))
            throw new InvalidOperationException($"The emulator isn't there: {emulator.Path}");
        if (emulator.CorePath is not null && !File.Exists(emulator.CorePath))
            throw new InvalidOperationException($"The emulator's core isn't there: {emulator.CorePath}");
        if (!settings.RomPaths.TryGetValue(game.Id, out var romPath))
            throw new InvalidOperationException($"No ROM is set for {game.Name}. Set its path in Settings.");
        if (!File.Exists(romPath))
            throw new InvalidOperationException($"{game.Name}'s ROM isn't there: {romPath}");
        romPath = Path.GetFullPath(romPath);

        var savePath = trainer.CurrentSaveId is { } saveId
            ? savesLoadersService.GetLoadersRequired(saveId).Save.Metadata.FilePath
            : null;

        // The patch for this launch: who the champion is now, and for a visitor, teams at the
        // party's level.
        var champions = await championsService.GetChampions();
        var champion = champions.Regions.TryGetValue(game.Region, out var current) ? current : null;
        double? averageLevel = trainer.Party.Count > 0 ? trainer.Party.Average(mon => mon.Level) : null;
        // How the trainer looks: their character, in the sets picked for each generation.
        var sprites = trainer.Appearance?.Sprites?.ToDictionary(
            entry => entry.Key,
            entry => new GenSpriteSelection(entry.Value.Front, null, entry.Value.Overworld));
        LaunchPatchService.WriteFor(game.Id, romPath, averageLevel, trainer.HomeRegion, champion, trainer.Appearance?.Character, sprites);

        // The emulator keeps the save beside the ROM, under the ROM's name.
        var runtimeSave = Path.ChangeExtension(romPath, ".sav");
        var stash = runtimeSave + ".connect-stash";
        if (savePath is not null)
        {
            File.Copy(savePath, runtimeSave, overwrite: true);
        }
        else if (File.Exists(runtimeSave))
        {
            // A new game must find no save; whoever's this is gets it back afterwards.
            File.Move(runtimeSave, stash, overwrite: true);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = emulator.Path,
            WorkingDirectory = Path.GetDirectoryName(emulator.Path) ?? Environment.CurrentDirectory,
            UseShellExecute = true,
        };
        if (emulator.CorePath is not null)
        {
            startInfo.ArgumentList.Add("-L");
            startInfo.ArgumentList.Add(emulator.CorePath);
        }
        startInfo.ArgumentList.Add(romPath);

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The emulator couldn't be started");
        Log.Information("[play] {Trainer}: {Game} in {Emulator} (save={Save})", trainer.Id, game.Id, emulator.Path, savePath ?? "new game");

        var started = new PlayStatusDTO("running", trainer.Id, game.Id, DateTime.UtcNow, $"Playing {game.Name}");
        lock (gate)
            status = started;

        _ = Task.Run(async () =>
        {
            PlayStatusDTO ended;
            try
            {
                WaitForTheGame(process);
                ended = savePath is not null
                    ? await ComeBack(started, trainer, game, savePath, runtimeSave)
                    : await TakeInFirstSave(started, trainer, game, runtimeSave, stash);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[play] coming back from {Game} failed", game.Id);
                ended = started with { State = "failed", Message = ex.Message };
            }
            lock (gate)
                status = ended;
        });

        return started;
    }

    // Some emulators hand the game to a window that is already open: the process started here
    // ends at once while the game goes on. Then it is the others of the same name that are waited for.
    private static void WaitForTheGame(Process process)
    {
        var name = process.ProcessName;
        var id = process.Id;
        var watch = Stopwatch.StartNew();
        process.WaitForExit();
        if (watch.Elapsed.TotalSeconds >= 5)
            return;

        foreach (var sibling in Process.GetProcessesByName(name).Where(p => p.Id != id))
        {
            try
            {
                sibling.WaitForExit();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[play] couldn't wait for {Name} {Id}", name, sibling.Id);
            }
            finally
            {
                sibling.Dispose();
            }
        }
    }

    private async Task<PlayStatusDTO> ComeBack(PlayStatusDTO started, TrainerSummaryDTO trainer, GameDTO game, string savePath, string runtimeSave)
    {
        if (File.Exists(runtimeSave))
            File.Copy(runtimeSave, savePath, overwrite: true);

        var result = await SettleIn(trainer.CurrentSaveId!.Value);
        return started with
        {
            State = "finished",
            CrownedRegion = result.CrownedRegion,
            Message = result.CrownedRegion is { } region
                ? $"{trainer.TrainerName} is the new {ConnectCatalog.RegionName(region)} Champion"
                : $"Back from {game.Name}",
        };
    }

    private async Task<PlayStatusDTO> TakeInFirstSave(PlayStatusDTO started, TrainerSummaryDTO trainer, GameDTO game, string runtimeSave, string stash)
    {
        try
        {
            if (!File.Exists(runtimeSave))
                return started with { State = "finished", Message = $"No save was made in {game.Name}. Save in the game before closing it." };

            // Named for its game: that is how a save of a paired version or an expansion build is told apart.
            await profilesService.ImportProfileSave(trainer.Id, $"{game.Id}.sav", await File.ReadAllBytesAsync(runtimeSave));
        }
        finally
        {
            // The ROM's folder goes back to how it was found.
            if (File.Exists(runtimeSave))
                File.Delete(runtimeSave);
            if (File.Exists(stash))
                File.Move(stash, runtimeSave, overwrite: true);
        }

        var summary = (await journeyService.GetTrainers()).Trainers.Find(t => t.Id == trainer.Id);
        if (summary?.CurrentSaveId is not { } saveId)
            return started with { State = "failed", Message = $"The save made in {game.Name} couldn't be read" };

        await profilesService.SetActiveSave(trainer.Id, saveId);
        // Until now the trainer had no name of their own.
        if (trainer.Name.StartsWith(PlaceholderName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(summary.TrainerName))
            await profilesService.RenameProfile(trainer.Id, summary.TrainerName);

        var result = await SettleIn(saveId);
        return started with
        {
            State = "finished",
            NewSave = true,
            CrownedRegion = result.CrownedRegion,
            Message = $"{summary.TrainerName}'s journey has begun",
        };
    }

    // Storage takes in what the linked Pokémon gained in the game, then the save and its trainer
    // are reconciled.
    private async Task<SaveSyncResultDTO> SettleIn(uint saveId)
    {
        await sessionService.StartNewSession(checkInitialActions: true, new DataUpdateFlags());
        if (!sessionService.HasEmptyActionList())
        {
            await actionService.Save();
            await sessionService.StartNewSession(checkInitialActions: false, new DataUpdateFlags());
        }
        return await saveSyncService.SyncSave(saveId, afterPlay: true);
    }
}