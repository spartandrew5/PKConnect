using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PKConnect.Core.nds;

/// <summary>
/// Composites an NCER ("RECN" on disk) cell bank's OAM parts onto a canvas -
/// what HGSS's front/back battle sprites actually need instead of
/// NcgrSpriteCodec's flat tile-major decode: each "bank" (one animation
/// frame) is 6 separately-sized, separately-positioned pieces (body, cap,
/// arm, ...) sharing one NCGR tile pool, not one contiguous image. Read-only
/// (view/extract), not a write path - see the session notes on why real
/// injection needs more than this.
///
/// Layout, reverse-engineered against real HG bytes (front/back sprites both
/// use tBank=1, nCells=6 per bank; see NdsSpritePipelineTests history):
///   [0x00 outer header, 16B][0x10 CEBK: id,size,nBanks,tBank,bank_data_offset,
///     block_size,partition_data_offset,unused(8B)][banks, 8 or 16B each]
///     [OAM pool: 3x u16 per cell, banks back-to-back]
/// OAM attribute bits are the standard GBA/NDS ones (obj0/obj1/obj2).
/// </summary>
public static class NcerCompositor
{
    private const int TileSize = 8;
    private const int BytesPerTile = 32; // 4bpp

    private static readonly (int W, int H)[,] OamSizeTable =
    {
        { (8, 8), (16, 16), (32, 32), (64, 64) },   // shape 0: square
        { (16, 8), (32, 8), (32, 16), (64, 32) },   // shape 1: horizontal
        { (8, 16), (8, 32), (16, 32), (32, 64) },   // shape 2: vertical
    };

    /// <param name="DoubleSize">
    /// Affine OAM with the double-size bit: the sprite is drawn centred in a 2W x 2H box at
    /// (XOffset, YOffset), so its real top-left is (XOffset + W/2, YOffset + H/2). Gen 5 back sprites
    /// use it for the rotated follow-through pose. Not applied by <see cref="CompositeBank"/>.
    /// </param>
    public readonly record struct Cell(int Width, int Height, int TileOffset, int XOffset, int YOffset, int Priority, bool Disabled, bool DoubleSize = false);

    /// <summary>
    /// A bank's cells plus its own base byte offset into the shared NCGR tile
    /// data (0 when the file has no per-bank partition table - see
    /// <see cref="ReadBanks"/>) and the CEBK-wide <see cref="BlockSize"/>
    /// shift. Real tile byte address = NCGR data start + PartitionBaseOffset
    /// + (cell.TileOffset &lt;&lt; BlockSize) * 32 - ported from Tinke's own
    /// import code (SpriteControl.cs Import_File: "tileOffset = (tileOffset
    /// &lt;&lt; BlockSize) * 0x20 + bank.data_offset"), which is the actual
    /// authoritative formula; a naive tileOffset*32 with no shift is only
    /// correct when BlockSize happens to be 0.
    /// </summary>
    /// <summary>
    /// The "extended" (tBank==1) per-bank bounding box. Some banks in a
    /// multi-frame animation shift this box sideways/vertically relative to
    /// bank 0 (checked against a real HG back sprite: frames 0-2 sit at
    /// xMin=-40..xMax=39, frames 3-7 shift to xMin=-24..xMax=55, same width)
    /// while still rendering in the same on-screen position in-game - the
    /// real renderer re-centres each bank's box rather than trusting OAM
    /// offset (0,0) to always mean "canvas centre". Null when tBank==0.
    /// </summary>
    public readonly record struct Bounds(int XMin, int XMax, int YMin, int YMax);

    public readonly record struct Bank(List<Cell> Cells, int PartitionBaseOffset, int BlockSize, Bounds? Bounds);

