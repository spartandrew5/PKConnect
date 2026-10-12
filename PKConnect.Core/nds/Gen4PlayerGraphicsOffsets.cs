namespace PKConnect.Core.nds;

/// <summary>
/// One overworld sprite slot: a BTX0 file inside the game's overworld-sprite
/// NARC, addressed by member index (per the community "known sprite
/// locations" doc). <see cref="HeroMember"/> is the male protagonist's copy;
/// <see cref="HeroineMember"/> the female's. A pack only ever supplies one
/// image per slot (matching the character it reskins), so today only the
/// hero slot is written - see <see cref="Gen4PlayerGraphicsWriter"/>.
/// </summary>
public record Gen4OverworldSlot(string Name, string PackKey, int HeroMember, int HeroineMember);

/// <summary>
/// One NCER-composited sprite slot (front/back battle sprite, HM-use pose):
/// a (sprite, palette, cell) NCGR/NCLR/NCER triplet inside one NARC, per
/// <see cref="NcerCompositor"/> - unlike <see cref="Gen4OverworldSlot"/>'s
/// flat BTX0 raster, so it needs the reverse-compositing write path in
/// <see cref="Gen4PlayerGraphicsWriter"/> rather than a same-size overwrite.
/// </summary>
public record Gen4CompositeSlot(
    string PackKey, string NarcPath, int CanvasSize,
    int HeroSprite, int HeroPalette, int HeroCell,
    int HeroineSprite, int HeroinePalette, int HeroineCell);

/// <summary>
/// A "duplicate" front/back sprite entry (see <see cref="Gen4CipheredSpriteCodec"/>)
/// - reuses <see cref="Front"/> or <see cref="Back"/>'s own palette member
/// (that's what pairs it to hero vs heroine), but has its own enciphered
/// 160x80 NCGR. Back has two of these (Throwing, Pointing); Front has one
/// (a single pose in the left 80x80 half, the right half unused).
/// </summary>
public record Gen4DuplicateSlot(string NarcPath, int HeroSprite, int HeroPalette, int HeroineSprite, int HeroinePalette);

/// <summary>
/// Diamond/Pearl's real Back animation: unlike every other game's Back (an
/// NCER composite living entirely inside one NARC), DP's send-out frames are
/// LZ10-compressed inside <see cref="BattObjNarcPath"/> (see <see cref="Lz10Codec"/>),
/// while the palette and the "duplicate" transition layer still live in
/// <see cref="DuplicateNarcPath"/> (trbgra.narc) alongside Front's own
/// cipher pair - see <see cref="Gen4PlayerGraphicsExtractor.ExtractDiamondPearl"/>.
/// <see cref="CellMember"/> is shared between hero and heroine (gender-
/// independent positioning) and is never rewritten - only the pixel content
/// changes, not the per-bank layout, so the writer only needs to re-encode
/// and recompress the sprite member.
/// </summary>
public record Gen4BattObjBackSlot(
    string BattObjNarcPath, int HeroSpriteMember, int HeroineSpriteMember, int CellMember, int CanvasSize,
    string DuplicateNarcPath, int HeroDuplicateSprite, int HeroPalette, int HeroineDuplicateSprite, int HeroinePalette);

/// <summary>
/// The trainer-card protagonist sprite (see <see cref="Gen4TrainerCardCodec"/>).
/// The 8bpp sheet <see cref="SpriteMember"/> is assembled into the on-card
/// figure by an NSCR BG tilemap - <see cref="HeroNscrMember"/> for the male,
/// <see cref="HeroineNscrMember"/> for the female - from a scattered set of
/// tiles, so the codec follows that map rather than a flat slice. A pack image
/// is fit to the figure's canvas and injected into the hero's tiles; heroine
/// tiles/indices stay vanilla (the "always reskin the hero" convention). The
/// character palette is a 256-colour bank duplicated across members
/// <see cref="PaletteMemberStart"/>..<see cref="PaletteMemberEnd"/>, all synced.
/// </summary>
public record Gen4TrainerCardSlot(
    string PackKey, string NarcPath, int SpriteMember,
    int HeroNscrMember, int HeroineNscrMember,
    int PaletteMemberStart, int PaletteMemberEnd,
    int VerticalOffset = 0, int HorizontalOffset = 0);

