using PKConnect.Core.regions;
using PKHeX.Core;
using PKVault.Core;

namespace PKConnect.Core.champions;

/// <summary>
/// Who becomes a region's champion, and when. A trainer takes the title on an event, never by
/// being looked at: their save's Hall of Fame has to differ from what it was when that save last
/// took the title. A save seen for the first time with a team already in its Hall of Fame counts
/// too - its trainer played the game through.
/// </summary>
public static class ChampionCrowning
{
    /// <summary>
    /// What a save's Hall of Fame looks like: how many teams it holds, and the newest one. It
    /// changes exactly when the League is beaten again, and is "" for an empty Hall of Fame.
    /// </summary>
    public static string Fingerprint(IReadOnlyList<HallOfFameTeamDTO> hallOfFame)
    {
        if (hallOfFame.Count == 0)
            return "";
        var newest = hallOfFame[^1].Pokemon.Select(mon => $"{mon.Species}:{mon.Level}:{mon.Nickname}");
        return $"{hallOfFame.Count}|{string.Join(";", newest)}";
    }

    /// <summary>The region whose title the save's League gives.</summary>
    public static string? RegionOf(SaveFile save) => ConnectRegions.ForSave(save);

    /// <summary>
    /// Whether a League win in this save is a title at all. Black and White have no door guard
    /// that could be made to ask a visitor for badges, and a finished save can Fly to the League,
    /// so there the eight badges are asked for here.
    /// </summary>
    public static bool HasEarnedTheTitle(SaveFile save) => save is not SAV5 sav5 || (sav5.Misc.Badges & 0xFF) == 0xFF;

    /// <summary>
    /// The newest Hall of Fame team as a champion's team. The Hall of Fame records little more
    /// than species and level, so each Pokémon takes its moves and held item from the party
    /// member of the same species, each used once. Null when there is no team.
    /// </summary>
    public static ChampionTeamDTO? BuildChampion(
        SaveFile save, IReadOnlyList<HallOfFameTeamDTO> hallOfFame, string profileId, string profileName, DateTime now)
    {
        if (hallOfFame.Count == 0)
            return null;

        var wrapper = new SaveWrapper(save);
        var party = wrapper.GetPartyData();
        var used = new HashSet<int>();
        var strings = GameInfo.GetStrings("en");

        var team = new List<ChampionMonDTO>();
        foreach (var mon in hallOfFame[^1].Pokemon.Where(mon => mon.Species > 0))
        {
            string[]? moves = null;
            string? heldItem = null;

            var index = Enumerable.Range(0, party.Count).FirstOrDefault(i => party[i].Species == mon.Species && !used.Contains(i), -1);
            if (index >= 0)
            {
                used.Add(index);
                var member = party[index];
                var names = member.Moves
                    .Where(move => move > 0 && move < strings.Move.Count)
                    .Select(move => strings.Move[move])
                    .ToArray();
                moves = names.Length > 0 ? names : null;

                var item = member.GetConvertedHeldItem();
                heldItem = item > 0 && item < strings.Item.Count ? strings.Item[item] : null;
            }

            var nickname = string.IsNullOrWhiteSpace(mon.Nickname) ? null : mon.Nickname;
            team.Add(new ChampionMonDTO(mon.Species, mon.Level, nickname, moves, heldItem));
        }
        if (team.Count == 0)
            return null;

        var name = string.IsNullOrWhiteSpace(save.OT) ? profileName : save.OT.Trim();
        return new ChampionTeamDTO(
            string.IsNullOrWhiteSpace(name) ? "Champion" : name,
            team,
            Source: profileId,
            BeatenAt: now,
            GameVersion: wrapper.PlayedVersion.ToString());
    }
}