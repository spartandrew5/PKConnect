using PKConnect.Core.champions;
using PKConnect.Core.launch;
using PKConnect.Core.nds;

namespace PKConnect.Core.Tests.launch;

public class Gen5ConnectPatchServiceTests
{
    private const int Alder = 407;
    private const int Lenora = 21;

    // ---- Text files ------------------------------------------------------------------------------

    /// <summary>A one-section text file holding these lines, each encrypted from its own key.</summary>
    private static byte[] TextFile(params (string Text, ushort Key)[] lines)
    {
        const int section = 0x10;
        var dataStart = section + 4 + lines.Length * 8;
        var body = new List<byte>();
        var file = new byte[dataStart];
        file[0] = 1;
        BitConverter.TryWriteBytes(file.AsSpan(2), (ushort)lines.Length);
        BitConverter.TryWriteBytes(file.AsSpan(12), (uint)section);
        for (var i = 0; i < lines.Length; i++)
        {
            BitConverter.TryWriteBytes(file.AsSpan(section + 4 + i * 8), (uint)(dataStart - section + body.Count));
            BitConverter.TryWriteBytes(file.AsSpan(section + 8 + i * 8), (ushort)(lines[i].Text.Length + 1));
            var key = lines[i].Key;
            foreach (var unit in lines[i].Text.Select(c => (ushort)c).Append((ushort)0xFFFF))
            {
                body.AddRange(BitConverter.GetBytes((ushort)(unit ^ key)));
                key = (ushort)((key << 3) | (key >> 13));
            }
        }
        var size = (uint)(dataStart - section + body.Count);
        BitConverter.TryWriteBytes(file.AsSpan(4), size);
        BitConverter.TryWriteBytes(file.AsSpan(section), size);
        return [.. file, .. body];
    }

    [Theory]
    [InlineData("Sky")]       // shorter than the line it replaces
    [InlineData("Alder")]     // the same length
    [InlineData("Cynthia")]   // longer
    public void TextFile_ReplaceLine_ChangesThatLineOnly(string name)
    {
        var file = TextFile(("Ingo", 0x1234), ("Alder", 0xBEEF), ("Emmet", 0x0F0F));

        var changed = Gen5TextFile.ReplaceLine(file, 1, name);

        Assert.Equal("Ingo", Gen5TextFile.ReadLine(changed, 0));
        Assert.Equal(name, Gen5TextFile.ReadLine(changed, 1));
        Assert.Equal("Emmet", Gen5TextFile.ReadLine(changed, 2));
        Assert.Equal(file.Length + (name.Length - "Alder".Length) * 2, changed.Length);
        // The line keeps the key it starts with: its first unit is encrypted as before.
        var dataStart = 0x10 + 4 + 3 * 8;
        var lineStart = dataStart + ("Ingo".Length + 1) * 2;
        Assert.Equal(BitConverter.ToUInt16(file, lineStart) ^ 'A', BitConverter.ToUInt16(changed, lineStart) ^ name[0]);
    }

    // ---- Skipping a script's story part --------------------------------------------------------------

    [Fact]
    public void SkipOver_PartEndingInAJump_JumpsToTheSamePlace()
    {
        // setflag 542; clearflag 540; setwork 0x4088 = 1; jump +0x16
        var script = Convert.FromHexString("0000" + "23001E02" + "24001C02" + "280088400100" + "1E0016000000" + "0200");
        var skip = Gen5ConnectPatchService.SkipOver(script, 2);

        Assert.NotNull(skip);
        Assert.Equal(6, skip.Length);
        Assert.Equal(0x1E, skip[0]);
        var jumpEnd = 2 + 4 + 4 + 6 + 6;
        Assert.Equal(jumpEnd + 0x16, 2 + 6 + BitConverter.ToInt32(skip, 2));
    }

    [Fact]
    public void SkipOver_PartEndingInAReturn_Returns()
    {
        var script = Convert.FromHexString("9500C700" + "28009C400100" + "23008200" + "0500");
        Assert.Equal([0x05, 0x00], Gen5ConnectPatchService.SkipOver(script, 0));
    }

    [Fact]
    public void SkipOver_UnknownCommand_IsRefused()
    {
        // A message command in the part: not something to skip blindly.
        var script = Convert.FromHexString("23001E02" + "3D0000040100000000" + "0500");
        Assert.Null(Gen5ConnectPatchService.SkipOver(script, 0));
    }

    // ---- The League guard's badge check ---------------------------------------------------------------