/// <summary>
/// Gen 4 (NDS) player-graphics ROM locations, per RomType. Unlike Gen 1-3
/// these aren't flat ROM addresses - they're (NARC path, member index) pairs
/// resolved through <see cref="NitroFs"/> + <see cref="NarcArchive"/> at
/// patch time, since NitroFS file IDs (and therefore byte offsets) can differ
/// between individual retail dumps of the "same" game.
/// </summary>
public static class Gen4PlayerGraphicsOffsets
{
    public sealed record Revision(
        string RomType, string OverworldNarcPath, IReadOnlyList<Gen4OverworldSlot> OverworldSlots,
        Gen4CompositeSlot? Front = null, Gen4CompositeSlot? Back = null, Gen4CompositeSlot? HmUse = null,
        Gen4DuplicateSlot? FrontDuplicate = null, IReadOnlyList<Gen4DuplicateSlot>? BackDuplicates = null,
        Gen4DuplicateSlot? CipheredFront = null, Gen4BattObjBackSlot? BattObjBack = null,
        Gen4TrainerCardSlot? TrainerCard = null,
        IReadOnlyList<Gen4CompositeSlot>? ExtraBacks = null);

    // Member indices from the community "known sprite locations" doc, all in
    // a/0/8/1. 69/70 (walk) verified byte-exact against a real HG ROM in
    // NdsSpritePipelineTests; the rest follow the doc's hero/heroine-pair
    // convention (heroine = hero + 1) and haven't been individually verified
    // yet the same way, so a wrong entry here would just skip cleanly (same
    // dimension-mismatch guard as every other slot) rather than corrupt data.
    private static readonly Gen4OverworldSlot[] HgSsOverworldSlots =
    [
        new("Walk", "WalkSprite", HeroMember: 69, HeroineMember: 70),
        new("Bike", "BikeSprite", HeroMember: 71, HeroineMember: 72),
        new("Surf", "SurfSprite", HeroMember: 73, HeroineMember: 74),
        new("Fish", "FishSprite", HeroMember: 79, HeroineMember: 80),
        new("Save", "SaveSprite", HeroMember: 87, HeroineMember: 88),
        // "Heal" per the community doc - the same healing-counter pose
        // DP/Pt's own PtOverworldSlots/DpOverworldSlots (member 367/368)
        // wire up, sharing this same HealSprite pack key across all 4 games.
        new("Heal", "HealSprite", HeroMember: 89, HeroineMember: 90),
    ];

