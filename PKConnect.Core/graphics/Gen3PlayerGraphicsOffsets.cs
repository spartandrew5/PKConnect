using PKConnect.Core.patching;

namespace PKConnect.Core.graphics;

/// <summary>
/// Pull specific frames from a shared "combined" sheet. SoulGold-style packs ship
/// one <c>surfing.png</c> holding the seated + hopping surf poses interleaved
/// (sit-S, jump-S, sit-N, jump-N, sit-W, jump-W) instead of separate
/// <c>sit_rse.png</c> / <c>jump.png</c> / <c>sit_frlg.png</c>. When the pack
/// provides <see cref="PackKey"/> it supersedes the slot's normal key: slot
/// frame <c>f</c> is taken from source frame <c>Frames[f]</c>. Source frames are
/// square (the sheet is a horizontal strip <c>height</c>px per frame).
/// </summary>
public record Gen3CombinedSheet(string PackKey, IReadOnlyList<int> Frames);

/// <summary>
/// A fall-back pack key: the other Gen 3 layout's name for the same sprite
/// (e.g. RSE <c>FieldMoveSprite</c> == FRLG <c>ItemSprite</c>). That layout may
/// store the art at a different native frame width - <see cref="SourceFrameWidth"/>
/// tells the writer how to slice the sheet before re-fitting it to this slot.
/// </summary>
public record Gen3AltPackKey(string Key, int SourceFrameWidth);

/// <summary>
/// One overworld sprite slot: a run of consecutive entries in the game's main
/// overworld image table, one per animation frame. Each table entry is 8 bytes
/// (4-byte GBA pointer to the uncompressed 4bpp frame, then its 4-byte length).
/// Frames are written in place; the frame count comes from the pack sheet width.
/// </summary>
/// <param name="WriteFrameOrder">
/// Where pack frame <c>k</c> is written, as an offset from <c>StartIndex</c>.
/// Needed when the game's pic table isn't a straight 0,1,2,... map onto the
/// image table (underwater / watering reference several source frames plus
/// aliased duplicates - a later pack frame written straight would land on an
/// alias of an earlier one and clobber it). Null = identity. Its length also
/// caps the frame count.
/// </param>
public record Gen3OverworldSlot(
    string Name, string PackKey, int StartIndex, int FrameWidth, int FrameHeight,
    IReadOnlyList<Gen3AltPackKey>? AltPackKeys = null, int MaxFrames = 0, Gen3CombinedSheet? Combined = null,
    IReadOnlyList<int>? WriteFrameOrder = null)
{
    public int FrameByteLength =>
        FrameWidth / GbaTileCodec.TileSize * (FrameHeight / GbaTileCodec.TileSize) * GbaTileCodec.BytesPerTile;
}

/// <summary>Compressed front battle image: LZ10, relocated + repointed, LZ10 palette.</summary>
public record Gen3FrontSlot(int PointerEntry, int PaletteEntry, int TilesWide, int TilesTall)
{
    public int ByteLength => TilesWide * TilesTall * GbaTileCodec.BytesPerTile;
}

/// <summary>Back battle image. FRLG/Em: uncompressed in place. RS: LZ10 relocated. Palette always LZ10.</summary>
public record Gen3BackSlot(bool Compressed, int PointerLocation, int PaletteEntry, int TilesWide, int TilesTall)
{
    public int ByteLength => TilesWide * TilesTall * GbaTileCodec.BytesPerTile;

    /// <summary>64x64 animation frames: 5 on FRLG, 4 on RSE.</summary>
    public int FrameCount => TilesTall / TilesWide;
}

/// <summary>
/// Map icon (16x16). FRLG: LZ10 image relocated, palette raw at a pointer.
/// RSE: image + palette both raw, at direct data offsets.
/// </summary>
public record Gen3MapIconSlot(bool Compressed, int ImageAddr, int PaletteAddr)
{
    public int ByteLength => 2 * 2 * GbaTileCodec.BytesPerTile; // 16x16 4bpp = 128
}

