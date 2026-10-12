namespace PKConnect.Core.nds;

/// <summary>
/// One Gen 5 overworld slot: a BTX0 in the overworld NARC (same container and
/// codec as Gen 4 - see <see cref="Btx0SpriteCodec"/>). <see cref="Name"/> is
/// also the set's file stem ("walk" -> walk.png).
/// </summary>
public record Gen5OverworldSlot(string Name, string PackKey, int HeroMember, int HeroineMember);

/// <summary>
/// A Gen 5 trainer battle sprite: an 8-member group (see <see cref="Gen5TrainerSpriteCodec"/>)
/// at <c>group * 8</c> in every NARC of <see cref="NarcPaths"/> - BW keeps two
/// byte-identical copies of the back NARC, both of which have to be patched.
/// </summary>
public record Gen5BattleSlot(string PackKey, IReadOnlyList<string> NarcPaths, int HeroGroup, int HeroineGroup);

/// <summary>
/// The trainer-card figure (see <see cref="Gen5TrainerCardCodec"/>): an 8bpp tile
/// sheet shared by both genders, each gender's BG tilemap, and the 256-colour
/// palette members the card can show it with (the first is the reference).
/// </summary>
public record Gen5TrainerCardSlot(
    string PackKey, string NarcPath, int SheetMember, int HeroMapMember, int HeroineMapMember, IReadOnlyList<int> PaletteMembers);

/// <summary>
/// Gen 5 (NDS) player-graphics locations, per RomType - (NARC path, member)
/// pairs resolved through <see cref="NitroFs"/> at patch time, like Gen 4.
/// Black and White's graphics NARCs are byte-identical, so one revision serves both.
/// </summary>
public static class Gen5PlayerGraphicsOffsets
{
    public sealed record Revision(
        string RomType, string OverworldNarcPath, IReadOnlyList<Gen5OverworldSlot> OverworldSlots,
        Gen5BattleSlot Front, Gen5BattleSlot Back, Gen5BattleSlot? AltBack = null,
        bool AnimatedFront = false, // B2W2 fronts play an idle animation out of the parts sheet, so it's real art too
        Gen5TrainerCardSlot? TrainerCard = null,
        int WalkPreviewFrame = 3); // walk.png runs up, down, left, right (stand, step, step each), then the run cycle

    // a/0/4/9 indices from the user's community sprite-location list, all
    // decoded and checked by eye against Black (2026-10-07).
    private static readonly Gen5OverworldSlot[] BwOverworldSlots =
    [
        new("walk", "WalkSprite", HeroMember: 6, HeroineMember: 9),       // walk + run, 32 frames
        new("bike", "BikeSprite", HeroMember: 7, HeroineMember: 10),
        new("surf", "SurfSprite", HeroMember: 8, HeroineMember: 11),
        new("item", "ItemSprite", HeroMember: 136, HeroineMember: 137),   // found item
        new("save", "SaveSprite", HeroMember: 138, HeroineMember: 139),
        new("heal", "HealSprite", HeroMember: 140, HeroineMember: 141),   // Pokémon Center counter
        new("hm_use", "HmUseSprite", HeroMember: 152, HeroineMember: 153),
        new("balance", "BalanceSprite", HeroMember: 154, HeroineMember: 155),
        new("fish", "FishSprite", HeroMember: 156, HeroineMember: 157),   // 64x64 frames
    ];

    /// <summary>
    /// Black/White. Fronts: a/0/7/2 (95 trainer classes, Hilbert 0, Hilda 1).
    /// Backs: a/0/7/3 and its byte-identical copy a/0/6/4 (Hilbert 0, Hilda 1,
    /// Bianca 2, Cheren 3). Groups 4/5 are a second Hilbert/Hilda back with the
    /// same still and palette but a parts sheet that adds a Poké Ball-in-hand
    /// piece - where the game uses it is still unknown.
    /// </summary>
    public static readonly Revision BlackWhite = new("BW", "a/0/4/9", BwOverworldSlots,
        Front: new("FrontImage", ["a/0/7/2"], HeroGroup: 0, HeroineGroup: 1),
        Back: new("BackImage", ["a/0/7/3", "a/0/6/4"], HeroGroup: 0, HeroineGroup: 1),
        AltBack: new("BackImage", ["a/0/7/3", "a/0/6/4"], HeroGroup: 4, HeroineGroup: 5),
        // Sheet 31 (88x80, 8bpp), hero map 43 (40x80 at col 23), heroine map 44 (48x80, padded
        // with blank tile 5). Members 0-24 are all palettes; 5-11 and 15-20 show the figure (one per
        // card rank/face - the community list only had 5-11, which left the purple card vanilla-coloured).
        TrainerCard: new("TrainerCardImage", "a/0/4/0", SheetMember: 31, HeroMapMember: 43, HeroineMapMember: 44,
            PaletteMembers: CardPalettes(reference: 5)));

