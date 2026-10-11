using System.Text;
using PKHeX.Core;
using PKConnect.Core.champions;

namespace PKConnect.Core.Tests.champions;

/// <summary>
/// Black/White's Hall of Fame, in the layout read from real saves (see
/// HallOfFameReader): 15 teams of 0x16C at 0x7C800 with a second
/// copy at 0x7E000, then the next slot and the total.
/// </summary>
public class Gen5HallOfFameTests
{
    private const int Block = 0x7C800;
    private const int Mirror = 0x7E000;
    private const int TeamSize = 0x16C;
    private const int MonSize = 0x3C;
    private const int Footer = 15 * TeamSize;

    private static SAV5BW Save()
    {
        var data = new byte[0x80000];
        // An untouched Hall of Fame is all 0xFF.
        data.AsSpan(Block, 0x1800).Fill(0xFF);
        data.AsSpan(Mirror, 0x1800).Fill(0xFF);
        return new SAV5BW(data);
    }

    private static void WriteTeam(SAV5BW save, int block, int slot, params (ushort Species, byte Level, string Nickname)[] mons)
    {
        var team = save.Data.Slice(block + slot * TeamSize, TeamSize);
        team.Clear();
        for (var i = 0; i < mons.Length; i++)
        {
            var mon = team.Slice(i * MonSize, MonSize);
            BitConverter.TryWriteBytes(mon, mons[i].Species);
            mon[2] = mons[i].Level;
            Encoding.Unicode.GetBytes(mons[i].Nickname).CopyTo(mon[0x0C..]);
            mon[0x0C + mons[i].Nickname.Length * 2] = 0xFF;
            mon[0x0D + mons[i].Nickname.Length * 2] = 0xFF;
        }
    }

    private static void WriteCounts(SAV5BW save, int block, uint next, uint total)
    {
        BitConverter.TryWriteBytes(save.Data.Slice(block + Footer, 4), next);
        BitConverter.TryWriteBytes(save.Data.Slice(block + Footer + 4, 4), total);
    }

    [Fact]
    public void UntouchedBlock_HasNoTeams()
    {
        Assert.Empty(HallOfFameReader.Read(Save()));
    }

    [Fact]
    public void Teams_ComeOutOldestFirst()
    {
        var save = Save();
        save.Data.Slice(Block, Footer + 8).Clear();
        WriteTeam(save, Block, 0, (497, 67, "Serperior"), (644, 63, "Zekrom"));
        WriteTeam(save, Block, 1, (497, 100, "Serperior"), (149, 60, "Dragonite"));
        WriteCounts(save, Block, 2, 2);

        var teams = HallOfFameReader.Read(save);

        Assert.Equal(2, teams.Count);
        var newest = teams[^1];
        Assert.Equal([497, 149], newest.Pokemon.Select(p => p.Species));
        Assert.Equal([100, 60], newest.Pokemon.Select(p => p.Level));
        Assert.Equal("Dragonite", newest.Pokemon[1].Nickname);
    }

    [Fact]
    public void Black2White2_KeepItAtAnotherAddress()
    {
        const int b2w2Block = 0x74000;
        var data = new byte[0x80000];
        data.AsSpan(b2w2Block, 0x3000).Fill(0xFF);
        var save = new SAV5B2W2(data);
        save.Data.Slice(b2w2Block, Footer + 8).Clear();
        var team = save.Data.Slice(b2w2Block, TeamSize);
        BitConverter.TryWriteBytes(team, (ushort)503);
        team[2] = 100;
        BitConverter.TryWriteBytes(save.Data.Slice(b2w2Block + Footer, 4), 1u);
        BitConverter.TryWriteBytes(save.Data.Slice(b2w2Block + Footer + 4, 4), 1u);

        var teams = HallOfFameReader.Read(save);

        Assert.Single(teams);
        Assert.Equal(503, teams[0].Pokemon[0].Species);
        Assert.Equal(100, teams[0].Pokemon[0].Level);
    }

    [Fact]
    public void SecondCopy_IsReadWhenTheFirstWasNeverWritten()
    {
        var save = Save();
        save.Data.Slice(Mirror, Footer + 8).Clear();
        WriteTeam(save, Mirror, 0, (635, 70, "Hydreigon"));
        WriteCounts(save, Mirror, 1, 1);

        var teams = HallOfFameReader.Read(save);

        Assert.Single(teams);
        Assert.Equal(635, teams[0].Pokemon[0].Species);
    }
}