/// <summary>
/// A player-linked sprite that lives OUTSIDE the overworld image table: its 4bpp
/// tiles sit at a fixed ROM address as one contiguous run of same-size frames,
/// row-major within each frame, edited in place. Rendered against the normal
/// overworld palette. Used for the FRLG field-effect sprites - the surf blob
/// and the Fly bird - whose art the game references through separate
/// field-effect pic tables rather than the main image table.
/// </summary>
/// <param name="PartialFrames">A pack strip with exactly this many frames is written starting at
/// <paramref name="PartialStartFrame"/> (the other frames keep the vanilla art) - e.g. the Fly bird's two
/// male-rider frames (1-2) out of five.</param>
public record Gen3RawTileSlot(
    string Name, string PackKey, int TilesAddr,
    int FrameWidth, int FrameHeight, int MaxFrames, bool VerticalStrip,
    int PartialFrames = 0, int PartialStartFrame = 0)
{
    public int FrameByteLength =>
        FrameWidth / GbaTileCodec.TileSize * (FrameHeight / GbaTileCodec.TileSize) * GbaTileCodec.BytesPerTile;
}

/// <summary>
/// FRLG "Prof. Oak's speech" intro portrait - the "your very own POKeMON legend
/// is about to unfold" screen. An 8bpp / 32-colour LZ77 BG image (64x96), drawn
/// from a fixed sequential tilemap generated in code. The compressed tiles are
/// relocated + repointed; the 32-colour palette (BGR555, loaded at colour index
/// 64) is overwritten in place. RSE's Birch intro shows no player portrait, so
/// this slot is FRLG-only.
/// </summary>
public record Gen3IntroSlot(int TilesPointerLocation, int PaletteAddr)
{
    public const int WidthPx = 64;
    public const int HeightPx = 96;
    public const int PaletteColors = 32;

    /// <summary>Global palette colour the 8bpp pixel bytes are offset from.</summary>
    public const int PaletteBaseColorIndex = 64;

    /// <summary>8bpp: one byte per pixel.</summary>
    public int DecompressedLength => WidthPx * HeightPx;
    public int PaletteByteLength => PaletteColors * 2;
}

/// <summary>
/// Gen 3 (GBA) player-graphics ROM offsets, per game revision. Ported from
/// UPR-FVX's <c>gen3_offsets.ini</c> + <c>Gen3Constants</c>. All slots target
/// the PC1 player ("Red" on FRLG, "Brendan" on RSE).
/// </summary>
public static class Gen3PlayerGraphicsOffsets
{
    public sealed record Revision(
        string Id,
        int OverworldSpriteImagesTable,
        int OverworldPalettesTable,
        int NormalPaletteIndex,
        int ReflectionPaletteIndex,
        int? UnderwaterPaletteIndex,
        Gen3FrontSlot Front,
        /// <summary>
        /// Base address of the shared gTrainerFrontPicTable / palette-relative
        /// offset — the SAME table gTrainers[].trainerPic indexes into. Lets a
        /// caller compute a Gen3FrontSlot for an arbitrary trainer index (e.g.
        /// the region's champion NPC), not just this game's player index — see
        /// ChampionGraphicsPatchService.
        /// </summary>
        int TrainerFrontImagesTable,
        int TrainerFrontPaletteRel,
        Gen3BackSlot Back,
        Gen3MapIconSlot MapIcon,
        Gen3IntroSlot? Intro,
        int FreeSpaceStart,
        string RunSpriteOrder, // "frlg" | "rse" - the frame order this game expects
        IReadOnlyList<Gen3OverworldSlot> OverworldSlots,
        IReadOnlyList<Gen3RawTileSlot> RawTileSlots);

