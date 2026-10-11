using PKHeX.Core;
using PKConnect.Core.champions;
using PKConnect.Core.nds;

namespace PKConnect.Core.launch;

/// <summary>
/// Pokemon Connect for Black and White. There is no decomp to build a base patch from, so the
/// retail ROM is edited directly at launch, in its own data files:
///  - gym leaders (visiting launches): each leader's party (a/0/9/3) and trainer record (a/0/9/2)
///    become a randomized, level-scaled team of six with moves and held items;
///  - gym scripts (visiting launches): a visitor plays on a finished save with the badges taken
///    away, and a leader fights whoever lacks their badge - but each leader's script then replays
///    the story that followed that fight the first time (Lenora runs off after Team Plasma, Elesa
///    walks to the drawbridge...) and winds the save's story progress back. That part of each
///    script is jumped over: badge and TM are still given, nothing else changes;
///  - champion (every launch): Alder, who is the League champion on a finished save, gets the
///    region's current champion's team and name.
/// The trainer archives grow, so they are placed through <see cref="NdsFilePlacer"/>.
/// </summary>
public static class Gen5ConnectPatchService
{
    private const string TextPath = "a/0/0/2";
    /// <summary>Black/White's trainer names file (a member of <see cref="TextPath"/>).</summary>
    private const int TrainerNamesFile = 190;

    /// <summary>How a gym script's story part is skipped (see <see cref="GameData.StorySkips"/>).</summary>
    private enum SkipKind
    {
        /// <summary>Followed to the jump or return that ends it (see <see cref="SkipOver"/>).</summary>
        Follow,
        /// <summary>The part is the tail of a routine and includes a scene that can't be followed: return at once.</summary>
        Return,
        /// <summary>The part runs on into a scene in the main script: jump to the script's closing "wait for a button, release the player, end".</summary>
        ToEnd,
        /// <summary>
        /// The part's flag and work-value writes are followed by a scene the script needs played
        /// (Marlon dives off after every talk, and returning before it leaves the player locked):
        /// jump over the writes to the scene.
        /// </summary>
        ToScene,
    }

    /// <summary>
    /// Where one pair of games keeps what Connect edits. <paramref name="StorySkips"/>: where each gym
    /// leader's script starts on what followed the fight in the story - the first commands of that part,
    /// as they are in the script file. A script with several leaders (Striaton's three, Opelucid's two)
    /// has the part once per leader.
    /// </summary>
    private sealed record GameData(
        string TrainerDataPath, string TrainerPartyPath, string ScriptPath, int TrainerNamesFile,
        int[] ChampionTrainerIds, (string Gym, int ScriptFile, string Start, SkipKind Kind)[] StorySkips, LeagueGate Gate)
    {
        /// <summary>What the champion says in place of the game's own champion ({0} = the champion's name).</summary>
        public (string Narc, int Member, int Line, string Text)[] ChampionLines { get; init; } = [];
        /// <summary>Lines of gym leaders (and the League guard) reworded for a visitor.</summary>
        public (string Narc, int Member, int Line, string Text)[] GymLines { get; init; } = [];
    }

    /// <summary>
    /// The guard at the League's entrance (see <see cref="AddLeagueGate"/>): his script file, the test
    /// his script opens with (where the badge check hooks in), the line he answers "no" with, and
    /// the text line he refuses with - null when the game has none to spare, and the (reworded)
    /// "no" answer serves for both.
    /// </summary>
    private sealed record LeagueGate(int ScriptFile, string Hook, string Decline, int? RefusalLine);

    // Built on first use: the line tables they point at are declared further down.
    private static GameData? blackWhite, black2White2;

    private static GameData BlackWhite => blackWhite ??= new("a/0/9/2", "a/0/9/3", "a/0/5/7", TrainerNamesFile,
        // Alder: the champion of the League once the story is over.
        ChampionTrainerIds: [407],
        StorySkips:
        [
            ("Striaton", 14, "23001E0224001C02280088400100", SkipKind.Follow),
            ("Nacrene", 38, "0300100004003C01000023007E00", SkipKind.Follow),
            ("Castelia", 58, "9500C7009500C8002800", SkipKind.Follow),
            ("Nimbasa", 126, "2800934001002400380223003502", SkipKind.Follow),
            ("Driftveil", 194, "6C00100028009A400100", SkipKind.Follow),
            ("Mistralton", 216, "95007D0095007F00", SkipKind.Follow),
            ("Icirrus", 228, "2800A7400100280097400300", SkipKind.Follow),
            ("Opelucid", 242, "95000E0195001301", SkipKind.Follow),
        ],
        // pushflag 302; push 0; compare - and the "no" answer, line 3. Line 7 was "you may not enter at this time".
        Gate: new(272, "10002E010800000011000100", "3C0000040300000005000000", LeagueGuardRefusalLine))
    {
        ChampionLines = BlackWhiteChampionLines,
        GymLines = BlackWhiteGymLines,
    };