    // a/0/4/8 indices from the user's list. Black 2 and White 2's graphics
    // NARCs are byte-identical too.
    private static readonly Gen5OverworldSlot[] B2W2OverworldSlots =
    [
        new("walk", "WalkSprite", HeroMember: 209, HeroineMember: 218),
        new("bike", "BikeSprite", HeroMember: 210, HeroineMember: 219),
        new("surf", "SurfSprite", HeroMember: 211, HeroineMember: 220),
        new("item", "ItemSprite", HeroMember: 212, HeroineMember: 221),
        new("save", "SaveSprite", HeroMember: 213, HeroineMember: 222),
        new("search", "SearchSprite", HeroMember: 214, HeroineMember: 223), // search pouch
        new("hm_use", "HmUseSprite", HeroMember: 215, HeroineMember: 224),
        new("balance", "BalanceSprite", HeroMember: 216, HeroineMember: 225), // high beam wobble
        new("fish", "FishSprite", HeroMember: 217, HeroineMember: 226),
    ];

    /// <summary>
    /// Black 2/White 2. Fronts: a/0/7/1 (188 classes, Nate 0, Rosa 1; Hilbert/Hilda
    /// at 144/145), animated. Backs: a/0/7/2 and its byte-identical copy a/0/6/3
    /// (31 groups: Nate 0, Rosa 1, ball-in-hand alternates 9/10, Hilbert/Hilda
    /// 11/12). The back's 80x80 still is empty - all its art is the parts sheet.
    /// Checked against Black 2 (2026-10-07).
    /// </summary>
    public static readonly Revision Black2White2 = new("B2W2", "a/0/4/8", B2W2OverworldSlots,
        Front: new("FrontImage", ["a/0/7/1"], HeroGroup: 0, HeroineGroup: 1),
        Back: new("BackImage", ["a/0/7/2", "a/0/6/3"], HeroGroup: 0, HeroineGroup: 1),
        AltBack: new("BackImage", ["a/0/7/2", "a/0/6/3"], HeroGroup: 9, HeroineGroup: 10),
        AnimatedFront: true,
        // Same layout as BW's card, shifted members: sheet 36, maps 50/51; figure palettes 8-14 and 18-23.
        TrainerCard: new("TrainerCardImage", "a/0/3/9", SheetMember: 36, HeroMapMember: 50, HeroineMapMember: 51,
            PaletteMembers: CardPalettes(reference: 8)));

    /// <summary>
    /// Every palette member of the card NARC (0-24), the reference first. The codec only
    /// recolours the ones whose figure colours match the reference, so the other screens'
    /// themes (silhouettes, the blue variant) are left alone without listing them by hand.
    /// </summary>
    private static int[] CardPalettes(int reference) => [reference, .. Enumerable.Range(0, 25).Where(m => m != reference)];

    /// <summary>
    /// The League champion's battle sprite, for Connect: Alder's group in Black/White's fronts
    /// archive - 88, one below his trainer class (89); checked by eye, group 89 is the Cyclist.
    /// One "gender", so both groups are his.
    /// </summary>
    public static Gen5BattleSlot? ChampionFrontForGame(string gameId) => gameId switch
    {
        "black" or "white" => new("FrontImage", ["a/0/7/2"], HeroGroup: 88, HeroineGroup: 88),
        // Iris as champion: group 155 of Black 2/White 2's fronts (found by eye; 148-154 are Team Plasma).
        "black2" or "white2" => new("FrontImage", ["a/0/7/1"], HeroGroup: 155, HeroineGroup: 155),
        _ => null,
    };

    /// <summary>
    /// The League champion's overworld sheet, for Connect: Alder's, member 102 of Black/White's
    /// overworld archive (found by eye). 20 frames: the usual twelve walking ones in the order the
    /// player's sheet starts with (up, down, left, right - stand, step, step), then eight of his own.
    /// </summary>
    public static int? ChampionOverworldMemberForGame(string gameId) => gameId switch
    {
        "black" or "white" => 102,
        // Iris as champion: member 276 of a/0/4/8, the usual twelve frames.
        "black2" or "white2" => 276,
        _ => null,
    };

    /// <summary>Frames an NPC's sheet and the player's walk sheet share (see above).</summary>
    public const int NpcWalkFrames = 12;
    /// <summary>The player's walk frame facing the camera, standing: what an NPC's other frames get.</summary>
    public const int WalkFrameFacingDown = 3;

    public static Revision? ForGame(string gameId) => gameId switch
    {
        "black" or "white" => BlackWhite,
        "black2" or "white2" => Black2White2,
        _ => null,
    };
}