    /// <summary>
    /// The pack-frame index for each game slot when a pack's RunSprite sheet is
    /// stored in a different order than the target game. Ports UPR-FVX's
    /// FRLGPlayerCharacterGraphics / RSEPlayerCharacterGraphics run reorders.
    /// Returns null for identity (same order, or no/blank mode).
    /// </summary>
    public static int[]? RunFramePermutation(string? packMode, string gameOrder)
    {
        var pack = (packMode ?? gameOrder).Trim().ToLowerInvariant();
        return (pack, gameOrder) switch
        {
            ("frlg", "rse") => [0, 3, 6, 1, 2, 4, 5, 7, 8],
            ("rse", "frlg") => [0, 3, 4, 1, 5, 6, 2, 7, 8],
            _ => null,
        };
    }

    // ---- FRLG (FireRed / LeafGreen) ----
    private const int FrlgFrontTrainerIndex = 135;
    private const int FrlgBackTrainerIndex = 0;

    // Flat frame indices into the overworld image table (sPicTable_RedNormal and
    // the player pic tables linked after it). Verified against retail FireRed by
    // matching each table pointer back to the decomp .4bpp asset:
    //   0..8    red_normal          (walk, 9)
    //   9..19   red_surf_run[3..]   (run, 11)
    //   20..28  red_bike            (bike, 9 x 32x32)
    //   58..69  red_surf_run        (the seated surf pose - the "sit" sprite, 12)
    //   82..90  red_item            (item / field-move pose, 9)
    //   100..105 red_vs_seeker_bike (arm-raised-on-bike = the item-on-bike pose, 6 x 32x32)
    //   852..   red_fish           (fishing, 12 x 32x32)
    // The surf BLOB and Fly BIRD are separate field-effect art - see FrlgRawSlots.
    private static readonly Gen3OverworldSlot[] FrlgSlots =
    [
        new("Walk", "WalkSprite", 0, 16, 32),
        new("Run", "RunSprite", 9, 16, 32),
        new("Bike", "BikeSprite", 20, 32, 32, [new("MachBikeSprite", 32)]),
        // Seated surf pose: 3 real poses S/N/W. Combined reads surfing.png frames
        // 0/2/4; WriteFrameOrder pins the 3-frame target (flat 58..60 - the rest
        // of sPicTable_RedSurf aliases them).
        new("Sit", "SitSprite", 58, 16, 32,
            Combined: new("SurfingSprite", [0, 2, 4]), WriteFrameOrder: [0, 1, 2]),
        // FRLG's item/field-move sprite is 16px wide; RSE's field_move.png is 32px.
        new("Item", "ItemSprite", 82, 16, 32, [new("FieldMoveSprite", 32)], MaxFrames: 9),
        new("ItemBike", "ItemBikeSprite", 100, 32, 32, MaxFrames: 6),
        new("Fish", "FishSprite", 852, 32, 32),
    ];

    // Field-effect art at fixed ROM addresses (uncompressed, edited in place).
    // Tile offsets found by matching the decomp surf_blob.4bpp / bird.4bpp:
    //   surf blob: 32x32, 6 frames, row-major horizontal strip
    //   Fly bird : 64x64, 5 frames [0 bird-only, 1-2 Red, 3-4 Green], vertical
    private static Gen3RawTileSlot[] FrlgRawSlots(int surfBlobTiles, int birdTiles) =>
    [
        new("SurfBlob", "SurfBlobSprite", surfBlobTiles, 32, 32, 6, VerticalStrip: false),
        // 5 frames: 0 bird alone, 1-2 Red riding (toward / away), 3-4 Leaf riding. Packs reskin the male
        // player, so a 2-frame fly_bird.png (64x128) fills frames 1-2.
        new("Bird", "BirdSprite", birdTiles, 64, 64, 5, VerticalStrip: true, PartialFrames: 2, PartialStartFrame: 1),
    ];

