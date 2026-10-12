using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

using PKConnect.Core.patching;

namespace PKConnect.Core.graphics;

/// <summary>
/// FireRed / LeafGreen draw Red's field-move pose (sPicTable_RedItem) and surf sit pose (sPicTable_RedSurf) as
/// 16x32 sprites, so a pack's 32px-wide RSE art (field_move.png, surf.png) would be centre-cropped into those
/// slots - e.g. the raised Poké Ball gets cut off. When a pack has the 32px art and no FRLG-width version, this
/// writes the 32x32 frames to free space, builds a new pic table for them and switches
/// gObjectEventGraphicsInfo_RedFieldMove / _RedSurf to 32x32 (size, width, height, oam, subspriteTables, images)
/// - the same shape gObjectEventGraphicsInfo_RedFish already has, whose oam / subsprite pointers are reused.
/// The VS Seeker graphics share sPicTable_RedItem but have their own graphics info, so they keep the 16px pics.
/// </summary>
public static class Gen3FrlgWidePoses
{
    private const int GbaRom = 0x08000000;
    private const int InfoSize = 0x24;
    private const int OffSize = 0x06, OffWidth = 0x08, OffHeight = 0x0A, OffOam = 0x10, OffSubsprites = 0x14, OffImages = 0x1C;
    private const int FrameBytes = 32 * 32 / 2;

    // Flat indices into the overworld image table (see Gen3PlayerGraphicsOffsets.FrlgSlots).
    private const int FlatNormal = 0, FlatSurf = 58, FlatItem = 82, FlatFish = 852;

    /// <summary>sPicTable_RedSurf: anim frame -> sit pic (S, N, W).</summary>
    private static readonly int[] SurfPicTable = [0, 1, 2, 0, 0, 1, 1, 2, 2, 0, 1, 2];
    /// <summary>sAnim_FieldMove plays frames 0-4.</summary>
    private const int FieldMoveFrames = 5;

    /// <summary>ROM offsets of Red's graphics infos this needs.</summary>
    public sealed record PlayerInfos(int Surf, int FieldMove, int Fish);

