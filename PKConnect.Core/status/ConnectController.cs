using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using PKConnect.Core.regions;
using PKVault.Core;

namespace PKConnect.Core.status;

[Route("api/[controller]")]
public class ConnectController(IServiceProvider services)
{
    /// <summary>
    /// Each species' picture on PKVault's sprite sheets, by National Dex number (index 0 is unused).
    /// </summary>
    [HttpGet("species-sprites")]
    public async Task<List<SpeciesSpriteDTO?>> GetSpeciesSprites()
    {
        // Asked for here, not in the constructor: the status route must answer without PKVault's data services.
        var sheets = (await services.GetRequiredService<StaticDataService>().GetStaticSpritesheets()).Species;
        List<SpeciesSpriteDTO?> sprites = [null];
        for (var species = 1; sheets.TryGetValue($"sprites/pokemon/{species}.png", out var sprite); species++)
            sprites.Add(new(sprite.SheetName, sprite.X, sprite.Y, sprite.Width, sprite.Height));
        return sprites;
    }

    [HttpGet()]
    public ConnectStatusDTO GetStatus()
    {
        var version = typeof(ConnectController).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        return new(Version: version, PKVaultVersion: SettingsService.GetBuildInfo().Version);
    }

    /// <summary>The image a sprite set is shown by (see the catalog's characters), as a PNG.</summary>
    /// <param name="set">The set's id, e.g. "brendan/gen3/front/emerald".</param>
    [HttpGet("sprite")]
    public CoreFileResponse GetSpritePreview(string set)
    {
        var path = ConnectCharacters.PreviewPath(set)
            ?? throw new KeyNotFoundException($"No such sprite set: {set}");
        return new(
            StatusCode: 200,
            ContentType: "image/png",
            File: new(new MemoryStream(File.ReadAllBytes(path)), "image/png", "sprite.png"),
            Header: new() { ["Cache-Control"] = "max-age=3600" });
    }

    /// <summary>Species and move names by id, for showing what the other routes give as numbers.</summary>
    [HttpGet("names")]
    public NamesDTO GetNames()
    {
        return ConnectCatalog.Names();
    }

    /// <summary>The regions and games Connect knows: names, badges, and what can live in each game.</summary>
    [HttpGet("catalog")]
    public CatalogDTO GetCatalog()
    {
        return ConnectCatalog.Get();
    }
}