    // Front (a/0/5/8) and Back (a/0/0/6): hero sprite/palette/cell group is
    // 0/1/2, heroine's is 5/6/7 (same indices Gen4PlayerGraphicsExtractor.Extract
    // hardcodes). HmUse (a/0/9/3): hero 17/6/18, heroine 20/7/21 - confirmed
    // against real bytes (member 21 matched a user-supplied "3_21.recn").
    // "Duplicate" entries (see Gen4CipheredSpriteCodec): Front's is member
    // 4 (hero) / 9 (heroine); Back has member 4/9 and member 79/84 ("Throwing"/
    // "Pointing" per the community's naming, though Gen4PlayerGraphicsWriter
    // fills both with the animation's own frame 0, not those poses - see its
    // doc comment). This layer draws underneath the real animated NCER
    // sprite for the first couple frames of the send-out; a mismatched pose
    // here showed as a permanent ghosted double against real hardware, not
    // just a brief glitch, until the fill was changed to match the main
    // animation's own starting frame.
    // Two back groups per player (a/0/0/6, 5 members each: sprite, palette,
    // cell, anim, enciphered duplicate): TRAINER_BACKPIC_ETHAN/LYRA = group 0/1
    // (members 0-4 / 5-9) and TRAINER_BACKPIC_ETHAN_2/LYRA_2 = group 15/16
    // (members 75-79 / 80-84). pokeheartgold's TrainerClassToBackpicID picks _2
    // whenever its "isLink" arg is set - which the battle controller sets for
    // SINGLE (and multi) battles, not just link ones; only plain doubles use
    // group 0. So group 15's animation (ExtraBacks) must be written too, or the
    // send-out throw plays the vanilla tiles under the new palette.
    public static readonly Revision HeartGoldSoulSilver = new("HGSS", "a/0/8/1", HgSsOverworldSlots,
        Front: new("FrontImage", "a/0/5/8", CanvasSize: 80, HeroSprite: 0, HeroPalette: 1, HeroCell: 2, HeroineSprite: 5, HeroinePalette: 6, HeroineCell: 7),
        Back: new("BackImage", "a/0/0/6", CanvasSize: 80, HeroSprite: 0, HeroPalette: 1, HeroCell: 2, HeroineSprite: 5, HeroinePalette: 6, HeroineCell: 7),
        ExtraBacks: [new("BackImage", "a/0/0/6", CanvasSize: 80, HeroSprite: 75, HeroPalette: 76, HeroCell: 77, HeroineSprite: 80, HeroinePalette: 81, HeroineCell: 82)],
        HmUse: new("HmUseSprite", "a/0/9/3", CanvasSize: 32, HeroSprite: 17, HeroPalette: 6, HeroCell: 18, HeroineSprite: 20, HeroinePalette: 7, HeroineCell: 21),
        FrontDuplicate: new("a/0/5/8", HeroSprite: 4, HeroPalette: 1, HeroineSprite: 9, HeroinePalette: 6),
        BackDuplicates:
        [
            new("a/0/0/6", HeroSprite: 4, HeroPalette: 1, HeroineSprite: 9, HeroinePalette: 6),
            new("a/0/0/6", HeroSprite: 79, HeroPalette: 76, HeroineSprite: 84, HeroinePalette: 81),
        ],
        // Trainer card (a/0/4/9): sprite member 44 is an 8bpp sheet assembled
        // into the on-card figure by an NSCR tilemap - member 54 (hero) / 55
        // (heroine). The character palette is the 256-colour bank duplicated
        // across members 0..24, all synced on write. See Gen4TrainerCardCodec.
        // VerticalOffset: HGSS's own card window sits 1px up from a literal
        // centred-bottom-of-canvas fit - confirmed by eye in-game.
        TrainerCard: new("TrainerCardImage", "a/0/4/9", SpriteMember: 44,
            HeroNscrMember: 54, HeroineNscrMember: 55,
            PaletteMemberStart: 0, PaletteMemberEnd: 24,
            VerticalOffset: 2));

    // Member indices supplied by the user from a community sprite-location
    // doc (not independently derived), verified by decoding several (Walk,
    // Bike, Surf) and eyeballing the result for both games. All in
    // data/mmodel/mmodel.narc, BTX0 format - same codec as HGSS's a/0/8/1,
    // just a different NARC path/index scheme. Diamond and Pearl share one
    // NitroFS layout; the two games' own slot tables only differ in the
    // "Aerobics"/"Use Poketch" naming at 363/364 and Platinum's extra VS
    // Seeker pose.
    private static readonly Gen4OverworldSlot[] DpOverworldSlots =
    [
        new("Walk", "WalkSprite", HeroMember: 90, HeroineMember: 91),
        new("Bike", "BikeSprite", HeroMember: 92, HeroineMember: 93),
        new("Pokeball", "PokeballSprite", HeroMember: 155, HeroineMember: 156),
        new("WaterFlower", "WaterFlowerSprite", HeroMember: 157, HeroineMember: 158),
        new("Surf", "SurfSprite", HeroMember: 159, HeroineMember: 160),
        new("Outfit", "OutfitSprite", HeroMember: 164, HeroineMember: 165),
        new("Fishing", "FishSprite", HeroMember: 166, HeroineMember: 167),
        new("Aerobics", "AerobicsSprite", HeroMember: 363, HeroineMember: 364),
        new("Save", "SaveSprite", HeroMember: 365, HeroineMember: 366),
        // "Pokecenter" - same healing-counter pose as HGSS's own "Heal" slot,
        // sharing its HealSprite pack key across all 4 games.
        new("Heal", "HealSprite", HeroMember: 367, HeroineMember: 368),
    ];