    private static Revision Frlg(
        string id, int owImages, int owPalettes, int trainerFrontImages, int trainerBackImages, int mapIconImagePointer,
        int introTilesPointer, int introPaletteAddr, int surfBlobTiles, int birdTiles)
        => new(
            Id: id,
            OverworldSpriteImagesTable: owImages,
            OverworldPalettesTable: owPalettes,
            NormalPaletteIndex: 8,
            ReflectionPaletteIndex: 9,
            UnderwaterPaletteIndex: null,
            Front: new(trainerFrontImages + FrlgFrontTrainerIndex * 8,
                       trainerFrontImages + 0x4A0 + FrlgFrontTrainerIndex * 8, 8, 8),
            TrainerFrontImagesTable: trainerFrontImages,
            TrainerFrontPaletteRel: 0x4A0,
            Back: new(Compressed: false,
                      PointerLocation: trainerBackImages + FrlgBackTrainerIndex * 8,
                      PaletteEntry: trainerBackImages + 0x30 + FrlgBackTrainerIndex * 8, 8, 40),
            MapIcon: new(Compressed: true, ImageAddr: mapIconImagePointer, PaletteAddr: mapIconImagePointer + 0xC4),
            Intro: new(introTilesPointer, introPaletteAddr),
            FreeSpaceStart: 0x800000,
            RunSpriteOrder: "frlg",
            OverworldSlots: FrlgSlots,
            RawTileSlots: FrlgRawSlots(surfBlobTiles, birdTiles));

    // ---- RSE (Ruby / Sapphire / Emerald) ----
    private const int EmBrendanFrontTrainerIndex = 71;
    private const int RsBrendanFrontTrainerIndex = 0;
    private const int BrendanBackTrainerIndex = 0;

    // Brendan* overworld indices — constant across all RSE revisions
    // ("// TODO: move to constants" in the ini). Verified against retail Emerald.
    private static readonly Gen3OverworldSlot[] RseSlots =
    [
        new("Walk", "WalkSprite", 0, 16, 32),
        new("Run", "RunSprite", 9, 16, 32),
        new("Bike", "BikeSprite", 18, 32, 32, [new("MachBikeSprite", 32)], MaxFrames: 9),
        new("AcroBike", "AcroBikeSprite", 27, 32, 32, MaxFrames: 27),
        // SoulGold packs a single surfing.png (sit-S, jump-S, sit-N, jump-N,
        // sit-W, jump-W) - matches pokeemerald's native gObjectEventPic_*Surfing.
        // 3 real poses each; WriteFrameOrder pins the target (rest of the pic
        // table aliases these) and caps a stray sit_rse.png / jump.png.
        new("Sit", "SitSprite", 54, 32, 32,
            Combined: new("SurfingSprite", [0, 2, 4]), WriteFrameOrder: [0, 1, 2]),
        new("SitJump", "SitJumpSprite", 63, 32, 32,
            Combined: new("SurfingSprite", [1, 3, 5]), WriteFrameOrder: [0, 1, 2]),
        // 3 real poses (S/N/W); flat 66..68. Flat 69-74 alias them, so a 4th
        // pack frame (E) written straight would clobber S. E is game-flipped W.
        new("Underwater", "UnderwaterSprite", 66, 32, 32, WriteFrameOrder: [0, 1, 2]),
        // FRLG calls this same "raise a Pokemon / hold item overhead" sprite Item
        // and stores it 16px wide (vs RSE's 32px).
        new("FieldMove", "FieldMoveSprite", 75, 32, 32, [new("ItemSprite", 16)], MaxFrames: 5),
        new("Fish", "FishSprite", 1016, 32, 32),
        // sPicTable_BrendanWatering = [0,2,4,1,1,3,3,5,5]. Pack sheet is natural
        // src order [0..5]; route pack frame k to the first flat entry for src k
        // (src0@0 src1@3 src2@1 src3@5 src4@2 src5@7).
        new("WateringCan", "WateringCanSprite", 1139, 32, 32, WriteFrameOrder: [0, 3, 1, 5, 2, 7]),
        new("Decorate", "DecorateSprite", 1157, 16, 32),
    ];

