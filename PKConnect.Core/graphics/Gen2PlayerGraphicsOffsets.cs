namespace PKConnect.Core.graphics;

/// <summary>
/// Static ROM offsets for the Gen 2 (Crystal) player sprites. Values ported from
/// UPR-FVX's <c>gen2_offsets.ini</c> [Crystal (U)], PC1 ("Chris") slots. Covers
/// walk / bike / fish / front / trainer-card (in place), the LZ3 back image
/// (relocated), and the sprite + trainer-class palettes.
/// </summary>
public static class Gen2PlayerGraphicsOffsets
{
    public sealed record Revision(
        Gen1SpriteSlot Walk,
        Gen1SpriteSlot Bike,
        Gen1SpriteSlot Fish,
        Gen1SpriteSlot Front,
        Gen1SpriteSlot TrainerCard,
        int BackTiles,
        int BackPrimaryPointerSite,
        int[] BackSecondaryPointerSites,
        int[] BackBankSites,
        int[] UnusedBanks,
        int TrainerPalettesPointerSite,
        int TrainerPalettesPointerBank,
        int PlayerTrainerClass,
        int[] SpritePaletteByteOffsets,
        int[] OwSpriteTablePaletteOffsets);

    // Walk / Bike are 16x96 (2x12 tiles). Fish is one 24x16 image (3x2 tiles):
    // the fishing front / back / side poses stitched left-to-right, row-major.
    // Front (7x7) and trainer-card (5x7) images are uncompressed and stored
    // column-major on Crystal (writeImage). Verified by decoding each offset.
    public static readonly Revision Crystal = new(
        Walk: new("Walk", 0xC0000, 2, 12),
        Bike: new("Bike", 0xC0180, 2, 12),
        Fish: new("Fish", 0xB84F2, 3, 2),
        Front: new("FrontImage", 0x888A9, 7, 7),
        TrainerCard: new("TrainerCardImage", 0x88365, 5, 7),
        // Back image (6x6) is LZ3-compressed. ChrisBackImagePointers = [0x3FC23,
        // 0x88831]; the bank bytes are derived exactly as UPR-FVX's
        // Gen2RomHandler.initRomEntry (Crystal: primary-2, secondary+3).
        // Verified against retail Crystal (both words 0x7A1A, both bank bytes 0x0A).
        BackTiles: 6,
        BackPrimaryPointerSite: 0x3FC23,
        BackSecondaryPointerSites: [0x88831],
        BackBankSites: [0x3FC23 - 2, 0x88831 + 3],
        UnusedBanks: [0x75, 0x76, 0x79, 0x7A],
        // TrainerPalettesPointers[0] (in bank 2) points at the trainer-class
        // palette table; the player ("Chris") is class 0. Verified: the pointer
        // at 0x9771 resolves to the table and class 0 is Chris's skin palette.
        TrainerPalettesPointerSite: 0x9771,
        TrainerPalettesPointerBank: 2,
        PlayerTrainerClass: 0,
        // The player's overworld palette is set two ways, and we patch both:
        //  1. SpawnPlayer's hardcoded PAL_NPC_* immediates - one per gender:
        //     ChrisSpritePalette (0x8048) and KrisSpritePalette (+0x10, per
        //     UPR-FVX's krisSpritePaletteOffset). Value = (ordinal|8)<<4.
        //  2. The OverworldSprites data table's default PAL_OW_* index for the
        //     Chris walk / bike sprites (found at 0x1473B / 0x14741 on Crystal U;
        //     vanilla PAL_OW_RED = 0). Value = ordinal (0-7).
        SpritePaletteByteOffsets: [0x8048, 0x8048 + 0x10],
        OwSpriteTablePaletteOffsets: [0x1473B, 0x14741]);

    /// <summary>
    /// Vanilla values at each <see cref="Revision.SpritePaletteByteOffsets"/>
    /// entry — the <c>ln e, PAL_NPC_RED</c> / <c>PAL_NPC_BLUE</c> immediates in
    /// SpawnPlayer. If the ROM doesn't have these, it isn't retail Crystal (U)
    /// and the overworld-tint write is skipped rather than corrupting code.
    /// </summary>
    public static readonly byte[] CrystalSpritePaletteVanilla = [0x80, 0x90];

    /// <summary>Vanilla PAL_OW_* index at each OverworldSprites table slot (both PAL_OW_RED = 0).</summary>
    public static readonly byte[] CrystalOwSpriteTableVanilla = [0x00, 0x00];

    /// <summary>PAL_OW_* index (0-7) for an info.ini SpritePalette name, or null.</summary>
    public static byte? SpritePaletteOwIndex(string? name) => name?.Trim().ToUpperInvariant() switch
    {
        "RED" => 0,
        "BLUE" => 1,
        "GREEN" => 2,
        "BROWN" => 3,
        "PINK" => 4,
        "EMOTE" => 5,
        "TREE" => 6,
        "ROCK" => 7,
        _ => null,
    };

    public static Revision? ForGame(string gameId) => gameId switch
    {
        "crystal" => Crystal,
        _ => null,
    };

    /// <summary>
    /// The byte written at <see cref="Revision.SpritePaletteByteOffset"/> for an
    /// info.ini <c>SpritePalette=</c> value, per UPR-FVX
    /// (<c>(ordinal | 0b1000) &lt;&lt; 4</c>). Null for an unknown name.
    /// </summary>
    public static byte? SpritePaletteByte(string? name)
    {
        var ordinal = name?.Trim().ToUpperInvariant() switch
        {
            "RED" => 0,
            "BLUE" => 1,
            "GREEN" => 2,
            "BROWN" => 3,
            "PINK" => 4,
            "EMOTE" => 5,
            "TREE" => 6,
            "ROCK" => 7,
            _ => -1,
        };
        return ordinal < 0 ? null : (byte)((ordinal | 0b1000) << 4);
    }
}