    private static readonly Gen4OverworldSlot[] PtOverworldSlots =
    [
        new("Walk", "WalkSprite", HeroMember: 90, HeroineMember: 91),
        new("Bike", "BikeSprite", HeroMember: 92, HeroineMember: 93),
        new("Pokeball", "PokeballSprite", HeroMember: 155, HeroineMember: 156),
        new("WaterFlower", "WaterFlowerSprite", HeroMember: 157, HeroineMember: 158),
        new("Surf", "SurfSprite", HeroMember: 159, HeroineMember: 160),
        new("Outfit", "OutfitSprite", HeroMember: 164, HeroineMember: 165),
        new("Fishing", "FishSprite", HeroMember: 166, HeroineMember: 167),
        new("UsePoketch", "UsePoketchSprite", HeroMember: 363, HeroineMember: 364),
        new("Save", "SaveSprite", HeroMember: 365, HeroineMember: 366),
        // "Pokecenter" - same healing-counter pose as HGSS's own "Heal" slot,
        // sharing its HealSprite pack key across all 4 games.
        new("Heal", "HealSprite", HeroMember: 367, HeroineMember: 368),
        new("VsSeeker", "VsSeekerSprite", HeroMember: 415, HeroineMember: 416),
    ];

    // Diamond/Pearl's trfgra.narc/trbgra.narc are a bare 2-member-per-group
    // (sprite, palette) layout, no NCER - group 0 is Lucas, group 1 Dawn,
    // same as Platinum's group convention. Front has no NCER composite
    // counterpart at all (it's the "primary" ciphered pose, not a duplicate
    // reusing someone else's palette) - CipheredFront builds its own fresh
    // palette from the pack's front.png rather than borrowing Back's. Back's
    // real 5-frame send-out animation lives in battle/graphic/batt_obj.narc
    // (member 195 hero / 196 heroine, LZ10-compressed NCGR, cell member 203
    // shared between both) - see Gen4PlayerGraphicsExtractor.ExtractDiamondPearl
    // and Gen4BattObjBackSlot's own doc comment. trbgra's own (sprite,
    // palette) pair per group is the "duplicate" transition layer (same role
    // as HGSS/Platinum's ciphered duplicates), paired with the SAME palette
    // batt_obj's sprite is encoded against so the two layers' colours never
    // drift apart.
    // The Fly cutscene (raise Pokeball -> mount the generic bird -> dismount,
    // reverse) reads the SAME "HmUseSprite" pack key HGSS's own a/0/9/3
    // HmUse composite uses - same bank order/meaning, same 32x32 canvas,
    // just a different physical NARC (data/field_cutin.narc, member 13/14
    // hero sprite/cell, 16/17 heroine; palette 3 hero / 4 heroine - member 2
    // decodes to an unrelated muted colour scheme, likely some other NPC
    // actor sharing this narc). One pack image now serves every Gen 4 game;
    // only where it gets physically written differs. Confirmed by decoding
    // and eyeballing both Lucas and Dawn (initially at the wrong 64x64
    // canvas size before this - 32x32 is correct and still shows the whole
    // pose with no clipping).
    public static readonly Revision Diamond = new("DP", "data/mmodel/mmodel.narc", DpOverworldSlots,
        CipheredFront: new("poketool/trgra/trfgra.narc", HeroSprite: 0, HeroPalette: 1, HeroineSprite: 2, HeroinePalette: 3),
        BattObjBack: new("battle/graphic/batt_obj.narc", HeroSpriteMember: 195, HeroineSpriteMember: 196, CellMember: 203, CanvasSize: 80,
            DuplicateNarcPath: "poketool/trgra/trbgra.narc", HeroDuplicateSprite: 0, HeroPalette: 1, HeroineDuplicateSprite: 2, HeroinePalette: 3),
        HmUse: new("HmUseSprite", "data/field_cutin.narc", CanvasSize: 32, HeroSprite: 13, HeroPalette: 3, HeroCell: 14, HeroineSprite: 16, HeroinePalette: 4, HeroineCell: 17),
        // Trainer card (graphic/trainer_case.narc): sprite member 27, assembled
        // into the figure by NSCR member 35 (hero/Lucas) / 36 (heroine/Dawn) -
        // same clean per-gender tilemap structure as HGSS. Character palette is
        // the 256-colour bank duplicated across members 0..6. See Gen4TrainerCardCodec.
        // VerticalOffset/HorizontalOffset: Diamond's own card window sits a
        // few pixels up/left of a literal centred-bottom-of-canvas fit - same
        // as Platinum's (confirmed by eye in-game).
        TrainerCard: new("TrainerCardImage", "graphic/trainer_case.narc", SpriteMember: 27,
            HeroNscrMember: 35, HeroineNscrMember: 36, PaletteMemberStart: 0, PaletteMemberEnd: 6,
            VerticalOffset: 6, HorizontalOffset: 1));

