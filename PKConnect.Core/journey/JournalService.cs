using System.Text.Json.Serialization;
using PKVault.Core;

namespace PKConnect.Core.journey;

/// <summary>
/// Something that happened on a trainer's journey.
/// </summary>
/// <param name="Type">"travel", "badge" or "title".</param>
/// <param name="Region">The region it happened in.</param>
public record JournalEntryDTO(DateTime At, string Type, string? Region, string Text);

[JsonSerializable(typeof(Dictionary<string, List<JournalEntryDTO>>))]
public partial class JournalStoreJsonContext : JsonSerializerContext
{
}

public interface IJournalService
{
    /// <summary>A trainer's journal, newest entry first.</summary>
    Task<List<JournalEntryDTO>> Get(string profileId);

    Task Add(string profileId, string type, string? region, string text);
}

/// <summary>
/// Each trainer's journal: where they went, the badges they earned and the titles they took.
/// Written by the backend as those things happen; nothing is reconstructed from saves.
/// </summary>
public class JournalService(IFileIOService fileIOService, TimeProvider timeProvider) : IJournalService
{
    public const string Travel = "travel";
    public const string Badge = "badge";
    public const string Title = "title";

    private const string StorePath = "./config/journal.json";
    private static readonly SemaphoreSlim Semaphore = new(1);

    private Dictionary<string, List<JournalEntryDTO>> Read() => fileIOService.ReadJSONFileSync(
        StorePath, JournalStoreJsonContext.Default.DictionaryStringListJournalEntryDTO, []);

    public async Task<List<JournalEntryDTO>> Get(string profileId)
    {
        await Semaphore.WaitAsync();
        try
        {
            return Read().TryGetValue(profileId, out var entries)
                ? [.. entries.OrderByDescending(entry => entry.At)]
                : [];
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task Add(string profileId, string type, string? region, string text)
    {
        await Semaphore.WaitAsync();
        try
        {
            var store = Read();
            if (!store.TryGetValue(profileId, out var entries))
                store[profileId] = entries = [];
            entries.Add(new(timeProvider.GetUtcNow().UtcDateTime, type, region, text));
            await fileIOService.WriteJSONFile(StorePath, JournalStoreJsonContext.Default.DictionaryStringListJournalEntryDTO, store);
        }
        finally
        {
            Semaphore.Release();
        }
    }
}