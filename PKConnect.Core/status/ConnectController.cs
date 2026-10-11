using System.Reflection;
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
}