    /// <summary>
    /// Black 2/White 2. Every leader and the champion exist twice: the Normal-mode trainer and a
    /// Challenge-mode one the script picks instead (Easy mode uses the Normal one), so both are written -
    /// the pools name the second as "&lt;game&gt;-challenge". Iris is fought as 341 the first time and 536
    /// from then on (776 / 781 in Challenge mode).
    /// </summary>
    private static GameData Black2White2 => black2White2 ??= new("a/0/9/1", "a/0/9/2", "a/0/5/6", 382,
        ChampionTrainerIds: [536, 781, 341, 776],
        StorySkips:
        [
            ("Aspertia", 978, "23006E092800A84002002400E602", SkipKind.Return),
            ("Virbank", 1004, "9500B2009500B3002800AD400100", SkipKind.Follow),
            ("Castelia", 976, "230070092800B4400100", SkipKind.Follow),
            ("Nimbasa", 126, "230071092300080128009C400100", SkipKind.Follow),
            ("Driftveil", 194, "95003F0195004001", SkipKind.ToEnd),
            ("Mistralton", 216, "950095009500960095009700", SkipKind.Follow),
            ("Opelucid", 242, "95007D01950080019500810195007F01", SkipKind.Follow),
            ("Humilau", 946, "95005E01950060019500620195005F01", SkipKind.ToScene),
        ],
        // pushflag 366; push 0; compare - and the "no" answer. The guard's text file has no line to spare.
        Gate: new(272, "10006E010800000011000100", "3C0000040300010005000000", null))
    {
        ChampionLines = Black2White2ChampionLines,
        GymLines = Black2White2GymLines,
    };

    private static GameData? ForGame(string gameId) => gameId switch
    {
        "black" or "white" => BlackWhite,
        "black2" or "white2" => Black2White2,
        _ => null,
    };

    private const int TrainerDataSize = 20;
    private const int FlagMoves = 1;
    private const int FlagItems = 2;
    private const int MonSize = 18; // 8 + held item + four moves
    private const int PartyCapacity = 6;
    private const int MaxMove = 559;
    private const int MaxNameLength = 7;

    public static bool IsConnectGame(string gameId) => ForGame(gameId) is not null;

    public record ZoneResult(string Zone, int TeamSize, bool Applied, string? Reason);

    public sealed record Result(
        List<(int Offset, byte[] Data)> Edits,
        List<ZoneResult> Gyms,
        List<ZoneResult> Champion,
        List<string> Warnings);

    private sealed record PartyMon(int Species, int Level, int Item, IReadOnlyList<int> Moves);

    // Script commands that may stand in a skipped part, with the size of their arguments.
    private const int CmdWait = 0x03, CmdCall = 0x04, CmdReturn = 0x05, CmdJump = 0x1E;
    private static readonly Dictionary<int, int> SkippableArgumentBytes = new()
    {
        [CmdWait] = 2, [CmdCall] = 4,
        [0x23] = 2, [0x24] = 2, // set / clear flag
        [0x28] = 4,             // set work value
        [0x6C] = 2, [0x95] = 2, [0xDA] = 6,
        [0x26] = 4, [0x262] = 4, // Black 2/White 2: add to a work value; an unidentified one that closes most gyms' parts
    };