    // Platinum's trfgra.narc/trbgra.narc mirror HGSS's layout exactly (just a
    // different NARC path/index scheme): 5-member (sprite, palette, cell,
    // anim, ciphered-duplicate) groups, one per trainer class - group 0 is
    // hero, group 1 heroine (confirmed by decoding and eyeballing both
    // Front and Back). Unlike Diamond/Pearl's bare 2-member trfgra/trbgra,
    // Platinum's own Back group already has the full multi-bank send-out
    // animation (8 banks, same as HGSS) - no battle/graphic/batt_obj
    // workaround needed here. Only one "duplicate" member per group (index+4,
    // same palette as the group's own index+1) - Platinum's trbgra has no
    // room for a separate "Pointing" duplicate the way HGSS's a/0/0/6 does
    // (55 members = 11 groups * 5 exactly, no extra members left over).
    public static readonly Revision Platinum = new("PT", "data/mmodel/mmodel.narc", PtOverworldSlots,
        Front: new("FrontImage", "poketool/trgra/trfgra.narc", CanvasSize: 80, HeroSprite: 0, HeroPalette: 1, HeroCell: 2, HeroineSprite: 5, HeroinePalette: 6, HeroineCell: 7),
        Back: new("BackImage", "poketool/trgra/trbgra.narc", CanvasSize: 80, HeroSprite: 0, HeroPalette: 1, HeroCell: 2, HeroineSprite: 5, HeroinePalette: 6, HeroineCell: 7),
        FrontDuplicate: new("poketool/trgra/trfgra.narc", HeroSprite: 4, HeroPalette: 1, HeroineSprite: 9, HeroinePalette: 6),
        BackDuplicates: [new("poketool/trgra/trbgra.narc", HeroSprite: 4, HeroPalette: 1, HeroineSprite: 9, HeroinePalette: 6)],
        // Same data/field_cutin.narc layout as Diamond/Pearl - see Diamond's
        // own HmUse doc comment above.
        HmUse: new("HmUseSprite", "data/field_cutin.narc", CanvasSize: 32, HeroSprite: 13, HeroPalette: 3, HeroCell: 14, HeroineSprite: 16, HeroinePalette: 4, HeroineCell: 17),
        // Trainer card (graphic/trainer_case.narc): sprite member 31, assembled
        // by NSCR member 40 (hero/Lucas) / 41 (heroine/Dawn) - Platinum
        // redesigned the card so its maps differ from DP's (42/43 exist too but
        // render scrambled against member 31 - they mirror DP's arrangement,
        // not Platinum's). Character palette is member 0. See Gen4TrainerCardCodec.
        // VerticalOffset/HorizontalOffset: Platinum's own card window sits a
        // few pixels up/left of a literal centred-bottom-of-canvas fit -
        // confirmed by eye in-game. HGSS/DP's own windows don't need this.
        TrainerCard: new("TrainerCardImage", "graphic/trainer_case.narc", SpriteMember: 31,
            HeroNscrMember: 40, HeroineNscrMember: 41, PaletteMemberStart: 0, PaletteMemberEnd: 6,
            VerticalOffset: 6, HorizontalOffset: 1));