    private static Revision Rse(
        string id, bool emerald, int owImages, int owPalettes,
        int trainerFrontImages, int trainerBackImages, int mapIconImage,
        int normalPal, int reflectPal, int underwaterPal,
        int trainerFrontPalRel, int trainerBackPalRel, int freeSpace, bool backCompressed)
    {
        var frontIdx = emerald ? EmBrendanFrontTrainerIndex : RsBrendanFrontTrainerIndex;
        return new(
            Id: id,
            OverworldSpriteImagesTable: owImages,
            OverworldPalettesTable: owPalettes,
            NormalPaletteIndex: normalPal,
            ReflectionPaletteIndex: reflectPal,
            UnderwaterPaletteIndex: underwaterPal,
            Front: new(trainerFrontImages + frontIdx * 8,
                       trainerFrontImages + trainerFrontPalRel + frontIdx * 8, 8, 8),
            TrainerFrontImagesTable: trainerFrontImages,
            TrainerFrontPaletteRel: trainerFrontPalRel,
            Back: new(Compressed: backCompressed,
                      PointerLocation: trainerBackImages + BrendanBackTrainerIndex * 8,
                      PaletteEntry: trainerBackImages + trainerBackPalRel + BrendanBackTrainerIndex * 8, 8, 32),
            MapIcon: new(Compressed: false, ImageAddr: mapIconImage, PaletteAddr: mapIconImage - 0x20),
            Intro: null,
            FreeSpaceStart: freeSpace,
            RunSpriteOrder: "rse",
            OverworldSlots: RseSlots,
            RawTileSlots: []);
    }

    public static readonly Revision FireRedU10 =
        Frlg("firered-u-1.0", 0x3A00A0, 0x3A5158, 0x23957C, 0x239FA4, 0xC423C,
            introTilesPointer: 0x13122C, introPaletteAddr: 0x4615FC,
            surfBlobTiles: 0x396B08, birdTiles: 0x39D3C8);

    public static readonly Revision LeafGreenU10 =
        Frlg("leafgreen-u-1.0", 0x3A0080, 0x3A5138, 0x239558, 0x239F80, 0xC4210,
            introTilesPointer: 0x131204, introPaletteAddr: 0x46101C,
            surfBlobTiles: 0x396AE8, birdTiles: 0x39D3A8);

    public static readonly Revision EmeraldU =
        Rse("emerald-u", emerald: true, 0x505A8C, 0x50BBC8, 0x305654, 0x305D4C, 0x5A084C,
            normalPal: 8, reflectPal: 9, underwaterPal: 11,
            trainerFrontPalRel: 0x2E8, trainerBackPalRel: 0x40, freeSpace: 0xE40000, backCompressed: false);

    public static readonly Revision RubyU =
        Rse("ruby-u", emerald: false, 0x36E050, 0x37377C, 0x1EC53C, 0x1ECAE4, 0x3E5C40,
            normalPal: 8, reflectPal: 9, underwaterPal: 11,
            trainerFrontPalRel: 0x298, trainerBackPalRel: 0x18, freeSpace: 0x700000, backCompressed: true);

    public static readonly Revision SapphireU =
        Rse("sapphire-u", emerald: false, 0x36DFE0, 0x37370C, 0x1EC4CC, 0x1ECA74, 0x3E5C98,
            normalPal: 8, reflectPal: 9, underwaterPal: 11,
            trainerFrontPalRel: 0x298, trainerBackPalRel: 0x18, freeSpace: 0x700000, backCompressed: true);

    // Rev 2 (the commonly-circulated "V1.1"/"V1.2" dumps, software version byte
    // 2 in the header) - same table STRUCTURE as Rev 0, just at different
    // absolute addresses. Verified by resolving each Rev 0 address to its
    // decomp symbol (gTrainerFrontPicTable, gTrainerBackPicTable,
    // gObjectEventPicTable_BrendanNormal, sObjectEventSpritePalettes,
    // sRegionMapBrendanIcon_Image) against a clean pokeruby/pokesapphire
    // decomp build, then re-resolving those same symbols in a REVISION=2
    // build.
    public static readonly Revision RubyU2 =
        Rse("ruby-u-rev2", emerald: false, 0x36E068, 0x373794, 0x1EC554, 0x1ECAFC, 0x3E5C5C,
            normalPal: 8, reflectPal: 9, underwaterPal: 11,
            trainerFrontPalRel: 0x298, trainerBackPalRel: 0x18, freeSpace: 0x700000, backCompressed: true);