    /// <summary>
    /// Reads every bank's cell list from an NCER file. When the CEBK has a
    /// per-bank "partition" table (ported from Tinke's NCER.cs - most
    /// multi-frame animated sprites do), each bank gets its own base offset
    /// into the NCGR tile pool, which is what actually makes banks reference
    /// different art instead of all landing on the same tiles.
    /// </summary>
    public static List<Bank> ReadBanks(byte[] ncer)
    {
        if (Tag(ncer, 0) != "RECN") throw new InvalidDataException("Not an NCER file.");
        var cebkOffset = ReadU16(ncer, 0x0C);
        if (Tag(ncer, cebkOffset) != "KBEC") throw new InvalidDataException("Expected a CEBK (KBEC) section.");

        var nBanks = ReadU16(ncer, cebkOffset + 8);
        var tBank = ReadU16(ncer, cebkOffset + 10);
        var bankDataOffset = (int)ReadU32(ncer, cebkOffset + 12);
        var blockSize = (int)(ReadU32(ncer, cebkOffset + 16) & 0xFF);
        var partitionDataOffset = (int)ReadU32(ncer, cebkOffset + 20);

        var partitionBaseOffsets = new int[nBanks];
        if (partitionDataOffset != 0)
        {
            var partHeaderPos = cebkOffset + 8 + partitionDataOffset;
            var firstPartitionDataOffset = (int)ReadU32(ncer, partHeaderPos + 4);
            var partTableStart = partHeaderPos + firstPartitionDataOffset;
            for (var b = 0; b < nBanks; b++)
            {
                partitionBaseOffsets[b] = (int)ReadU32(ncer, partTableStart + b * 8);
            }
        }

        var bankHeaderSize = tBank == 0 ? 8 : 16;
        var banksStart = cebkOffset + 8 + bankDataOffset;
        var oamPoolStart = banksStart + nBanks * bankHeaderSize;

        var banks = new List<Bank>();
        for (var b = 0; b < nBanks; b++)
        {
            var bankOffset = banksStart + b * bankHeaderSize;
            var nCells = ReadU16(ncer, bankOffset);
            var cellOffset = (int)ReadU32(ncer, bankOffset + 4);
            Bounds? bounds = null;
            if (tBank == 1)
            {
                bounds = new Bounds(
                    XMin: (short)ReadU16(ncer, bankOffset + 12),
                    XMax: (short)ReadU16(ncer, bankOffset + 8),
                    YMin: (short)ReadU16(ncer, bankOffset + 14),
                    YMax: (short)ReadU16(ncer, bankOffset + 10));
            }

            var cells = new List<Cell>();
            var oamArrayOffset = oamPoolStart + cellOffset;
            for (var c = 0; c < nCells; c++)
            {
                var obj0 = ReadU16(ncer, oamArrayOffset + c * 6);
                var obj1 = ReadU16(ncer, oamArrayOffset + c * 6 + 2);
                var obj2 = ReadU16(ncer, oamArrayOffset + c * 6 + 4);

                var shape = (obj0 >> 14) & 3;
                var size = (obj1 >> 14) & 3;
                var (w, h) = OamSizeTable[shape, size];

                var yOffset = (sbyte)(obj0 & 0xFF);
                var xOffsetRaw = obj1 & 0x1FF;
                var xOffset = xOffsetRaw >= 0x100 ? xOffsetRaw - 0x200 : xOffsetRaw;

                var tileOffset = obj2 & 0x3FF;
                var priority = (obj2 >> 10) & 3;

                // rs_flag (obj0 bit 8) selects rotation/scale mode, in which
                // case bit 9 is doubleSize instead of objDisable - only treat
                // bit 9 as "hide this OAM" in the plain (non-affine) mode.
                var rsFlag = (obj0 >> 8) & 1;
                var disabled = rsFlag == 0 && ((obj0 >> 9) & 1) == 1;

                var doubleSize = rsFlag == 1 && ((obj0 >> 9) & 1) == 1;
                cells.Add(new Cell(w, h, tileOffset, xOffset, yOffset, priority, disabled, doubleSize));
            }
            banks.Add(new Bank(cells, partitionBaseOffsets[b], blockSize, bounds));
        }
        return banks;
    }

    /// <summary>
    /// Composites one bank onto a <paramref name="canvasSize"/> canvas, tiles
    /// pool taken from an already-parsed NcgrSpriteCodec tile buffer. Origin
    /// (where OAM offset (0,0) lands) is the canvas centre, which is the
    /// standard NDS sprite convention and matches what these HG sprites
    /// produce visually. Draws every non-disabled cell - the earlier
    /// "exclude the right-hand column" workaround was masking the real bug
    /// (missing BlockSize shift, see Bank's doc comment), not a genuinely
    /// bad set of cells.
    /// </summary>
    public static Image<Rgba32> CompositeBank(
        byte[] ncgr, Rgba32[] palette, Bank bank, int canvasSize)
    {
        var canvas = new Image<Rgba32>(canvasSize, canvasSize);
        var originX = canvasSize / 2;
        var originY = canvasSize / 2;
        if (bank.Bounds is { } b)
        {
            // Re-centre this bank's bounding box on the canvas instead of
            // trusting OAM offset (0,0) to always land there - some banks in
            // a multi-frame animation shift the whole box sideways while
            // still displaying in the same on-screen spot in-game. "+1" before
            // halving rounds the odd-sum case up instead of C#'s default
            // truncate-toward-zero, which otherwise left a 1px rightward
            // drift on frames whose box sum is positive-odd (checked against
            // a real back sprite pack: bank0's box sums to -1 and was
            // already correct either way, bank3/4's sums to 31 and needed
            // the extra +1 to land in the right place).
            originX -= (b.XMin + b.XMax + 1) / 2;
            originY -= (b.YMin + b.YMax + 1) / 2;
        }

        foreach (var cell in bank.Cells.Where(c => !c.Disabled))
        {
            using var piece = RenderCellStandalone(ncgr, palette, cell, bank.PartitionBaseOffset, bank.BlockSize);
            var destX = originX + cell.XOffset;
            var destY = originY + cell.YOffset;
            canvas.Mutate(ctx => ctx.DrawImage(piece, new Point(destX, destY), 1f));
        }

        return canvas;
    }

