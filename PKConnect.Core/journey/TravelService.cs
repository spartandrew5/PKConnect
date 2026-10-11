using Microsoft.Extensions.DependencyInjection;
using PKConnect.Core.profiles;
using PKConnect.Core.regions;
using PKHeX.Core;
using PKVault.Core;
using Serilog;

namespace PKConnect.Core.journey;

/// <summary>A party member that didn't make a trip, and why.</summary>
public record StayedBehindDTO(int Species, string Nickname, int Level, string Reason);

/// <summary>How a trip went.</summary>
/// <param name="SaveId">The save the trainer is in now.</param>
/// <param name="NewSave">The trip started a save in the destination's game; otherwise the trainer came back to one.</param>
public record BoardingResultDTO(
    uint SaveId, string GameId, string Region, bool NewSave,
    List<TrainerPartyMonDTO> Travelled, List<StayedBehindDTO> Stayed);

public interface ITravelService
{
    /// <summary>
    /// Takes the active trainer and their party to another game. Refused while there are changes
    /// waiting to be saved.
    /// </summary>
    Task<BoardingResultDTO> Board(string gameId);
}

/// <summary>
/// Travelling: the trainer leaves the game they are in with their party and arrives in another.
///
/// The party is carried by main storage. Each member has a stored Pokémon there, kept in a box
/// of its own, which is linked to the copy in the save the trainer is in. On a trip:
///  1. party members not stored yet are stored, linked to the save they are in;
///  2. the links to that save are cut - what stays in its party is only a copy, replaced when
///     the trainer comes back;
///  3. the destination's party is emptied and each traveller is written into it, linked again.
///     PKVault's own move does the converting (and creates the variant of the right generation
///     when there is none), so a Pokémon keeps what the destination can't hold for when it leaves.
/// A member whose species doesn't exist in the destination's game stays in storage.
///
/// Everything goes through PKVault's actions and is saved at the end, with its backup; if a step
/// fails the session is put back as it was and nothing is written.
/// </summary>
public class TravelService(
    IServiceProvider sp,
    ITrainerProfilesService profilesService,
    ISavesLoadersService savesLoadersService,
    ISessionService sessionService,
    ActionService actionService,
    ISaveSyncService saveSyncService,
    IJournalService journalService
) : ITravelService
{
    public const string PartyBoxName = "Travelling party";
    private const int PartySize = 6;
    private static readonly string SavePartyBoxId = ((int)BoxType.Party).ToString();

    /// <summary>A stored Pokémon: every variant of it shares one box slot.</summary>
    private readonly record struct Stored(string BoxId, int Slot);

    public async Task<BoardingResultDTO> Board(string gameId)
    {
        var game = ConnectCatalog.Game(gameId) ?? throw new ArgumentException($"Unknown game: {gameId}");
        if (!game.Playable)
            throw new ArgumentException($"{game.Name} can't be travelled to yet");
        if (!sessionService.HasEmptyActionList())
            throw new InvalidOperationException("Save or undo the changes that are waiting before travelling");

        var store = await profilesService.GetProfiles();
        var profile = store.Profiles.Find(p => p.Id == store.ActiveProfileId)
            ?? throw new InvalidOperationException("No active trainer");

        // Storage as the game left it: the saves read again, and whatever the trainer's linked
        // Pokémon gained in the game brought into storage.
        await sessionService.StartNewSession(checkInitialActions: true, new DataUpdateFlags());
        if (!sessionService.HasEmptyActionList())
            await SaveAndRestart();

        var from = CurrentSave(profile) ?? throw new InvalidOperationException($"{profile.Name} has no save to travel from");
        var fromGame = ConnectCatalog.GameIdFor(from.Value.GetSave());
        if (fromGame == game.Id)
            throw new InvalidOperationException($"{profile.Name} is already in {game.Name}");
        var fromId = from.Key;

        // The save a trip starts takes the trainer's name and ID from the one they are in.
        await profilesService.SetActiveSave(profile.Id, fromId);
        var isNew = FindSave(game.Id) is null;
        if (isNew)
            await profilesService.ActivateBaseSave(profile.Id, game.Id);
        var destId = FindSave(game.Id)?.Key
            ?? throw new InvalidOperationException($"{game.Name}'s save couldn't be loaded");

        List<TrainerPartyMonDTO> travelled = [];
        List<StayedBehindDTO> stayed = [];
        try
        {
            var travellers = await StoreParty(fromId, stayed);
            await MoveIntoParty(travellers, destId, game, travelled, stayed);
            if (travelled.Count == 0)
                throw new InvalidOperationException($"None of the party can go to {game.Name}; a trainer can't arrive without a Pokémon");
            await StampLegacyOrigins(destId);
            await SaveAndRestart();
        }
        catch
        {
            // Back to what is on disk: nothing of the trip is kept.
            await actionService.RemoveDataActionsAndReset(0);
            throw;
        }

        await profilesService.SetActiveSave(profile.Id, destId);
        await saveSyncService.SyncSave(destId, afterPlay: false);

        var fromRegion = ConnectRegions.ForGameId(fromGame);
        await journalService.Add(profile.Id, JournalService.Travel, game.Region,
            fromRegion is null || fromRegion == game.Region
                ? $"Arrived in {game.Name} with {travelled.Count} Pokémon"
                : $"Arrived in {ConnectCatalog.RegionName(game.Region)} from {ConnectCatalog.RegionName(fromRegion)} with {travelled.Count} Pokémon");

        Log.Information("[travel] {Trainer}: {From} -> {To}, {Travelled} travelled, {Stayed} stayed",
            profile.Id, fromGame, game.Id, travelled.Count, stayed.Count);
        return new(destId, game.Id, game.Region, isNew, travelled, stayed);
    }

    private async Task SaveAndRestart()
    {
        await actionService.Save();
        await sessionService.StartNewSession(checkInitialActions: false, new DataUpdateFlags());
    }

    private KeyValuePair<uint, SaveWrapper>? FindSave(string gameId)
    {
        var found = savesLoadersService.GetSaveById().FirstOrDefault(entry => ConnectCatalog.GameIdFor(entry.Value.GetSave()) == gameId);
        return found.Value is null ? null : found;
    }

    // The save the trainer marked as theirs, else the one played last.
    private KeyValuePair<uint, SaveWrapper>? CurrentSave(TrainerProfileDTO profile)
    {
        var saves = savesLoadersService.GetSaveById();
        if (profile.ActiveSaveId is { } active && saves.TryGetValue(active, out var marked))
            return new(active, marked);
        var latest = saves
            .Where(entry => entry.Value.Metadata.FilePath is { } path && File.Exists(path))
            .OrderByDescending(entry => File.GetLastWriteTimeUtc(entry.Value.Metadata.FilePath!))
            .FirstOrDefault();
        return latest.Value is null ? null : latest;
    }

    /// <summary>
    /// Makes sure every party member of the save being left is in main storage, and cuts their
    /// links to that save. Returns them in party order.
    /// </summary>
    private async Task<List<Stored>> StoreParty(uint fromId, List<StayedBehindDTO> stayed)
    {
        var box = await GetOrCreatePartyBox();
        List<Stored> travellers = [];

        var party = savesLoadersService.GetLoadersRequired(fromId).Pkms.GetAllDtos()
            .Where(dto => dto.BoxId == (int)BoxType.Party)
            .OrderBy(dto => dto.BoxSlot)
            .ToList();

        foreach (var member in party)
        {
            using var scope = sp.CreateScope();
            var variants = scope.ServiceProvider.GetRequiredService<IPkmVariantLoader>();

            // Stored already: linked to this save on an earlier trip, or put in storage by hand.
            var known = await variants.GetEntityBySave(fromId, member.IdBase) ?? await variants.GetEntity(member.IdBase);
            if (known is not null)
            {
                travellers.Add(new(known.BoxId, known.BoxSlot));
                continue;
            }

            if (!member.CanMoveAttachedToMain)
            {
                stayed.Add(new(member.Species, member.Nickname, member.Level, member.IsEgg ? "Eggs can't travel" : "It can't be stored"));
                continue;
            }

            var slot = await FreeSlot(variants, box);
            await actionService.MovePkm(new([member.Id], fromId, null, box.Id, [slot], attached: true));
            travellers.Add(new(box.Id, slot));
        }

        using (var scope = sp.CreateScope())
        {
            var variants = scope.ServiceProvider.GetRequiredService<IPkmVariantLoader>();
            var linked = (await variants.GetEntitiesBySave(fromId)).Values
                .Where(variant => travellers.Contains(new(variant.BoxId, variant.BoxSlot)))
                .Select(variant => variant.Id)
                .Distinct()
                .ToArray();
            if (linked.Length > 0)
                await actionService.MainPkmDetachSaves(new(linked));
        }

        return travellers;
    }

    private async Task MoveIntoParty(
        List<Stored> travellers, uint destId, GameDTO game, List<TrainerPartyMonDTO> travelled, List<StayedBehindDTO> stayed)
    {
        // Who can go is settled before the destination's party is touched.
        List<(Stored Stored, PkmVariantEntity Main)> going = [];
        using (var scope = sp.CreateScope())
        {
            var variants = scope.ServiceProvider.GetRequiredService<IPkmVariantLoader>();
            var dest = savesLoadersService.GetLoadersRequired(destId).Save;
            foreach (var stored in travellers.Distinct())
            {
                var group = (await variants.GetEntitiesByBox(stored.BoxId, stored.Slot)).Values.ToList();
                var main = group.FirstOrDefault(variant => variant.IsMain) ?? group.FirstOrDefault();
                if (main is null)
                    continue;

                var pkm = await variants.GetPKM(main);
                if (going.Count >= PartySize)
                    stayed.Add(new(main.Species, pkm.Nickname, pkm.CurrentLevel, "The party is full"));
                else if (!dest.IsPresentInGame(main.Species, main.Form))
                    stayed.Add(new(main.Species, pkm.Nickname, pkm.CurrentLevel, $"It hasn't been discovered in {ConnectCatalog.RegionName(game.Region)}"));
                else
                    going.Add((stored, main));
            }
        }
        if (going.Count == 0)
            return;

        // What the destination's party holds is what the trainer left there last time: copies of
        // Pokémon that have been in storage since.
        var oldParty = savesLoadersService.GetLoadersRequired(destId).Pkms.GetAllDtos()
            .Where(dto => dto.BoxId == (int)BoxType.Party)
            // Last slot first: the party closes up after each one, and a Pokémon's id says its slot.
            .OrderByDescending(dto => dto.BoxSlot)
            .Select(dto => dto.Id)
            .ToArray();
        if (oldParty.Length > 0)
            await actionService.SaveDeletePkms(new(destId, oldParty));

        var slot = 0;
        foreach (var (stored, main) in going)
        {
            string nickname;
            int level;
            using (var scope = sp.CreateScope())
            {
                var variants = scope.ServiceProvider.GetRequiredService<IPkmVariantLoader>();
                var group = (await variants.GetEntitiesByBox(stored.BoxId, stored.Slot)).Values.ToList();
                var pkm = await variants.GetPKM(main);
                (nickname, level) = (pkm.Nickname, pkm.CurrentLevel);

                // A copy of it left in the destination's boxes would be taken for the same Pokémon.
                var copies = group
                    .SelectMany(variant => savesLoadersService.GetLoadersRequired(destId).Pkms.GetDtosByIdBase(variant.Id).Values)
                    .Select(dto => dto.Id)
                    .Distinct()
                    .ToArray();
                if (copies.Length > 0)
                    await actionService.SaveDeletePkms(new(destId, copies));

                // A link left from a trip that wasn't made through here.
                var linked = group.Where(variant => variant.AttachedSaveId is not null).Select(variant => variant.Id).ToArray();
                if (linked.Length > 0)
                    await actionService.MainPkmDetachSaves(new(linked));
            }

            try
            {
                await actionService.MovePkm(new([main.Id], null, destId, SavePartyBoxId, [slot], attached: true));
            }
            catch (Exception ex)
            {
                // The failed move was undone by PKVault; the ones before it stand.
                Log.Warning(ex, "[travel] {Species} couldn't be moved into save {Save}", main.Species, destId);
                stayed.Add(new(main.Species, nickname, level, "It couldn't be converted for this game"));
                continue;
            }

            travelled.Add(new(main.Species, nickname, level, main.IsShiny, false));
            slot++;
        }
    }

    /// <summary>
    /// The Game Boy games have no secret ID, so a Pokémon from one arrives in a later game looking
    /// like a traded one - and a traded Pokémon stops obeying a trainer without badges. They are
    /// the trainer's own: they take the destination save's trainer.
    /// </summary>
    private async Task StampLegacyOrigins(uint destId)
    {
        var loaders = savesLoadersService.GetLoadersRequired(destId);
        var save = loaders.Save.GetSave();
        if (save.Generation < 3)
            return;

        using var scope = sp.CreateScope();
        var variants = scope.ServiceProvider.GetRequiredService<IPkmVariantLoader>();
        var changed = false;

        foreach (var member in loaders.Pkms.GetAllDtos().Where(dto => dto.BoxId == (int)BoxType.Party).ToList())
        {
            if (await variants.GetEntityBySave(destId, member.IdBase) is not { } link)
                continue;
            var group = (await variants.GetEntitiesByBox(link.BoxId, link.BoxSlot)).Values;
            if (!group.Any(variant => variant.Generation < 3))
                continue;

            var mutable = member.Pkm.GetMutablePkm().Clone();
            mutable.OriginalTrainerName = save.OT;
            mutable.OriginalTrainerGender = save.Gender;
            mutable.TID16 = save.TID16;
            mutable.SID16 = save.SID16;
            mutable.CurrentHandler = 0;
            mutable.RefreshChecksum();

            var stamped = loaders.Pkms.CreateDTO(loaders.Save, new ImmutablePKM(mutable), member.BoxId, member.BoxSlot);
            loaders.Pkms.DeleteDto(member.Id);
            loaders.Pkms.WriteDto(stamped);

            link.AttachedSavePkmIdBase = stamped.IdBase;
            await variants.UpdateEntity(link);
            changed = true;
        }

        if (changed)
        {
            loaders.Pkms.FlushParty();
            await scope.ServiceProvider.GetRequiredService<SessionDbContext>().SaveChangesAsync();
        }
    }

    private async Task<BoxDTO> GetOrCreatePartyBox()
    {
        async Task<BoxDTO?> Find()
        {
            using var scope = sp.CreateScope();
            var boxes = await scope.ServiceProvider.GetRequiredService<IBoxLoader>().GetAllDtos();
            return boxes.Find(box => box.Type == BoxType.Box && box.Name == PartyBoxName);
        }

        if (await Find() is { } existing)
            return existing;

        string bankId;
        using (var scope = sp.CreateScope())
        {
            var banks = await scope.ServiceProvider.GetRequiredService<IBankLoader>().GetAllDtos();
            bankId = banks.OrderBy(bank => bank.Order).First().Id;
        }
        await actionService.MainCreateBox(new(bankId, 30, BoxType.Box, PartyBoxName));
        return await Find() ?? throw new InvalidOperationException("The travelling party's box couldn't be created");
    }

    private static async Task<int> FreeSlot(IPkmVariantLoader variants, BoxDTO box)
    {
        var used = (await variants.GetEntitiesByBox(box.Id)).Where(entry => entry.Value.Count > 0).Select(entry => entry.Key).ToHashSet();
        for (var slot = 0; slot < box.SlotCount; slot++)
            if (!used.Contains(slot))
                return slot;
        throw new InvalidOperationException($"The \"{PartyBoxName}\" box is full; move some Pokémon out of it");
    }
}