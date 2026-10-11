using System.Reflection;
using PKConnect.Core.regions;
using PKVault.Core;

namespace PKConnect.Core.status;

[Route("api/[controller]")]
public class ConnectController
{
    [HttpGet()]
    public ConnectStatusDTO GetStatus()
    {
        var version = typeof(ConnectController).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        return new(Version: version, PKVaultVersion: SettingsService.GetBuildInfo().Version);
    }

    /// <summary>The regions and games Connect knows: names, badges, and what can live in each game.</summary>
    [HttpGet("catalog")]
    public CatalogDTO GetCatalog()
    {
        return ConnectCatalog.Get();
    }
}