    public static void Apply(
        byte[] rom, string packDir, GraphicsPackStanza stanza, int overworldImagesTable, GbaPalette palette,
        RomFreeSpaceAllocator allocator, List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
    {
        var fieldMove = Wide32Frames(packDir, Gen3PlayerGraphicsWriter.Blank(stanza.Get("ItemSprite")) is null ? stanza.Get("FieldMoveSprite") : null, FieldMoveFrames, null);
        var sitFile = Gen3PlayerGraphicsWriter.Blank(stanza.Get("SurfingSprite")) is { } surf ? (surf, (int[]?)[0, 2, 4]) : (stanza.Get("SitSprite"), null);
        var sit = Wide32Frames(packDir, sitFile.Item1, 3, sitFile.Item2);
        if (fieldMove is null && sit is null)
        {
            return; // nothing 32px wide to keep - the regular 16px slots are right
        }

        var infos = FindPlayerInfos(rom, overworldImagesTable);
        if (infos is null)
        {
            results.Add(new("WidePoses", false, "couldn't find Red's graphics infos - 32px poses stay centre-cropped"));
            fieldMove?.ForEach(f => f.Dispose());
            sit?.ForEach(f => f.Dispose());
            return;
        }

        if (fieldMove is not null)
        {
            Widen(rom, infos.FieldMove, infos.Fish, fieldMove, Enumerable.Range(0, FieldMoveFrames).ToArray(), palette, allocator, edits, results, "FieldMove (32px)");
            fieldMove.ForEach(f => f.Dispose());
        }
        if (sit is not null)
        {
            Widen(rom, infos.Surf, infos.Fish, sit, SurfPicTable, palette, allocator, edits, results, "Sit (32px)");
            sit.ForEach(f => f.Dispose());
        }
    }

    /// <summary>
    /// The pack's frames as 32x32 images when the art is 32px wide (<paramref name="pick"/> selects frames from a
    /// combined sheet), else null.
    /// </summary>
    private static List<Image<Rgba32>>? Wide32Frames(string packDir, string? fileName, int count, int[]? pick)
    {
        if (Gen3PlayerGraphicsWriter.Blank(fileName) is null)
        {
            return null;
        }
        var path = Path.Combine(packDir, fileName!);
        if (!File.Exists(path))
        {
            return null;
        }
        using var sheet = Image.Load<Rgba32>(path);
        BackgroundRemover.MakeEdgeBackgroundTransparent(sheet);
        var available = sheet.Width / 32;
        var need = pick?.Max() + 1 ?? count;
        if (sheet.Width % 32 != 0 || available < need || sheet.Height < 32)
        {
            return null; // 16px FRLG art (or the wrong size) - leave it to the normal slots
        }
        var frames = new List<Image<Rgba32>>();
        for (var f = 0; f < count; f++)
        {
            var src = pick?[f] ?? f;
            frames.Add(sheet.Clone(c => c.Crop(new Rectangle(src * 32, sheet.Height - 32, 32, 32))));
        }
        return frames;
    }

    private static void Widen(
        byte[] rom, int info, int fishInfo, List<Image<Rgba32>> frames, int[] picTable, GbaPalette palette,
        RomFreeSpaceAllocator allocator, List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results, string name)
    {
        var tiles = new byte[frames.Count * FrameBytes];
        for (var f = 0; f < frames.Count; f++)
        {
            GbaTileCodec.Encode(frames[f], palette).CopyTo(tiles, f * FrameBytes);
        }
        var tableBytes = picTable.Length * 8;
        if (allocator.Allocate(tiles.Length + tableBytes) is not { } at)
        {
            results.Add(new(name, false, "no free ROM space"));
            return;
        }
        var tableAt = at + tiles.Length;
        var table = new byte[tableBytes];
        for (var i = 0; i < picTable.Length; i++)
        {
            BitConverter.TryWriteBytes(table.AsSpan(i * 8), GbaRom + at + picTable[i] * FrameBytes);
            BitConverter.TryWriteBytes(table.AsSpan(i * 8 + 4), (ushort)FrameBytes);
        }
        edits.Add((at, tiles));
        edits.Add((tableAt, table));

        var patched = rom.AsSpan(info, InfoSize).ToArray();
        BitConverter.TryWriteBytes(patched.AsSpan(OffSize), (ushort)FrameBytes);
        BitConverter.TryWriteBytes(patched.AsSpan(OffWidth), (short)32);
        BitConverter.TryWriteBytes(patched.AsSpan(OffHeight), (short)32);
        rom.AsSpan(fishInfo + OffOam, 4).CopyTo(patched.AsSpan(OffOam));
        rom.AsSpan(fishInfo + OffSubsprites, 4).CopyTo(patched.AsSpan(OffSubsprites));
        BitConverter.TryWriteBytes(patched.AsSpan(OffImages), GbaRom + tableAt);
        edits.Add((info, patched));
        results.Add(new(name, true, $"{frames.Count} frame(s) at 0x{at:X}, graphics info 0x{info:X} now 32x32"));
    }

    /// <summary>
    /// Finds gObjectEventGraphicsInfo_RedSurf / _RedFieldMove / _RedFish: each graphics info is located by its
    /// images pointer (Red's pic tables sit at known flat indices of the overworld image table), and the field-move
    /// one - which shares sPicTable_RedItem with the VS Seeker's - through gObjectEventGraphicsInfoPointers, where
    /// OBJ_EVENT_GFX_RED_NORMAL, _BIKE, _SURF, _FIELD_MOVE, _FISH are entries 0-4.
    /// </summary>
    public static PlayerInfos? FindPlayerInfos(byte[] rom, int overworldImagesTable)
    {
        int PicTable(int flat) => GbaRom + overworldImagesTable + flat * 8;
        int U16(int o) => rom[o] | (rom[o + 1] << 8);
        int U32(int o) => BitConverter.ToInt32(rom, o);
        int? Info(int images, int size, int width)
        {
            for (var i = 0; i + InfoSize <= rom.Length; i += 4)
            {
                if (U32(i + OffImages) == images && U16(i + OffSize) == size && U16(i + OffWidth) == width && U16(i + OffHeight) == 32)
                {
                    return i;
                }
            }
            return null;
        }

        if (Info(PicTable(FlatNormal), 512, 16) is not { } normal
            || Info(PicTable(FlatSurf), 256, 16) is not { } surf
            || Info(PicTable(FlatFish), 512, 32) is not { } fish)
        {
            return null;
        }
        for (var j = 0; j + 20 <= rom.Length; j += 4)
        {
            if (U32(j) != GbaRom + normal || U32(j + 8) != GbaRom + surf || U32(j + 16) != GbaRom + fish)
            {
                continue;
            }
            var fieldMove = U32(j + 12) - GbaRom;
            if (fieldMove < 0 || fieldMove + InfoSize > rom.Length
                || U32(fieldMove + OffImages) != PicTable(FlatItem) || U16(fieldMove + OffWidth) != 16)
            {
                return null;
            }
            return new PlayerInfos(surf, fieldMove, fish);
        }
        return null;
    }
}
