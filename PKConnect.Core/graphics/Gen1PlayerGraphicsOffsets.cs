namespace PKConnect.Core.graphics;

/// <summary>
/// One overworld sprite slot: a fixed-size, uncompressed 2bpp tile block written
/// directly over a static ROM offset (no repointing).
/// </summary>
public record Gen1SpriteSlot(string Name, int Offset, int TilesWide, int TilesTall)
{
    public int ByteLength => TilesWide * TilesTall * GbTileCodec.BytesPerTile;
}

/// <summary>
/// Static ROM offsets for the Gen 1 player overworld sprites, per cartridge
/// revision. Values ported from UPR-FVX's <c>gen1_offsets.ini</c> and verified
/// byte-exact by decoding the vanilla sprite at each offset.
///
/// Only the launcher's patchable Gen 1 game (<c>yellow</c>) is wired up; Red/Blue
/// offsets are kept here for the compressed-battle-sprite work (M3) and tests.
/// </summary>
public static class Gen1PlayerGraphicsOffsets
{
    // Walk / Bike are 16x96 (2x12 tiles = 6 stacked 16x16 frames).
    // Fish is 4 consecutive 8x16 poses (front, back, side, rod) at PlayerFishSprite.
    public sealed record Revision(
        Gen1SpriteSlot Walk,
        Gen1SpriteSlot Bike,
        int FishOffset,
        int FrontTiles,
        int[] FrontPointerSites,
        int[] FrontBankSites,
        int BackTiles,
        int BackPrimaryPointerSite,
        int[] BackSecondaryPointerSites,
        int[] BackBankSites,
        int OldManPointerSite,
        int OakPointerSite,
        int[] UnusedBanks,
        int[] ReservedBanks)
    {
        public Gen1SpriteSlot FishFront => new("FishFront", FishOffset, 1, 2);
        public Gen1SpriteSlot FishBack => new("FishBack", FishOffset + 32, 1, 2);
        public Gen1SpriteSlot FishSide => new("FishSide", FishOffset + 64, 1, 2);

        /// <summary>-1 on non-Yellow revisions (no Prof. Oak back image).</summary>
        public bool HasOakBackImage => OakPointerSite >= 0;
    }

    // Front-image pointer/bank sites are derived exactly as UPR-FVX's
    // Gen1RomHandler.addPlayerFrontImagePointersToRomEntry does: the 5 ini
    // pointers plus a 6th at pointer[1]+54, and 6 bank bytes at the derived
    // per-pointer offsets. Verified against retail Yellow (all 6 pointer sites
    // read 0x5A97, all 6 bank bytes read 0x04).
    private static readonly int[] YellowFrontPtrs = BuildFrontPtrs(0x3598, 0x5EFB, 0x6710, 0x1204D, 0x70391);
    private static readonly int[] YellowFrontBanks = BuildFrontBanks(0xF616B, YellowFrontPtrs);

    // Back-image sites, derived from the ini's PlayerBackImagePointers exactly as
    // UPR-FVX's addPlayerBackImageBankOffsetsToRomEntry / addOldManBackImagePointerToRomEntry
    // / addOakBackImagePointerToRomEntry do for Yellow (isYellow: backOffset0=3,
    // offset1=3, oldMan=-14, oak=-7). Verified against retail Yellow (both
    // pointer sites read 0x43B1, both bank bytes 0x3D, player/oldman/oak sprites
    // sit contiguously from 0xF43B1).
    private const int YellowBackPrimary = 0x3EE2A;
    private const int YellowBackSecondary = 0x703B3;

    private static int[] BuildFrontPtrs(int p0, int p1, int p2, int p3, int p4) =>
        [p0, p1, p2, p3, p4, p1 + 54];

    private static int[] BuildFrontBanks(int b0, int[] ptrs) =>
        [b0, ptrs[1] + 4, ptrs[2] + 3, ptrs[3] + 4, ptrs[4] + 3, ptrs[5] + 4];

    public static readonly Revision Yellow = new(
        Walk: new("Walk", 0x14571, 2, 12),
        Bike: new("Bike", 0x143F1, 2, 12),
        FishOffset: 0x7A19A,
        FrontTiles: 7,
        FrontPointerSites: YellowFrontPtrs,
        FrontBankSites: YellowFrontBanks,
        BackTiles: 4,
        BackPrimaryPointerSite: YellowBackPrimary,
        BackSecondaryPointerSites: [YellowBackSecondary],
        BackBankSites: [YellowBackPrimary + 3, YellowBackSecondary + 3],
        OldManPointerSite: YellowBackPrimary - 14,
        OakPointerSite: YellowBackPrimary - 7,
        UnusedBanks: [0x3B],
        ReservedBanks: [0x00]);

    /// <summary>The launcher's Gen 1 game id is always "yellow".</summary>
    public static Revision? ForGame(string gameId) => gameId switch
    {
        "yellow" => Yellow,
        _ => null,
    };
}
