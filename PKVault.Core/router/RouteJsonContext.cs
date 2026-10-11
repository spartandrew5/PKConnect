
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Primitives;
using PKHeX.Core;

namespace PKVault.Core;

[JsonSerializable(typeof(SettingsDTO))]
[JsonSerializable(typeof(DirectoryContent))]
[JsonSerializable(typeof(WarningsDTO))]
[JsonSerializable(typeof(List<BankDTO>))]
[JsonSerializable(typeof(List<BoxDTO>))]
[JsonSerializable(typeof(List<PkmVariantDTO>))]
[JsonSerializable(typeof(List<PkmSaveDTO>))]
[JsonSerializable(typeof(Dictionary<string, InventoryItemDTO>))]
[JsonSerializable(typeof(List<MoveItem>))]
[JsonSerializable(typeof(List<BackupDTO>))]
[JsonSerializable(typeof(DexMoveDTO))]
[JsonSerializable(typeof(DexLocationDTO))]
[JsonSerializable(typeof(StaticEvolvesRichData))]
[JsonSerializable(typeof(DataDTO))]
[JsonSerializable(typeof(StaticDataDTO))]
[JsonSerializable(typeof(Dictionary<uint, SaveInfosDTO>))]
[JsonSerializable(typeof(Dictionary<string, PkmLegalityDTO>))]
[JsonSerializable(typeof(BankEntity.BankView))]
[JsonSerializable(typeof(BankEntity.BankViewSave))]

// [JsonSerializable(typeof(EditPkmVariantPayload))]
[JsonSerializable(typeof(DeletePkmVariantActionInput))]
[JsonSerializable(typeof(DetachPkmSaveActionInput))]
[JsonSerializable(typeof(DexSyncActionInput))]
[JsonSerializable(typeof(EditPkmSaveActionInput))]
[JsonSerializable(typeof(EditPkmVariantActionInput))]
[JsonSerializable(typeof(EvolvePkmActionInput))]
[JsonSerializable(typeof(MainCreateBankActionInput))]
[JsonSerializable(typeof(MainCreateBoxActionInput))]
// [JsonSerializable(typeof(MainCreatePkmVariantActionInput))]
[JsonSerializable(typeof(MainDeleteBankActionInput))]
[JsonSerializable(typeof(MainDeleteBoxActionInput))]
[JsonSerializable(typeof(MainUpdateBankActionInput))]
[JsonSerializable(typeof(MainUpdateBoxActionInput))]
[JsonSerializable(typeof(MoveItemActionInput))]
[JsonSerializable(typeof(MoveItemBankActionInput))]
[JsonSerializable(typeof(MovePkmActionInput))]
[JsonSerializable(typeof(MovePkmBankActionInput))]
[JsonSerializable(typeof(SaveDeletePkmActionInput))]
[JsonSerializable(typeof(SetPkmVariantMainActionInput))]
[JsonSerializable(typeof(SortPkmActionInput))]

[JsonSerializable(typeof(DesktopMessageRequest))]
[JsonSerializable(typeof(DesktopMessageResponse))]

[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Guid?))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, StringValues>))]
[JsonSerializable(typeof(Dictionary<string, Tuple<string, string>>))]
public partial class RouteJsonContext : JsonSerializerContext
{
    public static readonly RouteJsonContext DefaultWithOptions = new(
        new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = RouteJsonContext.Default
        }
    );

    private static readonly List<IJsonTypeInfoResolver> ExtraResolvers = [];
    private static JsonSerializerOptions? options;

    /// <summary>
    /// Makes the types of another assembly's own context serializable by the routes. To call
    /// before <see cref="Options"/> is first used.
    /// </summary>
    public static void AddResolver(IJsonTypeInfoResolver resolver)
    {
        if (ExtraResolvers.Contains(resolver))
            return;
        if (options is not null)
            throw new InvalidOperationException("Json resolvers must be added before the first request is served");
        ExtraResolvers.Add(resolver);
    }

    /// <summary>What the routes read and write JSON with: this context's types, then any added by <see cref="AddResolver"/>.</summary>
    public static JsonSerializerOptions Options => options ??= new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = JsonTypeInfoResolver.Combine([RouteJsonContext.Default, .. ExtraResolvers]),
    };
}