    /// <summary>
    /// All of this launch's Connect edits. <paramref name="placer"/> is the patch's one placer
    /// (see <see cref="NdsFilePlacer.Reserve"/> when sprites were placed before).
    /// </summary>
    public static Result Build(
        string gameId, byte[] rom, NdsFilePlacer placer, bool isVisiting, double? averageLevel,
        ChampionTeamDTO? champion, int championLevelBonus, int? randomSeed = null)
    {
        var result = new Result([], [], [], []);
        if (ForGame(gameId) is not { } game) return result;
        if (NitroFs.ResolveFile(rom, game.TrainerDataPath) is not { } dataFile || NitroFs.ResolveFile(rom, game.TrainerPartyPath) is not { } partyFile)
        {
            result.Warnings.Add("trainer archives not found in the ROM");
            return result;
        }
        var dataNarc = rom.AsSpan(dataFile.Offset, dataFile.Length).ToArray();
        var partyNarc = rom.AsSpan(partyFile.Offset, partyFile.Length).ToArray();
        var newData = new Dictionary<int, byte[]>();
        var newParties = new Dictionary<int, byte[]>();

        string? WriteParty(int trainerId, List<PartyMon> mons)
        {
            if (trainerId < 0 || trainerId >= NarcArchive.MemberCount(dataNarc)) return "trainer not found";
            var (offset, length) = NarcArchive.GetMemberRange(dataNarc, trainerId);
            if (length != TrainerDataSize) return "unexpected trainer record size";
            var data = dataNarc.AsSpan(offset, length).ToArray();
            data[0] = FlagMoves | FlagItems;
            data[3] = (byte)mons.Count;

            // The Pokémon keep the trainer's own difficulty (IVs) and the second byte of the
            // original first entry (ability and gender).
            var (oldOffset, oldLength) = NarcArchive.GetMemberRange(partyNarc, trainerId);
            var difficulty = oldLength >= 2 ? partyNarc[oldOffset] : (byte)250;
            var party = new byte[mons.Count * MonSize];
            for (var i = 0; i < mons.Count; i++)
            {
                var mon = mons[i];
                var o = i * MonSize;
                party[o] = difficulty;
                party[o + 2] = (byte)mon.Level;
                WriteU16(party, o + 4, (ushort)mon.Species);
                WriteU16(party, o + 8, (ushort)mon.Item);
                for (var k = 0; k < 4; k++)
                    WriteU16(party, o + 10 + k * 2, (ushort)(k < mon.Moves.Count ? mon.Moves[k] : 0));
            }
            newData[trainerId] = data;
            newParties[trainerId] = party;
            return null;
        }

        if (isVisiting && averageLevel.HasValue)
        {
            BuildGyms(gameId, averageLevel.Value, randomSeed, WriteParty, result);
            BuildScripts(rom, placer, game, result);
            WriteText(rom, placer, game.GymLines, "", "gym text", result);
        }
        if (champion is not null)
        {
            BuildChampion(gameId, game, rom, placer, champion, averageLevel, championLevelBonus, WriteParty, result);
        }

        if (newData.Count > 0)
        {
            try
            {
                result.Edits.AddRange(placer.Place(game.TrainerDataPath, NarcArchive.Rebuild(dataNarc, newData)).Edits);
                result.Edits.AddRange(placer.Place(game.TrainerPartyPath, NarcArchive.Rebuild(partyNarc, newParties)).Edits);
            }
            catch (Exception ex)
            {
                // Without the archives no team was written: report every zone as skipped.
                result.Warnings.Add($"trainer archives could not be placed: {ex.Message}");
                Fail(result.Gyms, ex.Message);
                Fail(result.Champion, ex.Message);
            }
        }
        return result;
    }

    private static void Fail(List<ZoneResult> zones, string reason)
    {
        for (var i = 0; i < zones.Count; i++)
            if (zones[i].Applied) zones[i] = zones[i] with { Applied = false, TeamSize = 0, Reason = reason };
    }

    private static void BuildGyms(
        string gameId, double averageLevel, int? randomSeed, Func<int, List<PartyMon>, string?> writeParty, Result result)
    {
        var random = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
        var config = GymTeamPoolConfigLoader.Load();
        var variance = (int)Math.Round(config.DefaultLevelVariance);
        foreach (var leader in config.Leaders.Where(l => l.TrainerIds.ContainsKey(gameId)))
        {
            if (leader.Pool.Count == 0)
            {
                result.Gyms.Add(new(leader.DisplayName, 0, false, "No gym team pool for this leader; the vanilla team is kept"));
                continue;
            }
            var size = Math.Clamp(leader.TeamSize, 1, Math.Min(leader.Pool.Count, PartyCapacity));
            var mons = leader.Pool.OrderBy(_ => random.Next()).Take(size)
                .Select(p => new PartyMon(p.Species, Level(averageLevel, variance, random), p.Item, p.Moves))
                .ToList();
            var reason = writeParty(leader.TrainerIds[gameId], mons);
            // The leader's Challenge-mode trainer (Black 2/White 2) gets the same team.
            if (reason is null && leader.TrainerIds.TryGetValue($"{gameId}-challenge", out var challengeId))
                reason = writeParty(challengeId, mons);
            result.Gyms.Add(new(leader.DisplayName, reason is null ? mons.Count : 0, reason is null, reason));
        }
    }