    /// <summary>Renders one cell's own tile data in isolation, ignoring its canvas position - for diagnostics.</summary>
    public static Image<Rgba32> RenderCellStandalone(
        byte[] ncgr, Rgba32[] palette, Cell cell, int partitionBaseOffset = 0, int blockSize = 0)
    {
        var ch = NcgrSpriteCodec.ReadNcgrHeader(ncgr);
        var piece = new Image<Rgba32>(cell.Width, cell.Height);
        var tilesWide = cell.Width / TileSize;
        var tilesTall = cell.Height / TileSize;
        var baseOffset = ch.DataOffset + partitionBaseOffset + (cell.TileOffset << blockSize) * BytesPerTile;

        for (var ty = 0; ty < tilesTall; ty++)
        {
            for (var tx = 0; tx < tilesWide; tx++)
            {
                var tileIndex = ty * tilesWide + tx;
                var tileStart = baseOffset + tileIndex * BytesPerTile;
                if (tileStart + BytesPerTile > ncgr.Length) continue;

                for (var row = 0; row < TileSize; row++)
                {
                    for (var col = 0; col < TileSize; col += 2)
                    {
                        var b = ncgr[tileStart + row * 4 + col / 2];
                        var px = tx * TileSize + col;
                        var py = ty * TileSize + row;
                        piece[px, py] = palette[b & 0xF];
                        piece[px + 1, py] = palette[b >> 4];
                    }
                }
            }
        }
        return piece;
    }

    /// <summary>
    /// Maps each bank index to a 0-based "unique frame" group index, collapsing
    /// any run of consecutive banks that composite to byte-identical pixels
    /// (against the tile/palette data <paramref name="ncgr"/>/<paramref name="palette"/>
    /// already reference) onto the same group as the first bank in that run -
    /// the same rule <see cref="Gen4PlayerGraphicsExtractor"/> uses to collapse
    /// trailing hold-frames when building a pack's sprite strip. Both the
    /// extractor (to know which composited frame becomes the Nth strip image)
    /// and a writer reconstructing per-bank data from an already-collapsed
    /// strip need the exact same grouping, so it lives here once.
    /// </summary>
    public static int[] GroupDuplicateBanks(byte[] ncgr, Rgba32[] palette, List<Bank> banks, int canvasSize)
    {
        var groups = new int[banks.Count];
        var nextGroup = -1;
        Image<Rgba32>? previous = null;
        for (var b = 0; b < banks.Count; b++)
        {
            using var frame = CompositeBank(ncgr, palette, banks[b], canvasSize);
            if (previous is not null && ImagesEqual(previous, frame))
            {
                groups[b] = nextGroup;
            }
            else
            {
                nextGroup++;
                groups[b] = nextGroup;
                previous?.Dispose();
                previous = frame.Clone();
            }
        }
        previous?.Dispose();
        return groups;
    }