    public static readonly Revision SapphireU2 =
        Rse("sapphire-u-rev2", emerald: false, 0x36DFF8, 0x373724, 0x1EC4E4, 0x1ECA8C, 0x3E5CB8,
            normalPal: 8, reflectPal: 9, underwaterPal: 11,
            trainerFrontPalRel: 0x298, trainerBackPalRel: 0x18, freeSpace: 0x700000, backCompressed: true);

    // KNOWN LIMITATION (Ruby/Sapphire only): these tables are vanilla addresses.
    // A *visiting* launch passes the Connect base-patched ROM here, not the
    // vanilla one - and unlike Emerald/FireRed/LeafGreen (whose decomps have a
    // safe "extra" linker section, so their base patch shifts nothing else),
    // R/S's base patch shifts most of the ROM's layout (see BpsPatchService's
    // doc comment), moving these exact tables too. The header (game
    // code+revision) is unchanged by the patch, so ForRom can't tell "vanilla"
    // apart from "base-patched" to pick a different table - it currently
    // always resolves to the vanilla addresses, so player/champion FRONT and
    // OVERWORLD graphics edits silently no-op ("Unsupported/non-vanilla Gen 3
    // ROM revision") during a visiting R/S launch. Home-region launches
    // (vanilla ROM, no base patch) are unaffected. Fixing this needs the
    // base-patched addresses resolved from the base patch's own manifest
    // (like ChampionOverworldOffsets.FromBasePatchManifest already does for
    // the champion placeholder's overworld sprite) rather than this hardcoded
    // table, extended to cover the player's own graphics too - not done yet.
    public static Revision? ForRom(byte[] rom)
    {
        if (rom.Length < 0xC0)
        {
            return null;
        }
        var code = System.Text.Encoding.ASCII.GetString(rom, 0xAC, 4);
        var version = rom[0xBC];
        var rev = (code, version) switch
        {
            ("BPRE", 0) => FireRedU10,
            ("BPGE", 0) => LeafGreenU10,
            ("BPEE", 0) => EmeraldU,
            ("AXVE", 0) => RubyU,
            ("AXPE", 0) => SapphireU,
            ("AXVE", 2) => RubyU2,
            ("AXPE", 2) => SapphireU2,
            _ => (Revision?)null,
        };

        // The game code matches a vanilla revision, but a romhack keeps the code
        // while moving everything. Sanity-check that our tables actually hold
        // sane pointers before trusting the offsets.
        return rev is not null && LayoutLooksVanilla(rom, rev) ? rev : null;
    }

    private static bool LayoutLooksVanilla(byte[] rom, Revision rev)
    {
        if (Gba.ReadPointer(rom, rev.OverworldPalettesTable + rev.NormalPaletteIndex * 8) < 0
            || Gba.ReadPointer(rom, rev.OverworldPalettesTable + rev.ReflectionPaletteIndex * 8) < 0
            || Gba.ReadPointer(rom, rev.Front.PointerEntry) is var frontOff && frontOff < 0
            || rom[frontOff] != 0x10) // front image is LZ10 on every supported revision
        {
            return false;
        }

        if (rev.Intro is { } intro
            && (Gba.ReadPointer(rom, intro.TilesPointerLocation) is var introOff && introOff < 0
                || rom[introOff] != 0x10))
        {
            return false;
        }

        var walk = rev.OverworldSlots[0];
        var entry = rev.OverworldSpriteImagesTable + walk.StartIndex * 8;
        var walkOff = Gba.ReadPointer(rom, entry);
        return walkOff >= 0
            && (int)Gba.ReadU32(rom, entry + 4) == walk.FrameByteLength
            && walkOff + walk.FrameByteLength <= rom.Length;
    }
}