    /// <summary>
    /// The region's current champion into Alder's battle, fitted to the game by
    /// <see cref="ChampionTeamSubstitution"/>, and their name into his name slot.
    /// </summary>
    private static void BuildChampion(
        string gameId, GameData game, byte[] rom, NdsFilePlacer placer, ChampionTeamDTO champion, double? averageLevel, int championLevelBonus,
        Func<int, List<PartyMon>, string?> writeParty, Result result)
    {
        var team = ChampionTeamSubstitution.Adapt(gameId, champion.Team).Take(PartyCapacity).ToList();
        if (team.Count == 0)
        {
            result.Champion.AddRange(game.ChampionTrainerIds.Select(id => new ZoneResult($"champion {id}", 0, false, "No champion Pokemon exist in this game")));
            return;
        }

        var strings = GameInfo.GetStrings("en");
        var itemTable = strings.GetItemStrings(EntityContext.Gen5, GameVersion.W);
        var teamAvg = team.Average(t => t.Level);
        var shift = averageLevel.HasValue ? averageLevel.Value + championLevelBonus - teamAvg : 0;
        var mons = team.Select(t => new PartyMon(
                t.Species,
                Math.Clamp((int)Math.Round(t.Level + shift), 2, 100),
                IndexOf(itemTable, t.HeldItem),
                (t.Moves ?? []).Select(n => IndexOf(strings.Move, n)).Where(id => id > 0 && id <= MaxMove).Take(4).ToArray()))
            .ToList();

        var named = false;
        foreach (var trainerId in game.ChampionTrainerIds)
        {
            var reason = writeParty(trainerId, mons);
            result.Champion.Add(new($"champion {trainerId}", reason is null ? mons.Count : 0, reason is null, reason));
            named |= reason is null;
        }
        if (named && !string.IsNullOrWhiteSpace(champion.ChampionName))
        {
            var name = champion.ChampionName.Trim();
            // The name under every trainer the champion is fought as, then the champion's own lines.
            (string, int, int, string)[] lines =
                [.. game.ChampionTrainerIds.Select(id => (TextPath, game.TrainerNamesFile, id, "{0}")), .. game.ChampionLines];
            WriteText(rom, placer, lines, name.Length > MaxNameLength ? name[..MaxNameLength] : name, "champion text", result);
        }
    }

    // Text control codes, as the game's own lines use them.
    private const string NewLine = "\uFFFE";
    /// <summary>Wait for a button press, then clear the box.</summary>
    private const string Wait = "\uF000\uBE01\u0000";

    /// <summary>
    /// What the champion says, in place of Alder's lines: {0} is the champion's name. The lines
    /// are neutral on purpose - any trainer may hold the title. Each is no longer than what it
    /// replaces would allow (see <see cref="WriteText"/>).
    /// </summary>
    private static readonly (string Narc, int Member, int Line, string Text)[] BlackWhiteChampionLines =
    [

        // Said in battle: first turn, last Pokémon, last Pokémon nearly down, defeated.
        (TextPath, 189, 1758, $"You're good! But I won't give up{NewLine}this title that easily!"),
        (TextPath, 189, 1759, $"Your team has real spirit!{NewLine}Impressive!"),
        (TextPath, 189, 1760, $"Not yet! I won't lose my title{NewLine}without a fight!"),
        (TextPath, 189, 1761, $"That was a battle worthy of a Champion.{Wait}{NewLine}The title is yours now.{NewLine}Wear it well!"),
        // The champion's room: the first meeting, every later one, and after the battle.
        (StoryTextPath, 149, 22, $"{{0}}: You beat the Elite Four.{NewLine}I hold the Champion's title now.{Wait}{NewLine}If you want it, come and take it{NewLine}from me in battle!{Wait}"),
        (StoryTextPath, 149, 23, $"{{0}}: I won this title in battle,{NewLine}and that's how I'll defend it.{Wait}{NewLine}Show me everything you've got!{Wait}"),
        (StoryTextPath, 149, 24, $"{{0}}: That was a great battle.{NewLine}The title of Champion is yours!{Wait}{NewLine}Come with me.{Wait}"),
        // The Hall of Fame room.
        (StoryTextPath, 150, 0, $"{{0}}: This room is the Hall of Fame.{Wait}{NewLine}It honors the Trainers and Pok\u00E9mon{NewLine}who proved their strength here.{Wait}"),
        (StoryTextPath, 150, 1, $"{{0}}: My name is engraved here, too.{NewLine}Now it's your turn.{Wait}"),
    ];

    private const string StoryTextPath = "a/0/0/3";

