using Microsoft.Extensions.DependencyInjection;
using PKConnect.Core.champions;
using PKConnect.Core.launch;
using PKConnect.Core.status;
using PKVault.Core;

namespace PKConnect.Core;

/// <summary>
/// Pokémon Connect, as an addition to PKVault's core: its own services and routes, registered
/// by the hosts that want them (the desktop app and the web host; not the mobile app).
/// </summary>
public static class ConnectModule
{
    /// <summary>
    /// Adds Connect's services and routes. To call before PKVault's own services are set up,
    /// since the router reads its routes when it is created.
    /// </summary>
    public static void Register(IServiceCollection services)
    {
        RouteJsonContext.AddResolver(ConnectJsonContext.Default);

        services.AddSingleton<IChampionsService, ChampionsService>();

        CoreRouter.AddControllers(typeof(ConnectController), typeof(PatchController), typeof(ChampionsController));
        services.AddScoped<ConnectController>();
        services.AddScoped<PatchController>();
        services.AddScoped<ChampionsController>();
    }
}