    [Fact]
    public void AddLeagueGate_HooksTheGuardAndAppendsTheCheck()
    {
        // ... pushflag 302; push 0; compare; ... the "no" line; what walks the player back; end
        var script = Convert.FromHexString("2E00" + "10002E010800000011000100" + "6500" + "3C0000040300000005000000" + "3F00" + "0200");

        var gated = Gen5ConnectPatchService.AddLeagueGate(script);

        Assert.NotNull(gated);
        Assert.True(gated.Length > script.Length);
        // The hook is now a jump to the appended routine...
        Assert.Equal(0x1E, gated[2]);
        Assert.Equal(script.Length, 2 + 6 + BitConverter.ToInt32(gated, 4));
        // ...which asks for each of the eight badges in turn...
        var routine = gated.AsSpan(script.Length).ToArray();
        for (var badge = 0; badge < 8; badge++)
        {
            Assert.Equal(0xD5, routine[badge * 31]);
            Assert.Equal(badge, routine[badge * 31 + 4]);
        }
        // ...and everything else is as it was.
        Assert.Equal(script.AsSpan(10).ToArray(), gated.AsSpan(10, script.Length - 10).ToArray());
    }

    [Fact]
    public void AddLeagueGate_UnknownScript_IsRefused()
    {
        Assert.Null(Gen5ConnectPatchService.AddLeagueGate(Convert.FromHexString("2E0002000200")));
    }

    // ---- Against the real ROM -------------------------------------------------------------------------

    private static readonly ChampionTeamDTO Champion = new("Cynthia",
    [
        new(445, 62, Moves: ["Dragon Rush", "Earthquake", "Flamethrower", "Giga Impact"], HeldItem: "Sitrus Berry"),
        new(448, 60, Moves: ["Aura Sphere", "Psychic"]),
        new(700, 60, Moves: ["Moonblast"]), // Sylveon: not in Gen 5
    ]);

    private static byte[] Apply(byte[] rom, Gen5ConnectPatchService.Result result)
    {
        var patched = (byte[])rom.Clone();
        foreach (var (offset, data) in result.Edits) data.CopyTo(patched, offset);
        return patched;
    }

    private static byte[] Member(byte[] rom, string path, int index)
    {
        var file = NitroFs.ResolveFile(rom, path)!.Value;
        var narc = rom.AsSpan(file.Offset, file.Length).ToArray();
        var (offset, length) = NarcArchive.GetMemberRange(narc, index);
        return narc.AsSpan(offset, length).ToArray();
    }

    [Fact]
    public void VisitingLaunch_AgainstRealRom_WritesGymsScriptsAndChampion()
    {
        var rom = TestRoms.Read(TestRoms.Black);

        var result = Gen5ConnectPatchService.Build("black", rom, new NdsFilePlacer(rom), isVisiting: true, averageLevel: 50, Champion, championLevelBonus: 4, randomSeed: 7);

        Assert.Empty(result.Warnings);
        Assert.Equal(11, result.Gyms.Count); // Striaton's three, six more, Opelucid's two
        Assert.All(result.Gyms, g => Assert.True(g.Applied, g.Reason));
        Assert.All(result.Champion, c => Assert.True(c.Applied, c.Reason));

        var patched = Apply(rom, result);

        // A leader: six Pokémon with moves and items, around the traveller's level.
        var lenora = Member(patched, "a/0/9/2", Lenora);
        Assert.Equal(3, lenora[0]);
        Assert.Equal(6, lenora[3]);
        var party = Member(patched, "a/0/9/3", Lenora);
        Assert.Equal(6 * 18, party.Length);
        Assert.InRange(party[2], 48, 52);
        Assert.NotEqual(0, BitConverter.ToUInt16(party, 10)); // a move

        // Every other trainer is as it was.
        Assert.Equal(Member(rom, "a/0/9/3", 100), Member(patched, "a/0/9/3", 100));
        Assert.Equal(Member(rom, "a/0/9/2", 100), Member(patched, "a/0/9/2", 100));

        // The champion: Garchomp and Lucario as they are, Sylveon replaced by something the game has.
        var alder = Member(patched, "a/0/9/3", Alder);
        Assert.Equal(3 * 18, alder.Length);
        Assert.Equal(445, BitConverter.ToUInt16(alder, 4));
        Assert.Equal(448, BitConverter.ToUInt16(alder, 18 + 4));
        Assert.InRange(BitConverter.ToUInt16(alder, 36 + 4), 1, 649);
        Assert.Equal("Cynthia", Gen5TextFile.ReadLine(Member(patched, "a/0/0/2", 190), Alder));

        // Each gym script changed, and only by a jump or a return where its story part began.
        foreach (var script in new[] { 14, 38, 58, 126, 194, 216, 228, 242 })
        {
            var before = Member(rom, "a/0/5/7", script);
            var after = Member(patched, "a/0/5/7", script);
            Assert.Equal(before.Length, after.Length);
            var changed = Enumerable.Range(0, before.Length).Count(i => before[i] != after[i]);
            Assert.InRange(changed, 1, 18);
        }

        // The League guard asks for the badges, and says so.
        Assert.True(Member(patched, "a/0/5/7", 272).Length > Member(rom, "a/0/5/7", 272).Length);
        Assert.Contains("eight", Gen5TextFile.ReadLine(Member(patched, "a/0/0/3", 141), 7));
        // A leader's story line is reworded; the champion speaks under their own name.
        Assert.StartsWith("Welcome, challenger!", Gen5TextFile.ReadLine(Member(patched, "a/0/0/3", 34), 0));
        Assert.StartsWith("Cynthia: ", Gen5TextFile.ReadLine(Member(patched, "a/0/0/3", 149), 23));
    }