    /// <summary>
    /// Black 2/White 2: Iris's lines in her room and the Hall of Fame, as the champion's. What she
    /// says in battle is general enough to stay ("No! Not yet! We can fight on!!").
    /// </summary>
    private static readonly (string Narc, int Member, int Line, string Text)[] Black2White2ChampionLines =
    [
        // The champion's room: the first meeting, every later one, and after the battle.
        (StoryTextPath, 163, 2, $"{{0}}: So you made it past the Elite Four.{Wait}{NewLine}I hold the Champion's title now.{Wait}{NewLine}If you want it, come and take it{NewLine}from me in battle!{Wait}"),
        (StoryTextPath, 163, 3, $"{{0}}: I won this title in battle,{NewLine}and that's how I'll defend it.{Wait}{NewLine}Show me everything you've got!{Wait}"),
        (StoryTextPath, 163, 4, $"{{0}}: That was a great battle.{NewLine}The title of Champion is yours!{Wait}{NewLine}OK, let's go!{Wait}"),
        // The Hall of Fame room: the first time, and again.
        (StoryTextPath, 164, 0, $"{{0}}: So, this room is{NewLine}the Hall of Fame!{Wait}{NewLine}It honors the Trainers and Pok\u00E9mon{NewLine}who proved their strength here.{Wait}"),
        (StoryTextPath, 164, 1, $"{{0}}: You've made it here again!{Wait}{NewLine}My name is engraved here, too.{NewLine}Now it's your turn once more.{Wait}"),
    ];

    /// <summary>Black 2/White 2: the leaders' story-bound lines, and the League guard's "no" answer, which is also his refusal.</summary>
    private static readonly (string Narc, int Member, int Line, string Text)[] Black2White2GymLines =
    [
        // The League guard (see AddLeagueGate): it was "go through the entrance there, and prepare".
        (StoryTextPath, 155, 3, $"Don't neglect preparation!{Wait}{NewLine}You need all eight Unova Gym{NewLine}Badges to enter.{Wait}"),
        // Cheren, before and after the battle (his first battle as a leader, his first challenger).
        (StoryTextPath, 171, 5, $"I've been looking forward to this!{Wait}{NewLine}Let's both do our best and have{NewLine}a battle we can be proud of!{Wait}"),
        (StoryTextPath, 171, 6, $"That battle has made me feel really{NewLine}glad I became a Gym Leader...{Wait}{NewLine}I give you this in honor of the strength{NewLine}you and your Pok\u00E9mon showed!{Wait}"),
        // Burgh, before the battle (thanks for the sewers, then his own Dwebble and Leavanny by name).
        (StoryTextPath, 36, 0, $"Welcome, challenger!{Wait}{NewLine}My Bug-type Pok\u00E9mon have been scurrying{NewLine}with excitement about getting to{Scroll}{NewLine}battle you.{Wait}{NewLine}I'd say my Bug-type Pok\u00E9mon are{NewLine}pretty great!{Wait}{NewLine}Well now...{NewLine}Let's get right to it!{Wait}"),
        // Drayden, after the TM (Professor Juniper's errand; "please wait outside").
        (StoryTextPath, 140, 18, $"Now go, and keep aiming for the top,{NewLine}like a dragon taking flight."),
    ];
    /// <summary>Scroll up one line (the game's lines use it from a box's third line on).</summary>
    private const string Scroll = "\uF000\uBE00\u0000";

