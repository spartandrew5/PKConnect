using System.Security.Cryptography;
using PKConnect.Core.launch;
using PKConnect.Core.patching;

namespace PKConnect.Core.Tests.launch;

public class BasePatchServiceTests
{
    public static IEnumerable<object[]> Games() => BasePatchService.SupportedGames.Select(g => new object[] { g });

    public static IEnumerable<object[]> VanillaRoms() =>
    [
        ["emerald", TestRoms.Emerald],
        ["firered", TestRoms.FireRed],
        ["leafgreen", TestRoms.LeafGreen],
        ["crystal", TestRoms.Crystal],
        ["yellow", TestRoms.Yellow],
    ];

    [Theory]
    [MemberData(nameof(Games))]
    public void Manifest_LoadsWithGymAndChampionZonesInsideTheRom(string gameId)
    {
        var patch = BasePatchService.Load(gameId);
        var manifest = patch.Manifest;

        Assert.NotEmpty(patch.Edits);
        Assert.NotEmpty(manifest.Gyms);
        Assert.NotEmpty(manifest.ChampionSlots);
        Assert.All(patch.Edits, e => Assert.InRange(e.Offset + e.Data.Length, 1, manifest.RomSize));
    }

    [Theory]
    [MemberData(nameof(VanillaRoms))]
    public void ApplyToVanilla_ReproducesTheBuild_AndGymTeamsStayInsideLandingZones(string gameId, string romPath)
    {
        var vanilla = TestRoms.Read(romPath);
        var patch = BasePatchService.Load(gameId);

        // A fixture of another revision (e.g. Crystal 1.1 when the patch targets 1.0) can't
        // validate the patch; it must be rejected, not patched.
        var fixtureSha1 = Convert.ToHexStringLower(SHA1.HashData(vanilla));
        if (patch.Manifest.VanillaSha1 is { } expectedVanilla
            && !fixtureSha1.Equals(expectedVanilla, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Throws<InvalidOperationException>(() => BasePatchService.ApplyToVanilla(patch, vanilla));
            Assert.Skip($"{romPath} is sha1 {fixtureSha1}; the {gameId} base patch targets {expectedVanilla}");
        }

        BasePatchService.ApplyToVanilla(patch, vanilla); // throws unless the result matches the manifest's sha1

        var (gymEdits, gymResults) = ConnectTeamPatchService.BuildGymEdits(patch, averageLevel: 30, randomSeed: 42);

        Assert.Contains(gymResults, r => r.Applied);
        foreach (var (offset, data) in gymEdits)
        {
            var insideAZone = patch.Manifest.Gyms.Any(z =>
                offset >= z.PartyOffset
                    && offset + data.Length <= z.PartyOffset + z.Capacity * z.RecordSize + (z.Terminator.HasValue ? 1 : 0)
                || z.PartySizeOffset == offset && data.Length == 1);
            Assert.True(insideAZone, $"{gameId}: edit at 0x{offset:X} (len {data.Length}) is outside every gym landing zone");
        }
    }

    [Fact]
    public void BuildIps_SplitsLongRunsAndNeverStartsARecordAtTheEofMarkerOffset()
    {
        // 0x454F46 is "EOF" read as an offset: a record starting there would end the patch early.
        const int eof = 0x454F46;
        var rom = new byte[eof + 0x20000];
        var edits = new List<(int Offset, byte[] Data)>
        {
            (0x100, Enumerable.Repeat((byte)0xAB, 0x12000).ToArray()),
            (eof, [1, 2, 3, 4]),
        };
        IpsPatchService.ApplyInPlace(rom, edits);

        var ips = IpsPatchService.BuildIps(edits, rom);
        var parsed = IpsPatchService.Parse(ips);

        Assert.All(parsed, r => Assert.InRange(r.Data.Length, 1, 0xFFFF));
        Assert.DoesNotContain(parsed, r => r.Offset == eof);
        var rebuilt = new byte[rom.Length];
        IpsPatchService.ApplyInPlace(rebuilt, parsed);
        Assert.Equal(rom, rebuilt);
    }
}