    // Pokemon Connect League champion (Platinum Connect base patch): the champion trainer profile's
    // pack drawn over Cynthia instead of the player. Front pic = trainer class 69's trfgra group
    // (5 members per class, same layout as the player's group 0), which only her champion battles
    // use; overworld = mmodel 470, the base patch's dedicated copy of her model that only the
    // Champion's Room, the hallway and the Hall of Fame use (story scenes keep the real Cynthia).
    private const int PtCynthiaClass = 69;
    private const int PtConnectChampionModel = 470;

    public static readonly Revision PlatinumChampion = new("PT", "data/mmodel/mmodel.narc",
        [new Gen4OverworldSlot("Walk", "WalkSprite", HeroMember: PtConnectChampionModel, HeroineMember: PtConnectChampionModel)],
        Front: new("FrontImage", "poketool/trgra/trfgra.narc", CanvasSize: 80,
            HeroSprite: PtCynthiaClass * 5, HeroPalette: PtCynthiaClass * 5 + 1, HeroCell: PtCynthiaClass * 5 + 2,
            HeroineSprite: PtCynthiaClass * 5, HeroinePalette: PtCynthiaClass * 5 + 1, HeroineCell: PtCynthiaClass * 5 + 2),
        FrontDuplicate: new("poketool/trgra/trfgra.narc",
            HeroSprite: PtCynthiaClass * 5 + 4, HeroPalette: PtCynthiaClass * 5 + 1,
            HeroineSprite: PtCynthiaClass * 5 + 4, HeroinePalette: PtCynthiaClass * 5 + 1));

    // HeartGold Connect: a/0/5/8 holds 5 members per trainer class too (645 = 129 classes). Champion =
    // Lance's class (86), which only his League battles use; overworld = mmodel 863, the base patch's
    // copy of Lance's model that only the Champion's Room and the Hall of Fame use (story scenes keep
    // the real Lance).
    private const int HgLanceClass = 86;
    private const int HgConnectChampionModel = 863;
    // Professor Oak replaces Red at Mt. Silver in the HeartGold Connect build: Red's exclusive class
    // (109, renamed "Professor" there) gets Oak's front pic on every launch (not a player/champion pack).
    private const int HgRedClass = 109;

    public static readonly Revision HeartGoldChampion = ClassFrontRevision("HGSS", "a/0/8/1", "a/0/5/8", HgLanceClass) with
    {
        OverworldSlots = [new Gen4OverworldSlot("Walk", "WalkSprite", HeroMember: HgConnectChampionModel, HeroineMember: HgConnectChampionModel)],
    };
    public static readonly Revision HeartGoldOak = ClassFrontRevision("HGSS", "a/0/8/1", "a/0/5/8", HgRedClass);

    /// <summary>Just one trainer class's front pic group (5 members per class) - no overworld slots.</summary>
    private static Revision ClassFrontRevision(string name, string overworldNarc, string frontNarc, int trainerClass) =>
        new(name, overworldNarc, [],
            Front: new("FrontImage", frontNarc, CanvasSize: 80,
                HeroSprite: trainerClass * 5, HeroPalette: trainerClass * 5 + 1, HeroCell: trainerClass * 5 + 2,
                HeroineSprite: trainerClass * 5, HeroinePalette: trainerClass * 5 + 1, HeroineCell: trainerClass * 5 + 2),
            FrontDuplicate: new(frontNarc,
                HeroSprite: trainerClass * 5 + 4, HeroPalette: trainerClass * 5 + 1,
                HeroineSprite: trainerClass * 5 + 4, HeroinePalette: trainerClass * 5 + 1));

    public static Revision? ChampionForGame(string gameId) => gameId switch
    {
        "platinum" => PlatinumChampion,
        "heartgold" or "soulsilver" => HeartGoldChampion,
        _ => null,
    };

    public static Revision? ForGame(string gameId) => gameId switch
    {
        "heartgold" or "soulsilver" => HeartGoldSoulSilver,
        "diamond" or "pearl" => Diamond,
        "platinum" => Platinum,
        _ => null,
    };
}