    /// <summary>
    /// Gym leader lines that only make sense in the story (thanks for helping in the forest, "wait
    /// for me on Route 5", Team Plasma heading for Icirrus, go after Alder and N), reworded for a
    /// visitor in each leader's own voice. Everything else a leader says is left as it is. Each new
    /// line is shorter than the one it replaces, so no file grows.
    /// </summary>
    private static readonly (string Narc, int Member, int Line, string Text)[] BlackWhiteGymLines =
    [
        // The League guard's refusal (see AddLeagueGate); it was "you may not enter at this time".
        (StoryTextPath, 141, LeagueGuardRefusalLine, $"My apologies, young Trainer.{Wait}{NewLine}You need all eight Unova Gym{NewLine}Badges to enter.{Wait}"),
        // Burgh, before the battle ("Thanks again for your help.").
        (StoryTextPath, 34, 0, $"Welcome, challenger!{Wait}{NewLine}My bug Pok\u00E9mon are scurrying with{NewLine}excitement about getting to battle you.{Wait}{NewLine}Let's get straight to it!{Wait}"),
        // Elesa, after the TM (she offers to lower the drawbridge).
        (StoryTextPath, 68, 5, $"You and your Pok\u00E9mon shine together.{Wait}{NewLine}I hope the rest of your journey{NewLine}is just as dazzling."),
        // Clay, before the battle (Elesa vouched for the player) and after it (meet him at the cave).
        (StoryTextPath, 102, 0, $"Well...{Wait}{NewLine}Think it's about time to see fer myself{NewLine}what kinda skills ya been diggin' up{Scroll}{NewLine}on yer travels.{Wait}"),
        (StoryTextPath, 102, 3, $"So this is yer fifth Badge, huh?{Wait}{NewLine}If that's so, Pok\u00E9mon up to Lv. 60{NewLine}will obey ya.{Wait}{NewLine}Now get goin'! There's plenty more{NewLine}fer ya to dig up out there."),
        // Skyla, the TM's explanation (it went on to Team Plasma flying over Twist Mountain).
        (StoryTextPath, 113, 4, $"My present to you is Acrobatics.{NewLine}If the user doesn't have a held item,{Scroll}{NewLine}its power doubles.{Scroll}{NewLine}It's a pretty technical move.{Wait}{NewLine}Be sure to compensate for the{NewLine}lack of a held item!"),
        // Iris and Drayden, after the TM (Alder is facing N at the League).
        (StoryTextPath, 126, 5, $"Ooh, hey!{NewLine}That's all eight Badges, right?{Wait}{NewLine}Then you can take on the Pok\u00E9mon League!{Wait}{NewLine}It's past Victory Road, at the end of{NewLine}Route 10!"),
        (StoryTextPath, 126, 13, $"With every Badge in Unova, the{NewLine}Pok\u00E9mon League awaits you.{Wait}{NewLine}It is past Route 10, which runs from{NewLine}Opelucid City to Victory Road.{Wait}{NewLine}Show the Champion the strength{NewLine}you showed me."),
    ];

    /// <summary>
    /// Lines of a text file that only the story plays (the champion's room before the credits):
    /// emptied, if need be, to make room for longer champion lines without the file growing.
    /// </summary>
    private static readonly Dictionary<(string Narc, int Member), int[]> SpareLines = new()
    {
        [(StoryTextPath, 149)] = [2, 20, 21, 12, 0, 1],
    };