    [Fact]
    public void Black2_VisitingLaunch_AgainstRealRom_WritesBothDifficultiesAndSkipsEveryGymStory()
    {
        var rom = TestRoms.Read(TestRoms.Black2);

        var result = Gen5ConnectPatchService.Build("black2", rom, new NdsFilePlacer(rom), isVisiting: true, averageLevel: 50, Champion, championLevelBonus: 4, randomSeed: 7);

        Assert.Empty(result.Warnings);
        Assert.Equal(8, result.Gyms.Count);
        Assert.All(result.Gyms, g => Assert.True(g.Applied, g.Reason));
        var patched = Apply(rom, result);

        // Burgh: the Normal-mode trainer and the Challenge-mode one get the same six (each keeps its own
        // difficulty byte, the first of every entry, so the Challenge team still has the better stats).
        Assert.Equal(6 * 18, Member(patched, "a/0/9/2", 154).Length);
        Assert.Equal(Member(patched, "a/0/9/2", 154)[1..18], Member(patched, "a/0/9/2", 766)[1..18]);
        Assert.True(Member(patched, "a/0/9/2", 766)[0] > Member(patched, "a/0/9/2", 154)[0]);
        // Iris, as she is fought on a finished save, is the champion; her name follows.
        Assert.Equal(445, BitConverter.ToUInt16(Member(patched, "a/0/9/2", 536), 4));
        Assert.Equal("Cynthia", Gen5TextFile.ReadLine(Member(patched, "a/0/0/2", 382), 536));
        // Each gym script changed by a few bytes, in place.
        foreach (var script in new[] { 978, 1004, 976, 126, 194, 216, 242, 946 })
        {
            var before = Member(rom, "a/0/5/6", script);
            var after = Member(patched, "a/0/5/6", script);
            Assert.Equal(before.Length, after.Length);
            Assert.InRange(Enumerable.Range(0, before.Length).Count(i => before[i] != after[i]), 1, 6);
        }

        // The League guard asks for the badges; the champion and a leader speak their new lines.
        Assert.True(Member(patched, "a/0/5/6", 272).Length > Member(rom, "a/0/5/6", 272).Length);
        Assert.Contains("eight", Gen5TextFile.ReadLine(Member(patched, "a/0/0/3", 155), 3));
        Assert.StartsWith("Cynthia: ", Gen5TextFile.ReadLine(Member(patched, "a/0/0/3", 163), 3));
        Assert.StartsWith("Welcome, challenger!", Gen5TextFile.ReadLine(Member(patched, "a/0/0/3", 36), 0));
    }

    [Fact]
    public void HomeLaunch_AgainstRealRom_OnlyTouchesTheChampion()
    {
        var rom = TestRoms.Read(TestRoms.Black);

        var result = Gen5ConnectPatchService.Build("black", rom, new NdsFilePlacer(rom), isVisiting: false, averageLevel: 50, Champion, championLevelBonus: 4);

        Assert.Empty(result.Gyms);
        var patched = Apply(rom, result);
        Assert.Equal(Member(rom, "a/0/9/3", Lenora), Member(patched, "a/0/9/3", Lenora));
        Assert.Equal(Member(rom, "a/0/5/7", 38), Member(patched, "a/0/5/7", 38));
        Assert.Equal(445, BitConverter.ToUInt16(Member(patched, "a/0/9/3", Alder), 4));
    }
}