    private static bool ImagesEqual(Image<Rgba32> a, Image<Rgba32> b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                if (a[x, y] != b[x, y]) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Every raw palette index (0-15) this NCGR's own cells actually reference
    /// across all of <paramref name="banks"/> - confirmed necessary for
    /// data/field_cutin.narc's hero/heroine sprites, which only use 14 of 16
    /// slots (2 reserved, presumably for a companion object - the flying bird
    /// or clouds - sharing the same OBJ palette bank on real hardware).
    /// Rebuilding a full 16-colour palette from a pack's own art alone would
    /// silently repurpose those reserved slots with whatever colour happens
    /// to quantize there, corrupting the OTHER object's colours even though
    /// this sprite's own tiles never touch them - see TryCompositeSlot, which
    /// uses this to leave untouched indices' original ROM colours alone.
    /// </summary>
    public static HashSet<int> ComputeUsedIndices(byte[] ncgr, List<Bank> banks)
    {
        var ch = NcgrSpriteCodec.ReadNcgrHeader(ncgr);
        var used = new HashSet<int>();
        foreach (var bank in banks)
        {
            foreach (var cell in bank.Cells.Where(c => !c.Disabled))
            {
                var tilesWide = cell.Width / TileSize;
                var tilesTall = cell.Height / TileSize;
                var baseOffset = ch.DataOffset + bank.PartitionBaseOffset + (cell.TileOffset << bank.BlockSize) * BytesPerTile;
                for (var ty = 0; ty < tilesTall; ty++)
                {
                    for (var tx = 0; tx < tilesWide; tx++)
                    {
                        var tileIndex = ty * tilesWide + tx;
                        var tileStart = baseOffset + tileIndex * BytesPerTile;
                        if (tileStart + BytesPerTile > ncgr.Length) continue;
                        for (var row = 0; row < TileSize; row++)
                        {
                            for (var col = 0; col < TileSize; col += 2)
                            {
                                var b = ncgr[tileStart + row * 4 + col / 2];
                                used.Add(b & 0xF);
                                used.Add(b >> 4);
                            }
                        }
                    }
                }
            }
        }
        return used;
    }

    /// <summary>
    /// Inverse of <see cref="CompositeBank"/>: crops <paramref name="frame"/>
    /// (one canvasSize-square strip slice, already selected via
    /// <see cref="GroupDuplicateBanks"/>) back into each of the bank's cells'
    /// own tile range in <paramref name="ncgr"/>, quantizing against
    /// <paramref name="resolveIndex"/> (a caller-built palette lookup - the
    /// palette itself is written separately since it's shared across every
    /// bank, not per-cell). A destination pixel that falls outside
    /// <paramref name="frame"/>'s bounds (a cell placed partly off-canvas)
    /// writes palette index 0, the hardware's own sprite-transparent slot.
    /// </summary>
    public static void EncodeBank(
        byte[] ncgr, Func<Rgba32, int> resolveIndex, Image<Rgba32> frame, Bank bank, int canvasSize)
    {
        var ch = NcgrSpriteCodec.ReadNcgrHeader(ncgr);
        var originX = canvasSize / 2;
        var originY = canvasSize / 2;
        if (bank.Bounds is { } b)
        {
            originX -= (b.XMin + b.XMax + 1) / 2;
            originY -= (b.YMin + b.YMax + 1) / 2;
        }

        foreach (var cell in bank.Cells.Where(c => !c.Disabled))
        {
            var destX = originX + cell.XOffset;
            var destY = originY + cell.YOffset;
            var baseOffset = ch.DataOffset + bank.PartitionBaseOffset + (cell.TileOffset << bank.BlockSize) * BytesPerTile;
            var tilesWide = cell.Width / TileSize;
            var tilesTall = cell.Height / TileSize;

            for (var ty = 0; ty < tilesTall; ty++)
            {
                for (var tx = 0; tx < tilesWide; tx++)
                {
                    var tileIndex = ty * tilesWide + tx;
                    var tileStart = baseOffset + tileIndex * BytesPerTile;
                    if (tileStart + BytesPerTile > ncgr.Length) continue;

                    for (var row = 0; row < TileSize; row++)
                    {
                        for (var col = 0; col < TileSize; col += 2)
                        {
                            var px = destX + tx * TileSize + col;
                            var py = destY + ty * TileSize + row;

                            int ResolvePixel(int dx)
                            {
                                var x = px + dx;
                                if (x < 0 || x >= frame.Width || py < 0 || py >= frame.Height) return 0;
                                return resolveIndex(frame[x, py]);
                            }

                            var lo = ResolvePixel(0);
                            var hi = ResolvePixel(1);
                            ncgr[tileStart + row * 4 + col / 2] = (byte)(lo | (hi << 4));
                        }
                    }
                }
            }
        }
    }

    private static string Tag(byte[] data, int offset) => System.Text.Encoding.ASCII.GetString(data, offset, 4);
    private static uint ReadU32(byte[] data, int offset) => BitConverter.ToUInt32(data, offset);
    private static ushort ReadU16(byte[] data, int offset) => BitConverter.ToUInt16(data, offset);
}