    /// <summary>
    /// Writes lines of text ({0} in a line = <paramref name="name"/>). A changed file is kept at its
    /// own size - padded when it got shorter, which is the usual case - so each edit stays inside
    /// that one file; only a file that still grew makes its whole archive be rebuilt and placed.
    /// </summary>
    private static void WriteText(
        byte[] rom, NdsFilePlacer placer, (string Narc, int Member, int Line, string Text)[] lines, string name, string label, Result result)
    {
        foreach (var archive in lines.GroupBy(l => l.Narc))
        {
            try
            {
                var file = NitroFs.ResolveFile(rom, archive.Key) ?? throw new InvalidOperationException("archive not found");
                var narc = rom.AsSpan(file.Offset, file.Length).ToArray();
                var replaced = new Dictionary<int, byte[]>();
                var inPlace = new List<(int Offset, byte[] Data)>();
                var grew = false;
                foreach (var member in archive.GroupBy(l => l.Member))
                {
                    var (offset, length) = NarcArchive.GetMemberRange(narc, member.Key);
                    var text = narc.AsSpan(offset, length).ToArray();
                    foreach (var line in member) text = Gen5TextFile.ReplaceLine(text, line.Line, string.Format(line.Text, name));
                    foreach (var spare in SpareLines.GetValueOrDefault((archive.Key, member.Key), []))
                    {
                        if (text.Length <= length) break;
                        text = Gen5TextFile.ReplaceLine(text, spare, "");
                    }

                    replaced[member.Key] = text;
                    if (text.Length > length)
                    {
                        grew = true;
                        continue;
                    }
                    var padded = new byte[length];
                    Array.Fill(padded, (byte)0xFF);
                    text.CopyTo(padded, 0);
                    inPlace.Add((file.Offset + offset, padded));
                }

                if (grew) result.Edits.AddRange(placer.Place(archive.Key, NarcArchive.Rebuild(narc, replaced)).Edits);
                else result.Edits.AddRange(inPlace);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{label} ({archive.Key}): {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The script changes of a visiting launch: the gyms' story parts jumped over (see
    /// <see cref="StorySkips"/>) and the League guard's badge check (see <see cref="AddLeagueGate"/>).
    /// The guard's script grows, so the script archive is rebuilt and placed as a whole.
    /// </summary>
    private static void BuildScripts(byte[] rom, NdsFilePlacer placer, GameData game, Result result)
    {
        if (NitroFs.ResolveFile(rom, game.ScriptPath) is not { } file)
        {
            result.Warnings.Add("script archive not found; the gyms would replay their story, so play them at your own risk");
            return;
        }
        var narc = rom.AsSpan(file.Offset, file.Length).ToArray();
        var replaced = new Dictionary<int, byte[]>();
        byte[] Script(int member)
        {
            if (replaced.TryGetValue(member, out var known)) return known;
            var (offset, length) = NarcArchive.GetMemberRange(narc, member);
            return narc.AsSpan(offset, length).ToArray();
        }

        foreach (var (gym, scriptFile, startHex, kind) in game.StorySkips)
        {
            var script = Script(scriptFile);
            var start = Convert.FromHexString(startHex);
            var found = 0;
            for (var at = 0; at + start.Length <= script.Length; at++)
            {
                if (!script.AsSpan(at, start.Length).SequenceEqual(start)) continue;
                if (Skip(script, at, kind) is not { } skip)
                {
                    result.Warnings.Add($"[gym-script:{gym}] couldn't follow the script at 0x{at:X}; left as it is");
                    continue;
                }
                skip.CopyTo(script, at);
                found++;
            }
            if (found == 0) result.Warnings.Add($"[gym-script:{gym}] story part not found; the gym is left as it is");
            else replaced[scriptFile] = script;
        }

        if (AddLeagueGate(Script(game.Gate.ScriptFile), game.Gate) is { } gated) replaced[game.Gate.ScriptFile] = gated;
        else result.Warnings.Add("[league-gate] the entrance guard's script wasn't recognised; the League is open to any visitor");

        if (replaced.Count == 0) return;
        try
        {
            // Only the guard's script changes size; without it every edit stays inside its own script.
            if (replaced.All(r => r.Value.Length == NarcArchive.GetMemberRange(narc, r.Key).Length))
            {
                foreach (var (member, script) in replaced)
                    result.Edits.Add((file.Offset + NarcArchive.GetMemberRange(narc, member).Offset, script));
            }
            else
            {
                result.Edits.AddRange(placer.Place(game.ScriptPath, NarcArchive.Rebuild(narc, replaced)).Edits);
            }
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"scripts could not be placed: {ex.Message}");
        }
    }

    /// <summary>The command to write where a story part starts, by the gym's <see cref="SkipKind"/>; null if it can't be worked out.</summary>
    private static byte[]? Skip(ReadOnlySpan<byte> script, int at, SkipKind kind)
    {
        switch (kind)
        {
            case SkipKind.Return:
                return [CmdReturn, 0];
            case SkipKind.ToEnd:
                // wait for a button; release the player; end
                var end = script[at..].IndexOf(Convert.FromHexString("30002F000200"));
                if (end < 0) return null;
                var jump = new byte[6];
                jump[0] = CmdJump;
                BitConverter.TryWriteBytes(jump.AsSpan(2), end - 6);
                return jump;
            case SkipKind.ToScene:
                var o = at;
                while (o + 2 <= script.Length && SkippableArgumentBytes.TryGetValue(script[o] | script[o + 1] << 8, out var size)) o += 2 + size;
                if (o < at + 6) return null;
                var over = new byte[6];
                over[0] = CmdJump;
                BitConverter.TryWriteBytes(over.AsSpan(2), o - (at + 6));
                return over;
            default:
                return SkipOver(script, at);
        }
    }


    private const int LeagueGuardRefusalLine = 7;
    private const int BadgeCount = 8;

    /// <summary>
    /// The guard at the Pokémon League's entrance asks "do you want to go in?" and lets anyone
    /// through - the game's own badge check is on Route 10, which a visitor on a finished save
    /// flies past. This makes the guard check all eight badges first: his script gets a jump to
    /// a new routine appended at its end, which sends the player back with the guard's (reworded)
    /// "you may not enter" line unless every badge is held, and otherwise carries on as before.
    /// Null if the script isn't the one expected.
    /// </summary>
    public static byte[]? AddLeagueGate(byte[] script) => AddLeagueGate(script, BlackWhite.Gate);

    private static byte[]? AddLeagueGate(byte[] script, LeagueGate gate)
    {
        // Where the routine hooks in: the guard's "has he given his speech before?" test.
        byte[] hook = Convert.FromHexString(gate.Hook); // pushflag; push 0; compare (differs)
        // The "no" answer's line; what follows it walks the player back out and ends the script.
        byte[] decline = Convert.FromHexString(gate.Decline);
        var hookAt = IndexOfOnly(script, hook);
        var declineAt = IndexOfOnly(script, decline);
        if (hookAt < 0 || declineAt < 0) return null;
        var resume = hookAt + 8;                 // the compare
        var sendBack = declineAt + decline.Length;

        var code = new List<byte>();
        var routineAt = script.Length;
        int Here() => routineAt + code.Count;
        void Emit(params byte[] bytes) => code.AddRange(bytes);
        void EmitJump(int target) { Emit(CmdJump, 0); code.AddRange(BitConverter.GetBytes(target - (Here() + 4))); }

        // For each badge: held -> on to the next; otherwise refuse. (The only comparison whose
        // meaning is certain from the game's own scripts is 1, "differs", so the test is "the
        // result differs from 0" and the refusal is the fall-through.) The refusal's address
        // isn't known yet, so those jumps are filled in after.
        const int resultVar = 0x8008;
        var refusals = new List<int>();
        for (var badge = 0; badge < BadgeCount; badge++)
        {
            Emit(0xD5, 0, resultVar & 0xFF, resultVar >> 8, (byte)badge, 0); // hasbadge -> var
            Emit(0x09, 0, resultVar & 0xFF, resultVar >> 8);                 // push var
            Emit(0x08, 0, 0, 0);                                             // push 0
            Emit(0x11, 0, 1, 0);                                             // compare: differs
            Emit(0x1F, 0, 0xFF, 6, 0, 0, 0);                                 // if so, skip the next jump
            Emit(CmdJump, 0);
            refusals.Add(code.Count);
            Emit(0, 0, 0, 0);
        }
        // All eight: redo what the hook overwrote and go back.
        Emit(hook[..8]);
        EmitJump(resume);
        var refuseAt = Here();
        if (gate.RefusalLine is { } line)
        {
            Emit(0x3C, 0, 0x00, 0x04, (byte)line, 0, 0, 0, 5, 0, 0, 0);
            EmitJump(sendBack);
        }
        else
        {
            EmitJump(declineAt); // the "no" answer's own line, then out
        }
        foreach (var at in refusals)
            BitConverter.GetBytes(refuseAt - (routineAt + at + 4)).CopyTo(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(code).Slice(at, 4));

        var patched = new byte[script.Length + code.Count];
        script.CopyTo(patched, 0);
        code.CopyTo(patched, routineAt);
        patched[hookAt] = CmdJump;
        patched[hookAt + 1] = 0;
        BitConverter.GetBytes(routineAt - (hookAt + 6)).CopyTo(patched, hookAt + 2);
        return patched;
    }

    /// <summary>Where <paramref name="pattern"/> is, if it is there exactly once; else -1.</summary>
    private static int IndexOfOnly(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern)
    {
        var at = data.IndexOf(pattern);
        return at >= 0 && data[(at + 1)..].IndexOf(pattern) < 0 ? at : -1;
    }

    /// <summary>
    /// The command to put at <paramref name="at"/> so that the part starting there does nothing:
    /// a jump to where the part's own closing jump goes, or a return if it ends with one. Null if
    /// the part holds a command not known to be safe to skip.
    /// </summary>
    public static byte[]? SkipOver(ReadOnlySpan<byte> script, int at)
    {
        var o = at;
        while (o + 2 <= script.Length)
        {
            var command = script[o] | script[o + 1] << 8;
            if (command == CmdReturn) return [CmdReturn, 0];
            if (command == CmdJump)
            {
                if (o + 6 > script.Length) return null;
                var target = o + 6 + BitConverter.ToInt32(script.Slice(o + 2, 4));
                var jump = new byte[6];
                jump[0] = CmdJump;
                BitConverter.TryWriteBytes(jump.AsSpan(2), target - (at + 6));
                return jump;
            }
            if (!SkippableArgumentBytes.TryGetValue(command, out var size)) return null;
            o += 2 + size;
        }
        return null;
    }

    private static int Level(double average, int variance, Random random) =>
        Math.Clamp((int)Math.Round(average + random.Next(-variance, variance + 1)), 2, 100);

    private static int IndexOf(IReadOnlyList<string> table, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        for (var i = 1; i < table.Count; i++)
            if (string.Equals(table[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    private static void WriteU16(byte[] b, int o, ushort v)
    {
        b[o] = (byte)v;
        b[o + 1] = (byte)(v >> 8);
    }
}
