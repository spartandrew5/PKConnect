using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PKConnect.Core.status;
using PKVault.Core;

namespace PKConnect.Core.Tests;

public class ConnectModuleTests
{
    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        ConnectModule.Register(services);
        services.AddSingleton<CoreRouter>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task StatusRoute_IsServedThroughPKVaultsRouter()
    {
        using var services = Services();
        using var scope = services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<CoreRouter>();

        var response = await router.Dispatch(Task.CompletedTask, scope.ServiceProvider, "GET", "/api/connect", "", Stream.Null);

        Assert.Equal(200, response.StatusCode);
        var status = Assert.IsType<ConnectStatusDTO>(Assert.IsType<CoreJSONResponse>(response).Data);
        Assert.False(string.IsNullOrWhiteSpace(status.PKVaultVersion));
    }

    [Fact]
    public void ConnectTypes_SerializeWithTheRoutesOptions()
    {
        using var services = Services();

        var json = JsonSerializer.Serialize(new ConnectStatusDTO("1.2.3", "2.4.2"), RouteJsonContext.Options.GetTypeInfo(typeof(ConnectStatusDTO)));

        Assert.Equal("""{"version":"1.2.3","pkVaultVersion":"2.4.2"}""", json);
    }

    [Fact]
    public void PKVaultsOwnRoutes_AreStillThere()
    {
        using var services = Services();

        var routes = services.GetRequiredService<CoreRouter>().Routes;

        Assert.Contains(routes, r => r.HttpTemplate == "api/connect/");
        Assert.Contains(routes, r => r.ControllerName == "Settings");
    }
}