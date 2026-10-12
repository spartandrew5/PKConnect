using PKConnect.Core.launch;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

using PKConnect.Core.patching;

namespace PKConnect.Core.graphics;

/// <summary>
/// Writes the Gen 3 player sprites for the supported GBA revisions
/// (FireRed / LeafGreen (U) 1.0, Ruby / Sapphire (U), Emerald (U)):
///  - the overworld sprite-palette slots (normal / reflection / underwater),
///    16-colour, written in place through the overworld palette table;
///  - every overworld sprite slot the pack provides (walk / run / bike / fish /
///    sit / acro bike / underwater / field move / watering can / decorate / ...),
///    uncompressed 4bpp, written in place through the overworld image table;
///  - the front battle sprite (LZ10, relocated to free space + repointed);
///  - the back battle sprite (uncompressed in place on FRLG/Emerald, LZ10 on RS)
///    and its LZ10 palette;
///  - the map icon (LZ10 relocated on FRLG, raw in place on RSE) and its palette;
///  - the FRLG surf-blob + Fly-bird field-effect tiles (fixed ROM addresses);
///  - the FRLG Oak-speech intro portrait (8bpp / 32-colour LZ77, relocated +
///    repointed; palette in place). RSE has no player portrait in its intro.
///
/// Sprites are re-encoded against each pack PNG's own 16-colour palette.
/// </summary>
public static class Gen3PlayerGraphicsWriter
{
    public static (List<(int Offset, byte[] Data)> Edits, List<PlayerGraphicsSlotResult> Results)
        BuildEdits(byte[] rom, GraphicsPackStanza stanza, string packDir,
            IReadOnlyList<(int Start, int End)>? reservedRanges = null)
    {
        var edits = new List<(int Offset, byte[] Data)>();
        var results = new List<PlayerGraphicsSlotResult>();

        var rev = Gen3PlayerGraphicsOffsets.ForRom(rom);
        if (rev is null)
        {
            var code = rom.Length >= 0xB0 ? System.Text.Encoding.ASCII.GetString(rom, 0xAC, 4) : "????";
            results.Add(new("*", false, $"Unsupported Gen 3 ROM revision ({code})"));
            return (edits, results);
        }

        var allocator = new RomFreeSpaceAllocator(rom, rev.FreeSpaceStart, reservedRanges);

        // --- overworld sprite palettes ---
        // The vanilla normal palette, read before it's replaced (edits are only queued, the ROM bytes stay
        // vanilla): field-effect art drawn with it needs remapping onto the new one.
        var vanillaNormalOff = Gba.ReadPointer(rom, rev.OverworldPalettesTable + rev.NormalPaletteIndex * 8);
        var vanillaNormal = vanillaNormalOff >= 0 && vanillaNormalOff + GbaPalette.ByteLength <= rom.Length
            ? GbaPalette.FromRom(rom, vanillaNormalOff)
            : null;
        var walkFile = stanza.Get("WalkSprite");
        var normal = ReadJascPal(packDir, stanza.Get("SpriteNormalPalette"))
                     ?? DerivePalette(packDir, walkFile);
        if (normal is null)
        {
            results.Add(new("*", false, "no usable normal sprite palette (walk sheet isn't 16-colour indexed)"));
            return (edits, results);
        }
        WritePaletteInPlace(rom, rev, rev.NormalPaletteIndex, normal, edits);

        var reflection = ReadJascPal(packDir, stanza.Get("SpriteReflectionPalette")) ?? normal.DeriveReflection();
        WritePaletteInPlace(rom, rev, rev.ReflectionPaletteIndex, reflection, edits);

        GbaPalette? underwater = null;
        if (rev.UnderwaterPaletteIndex is { } uwIndex)
        {
            underwater = DerivePalette(packDir, stanza.Get("UnderwaterSprite")) ?? normal;
            WritePaletteInPlace(rom, rev, uwIndex, underwater, edits);
        }
        results.Add(new("SpritePalette", true, underwater is null ? "normal + reflection" : "normal + reflection + underwater"));

        // --- overworld sprite frames ---
        var runPerm = Gen3PlayerGraphicsOffsets.RunFramePermutation(
            stanza.Get("RunSpriteMode"), rev.RunSpriteOrder);
        foreach (var slot in rev.OverworldSlots)
        {
            var pal = slot.Name == "Underwater" && underwater is not null ? underwater : normal;
            var perm = slot.Name == "Run" ? runPerm : null;

            IReadOnlyList<int>? combinedFrames = null;
            int? sourceFrameWidth = null;
            string? file = null;

            // 1. A "combined" sheet (e.g. SoulGold's surfing.png = sit + jump
            //    interleaved) supersedes everything when the pack has it.
            if (slot.Combined is { } cs && Blank(stanza.Get(cs.PackKey)) is { } combinedFile)
            {
                file = combinedFile;
                combinedFrames = cs.Frames;
            }
            // 2. This slot's own key.
            file ??= Blank(stanza.Get(slot.PackKey));
            // 3. A fall-back key - the other Gen 3 layout's name for this sprite,
            //    which may store it at a different native frame width.
            if (file is null && slot.AltPackKeys is not null)
            {
                foreach (var alt in slot.AltPackKeys)
                {
                    if (Blank(stanza.Get(alt.Key)) is { } altFile)
                    {
                        file = altFile;
                        sourceFrameWidth = alt.SourceFrameWidth;
                        break;
                    }
                }
            }

            TryOverworldSlot(rom, packDir, file, rev, slot, pal, perm, combinedFrames, sourceFrameWidth, edits, results);
        }

        // --- FRLG: keep 32px RSE field-move / surf art whole instead of centre-cropping it to 16px ---
        if (rev.RunSpriteOrder == "frlg")
        {
            Gen3FrlgWidePoses.Apply(rom, packDir, stanza, rev.OverworldSpriteImagesTable, normal, allocator, edits, results);
        }

        // --- field-effect art at fixed ROM addresses (surf blob, Fly bird) ---
        // FRLG draws these with the player's palette (oam.paletteNum 0). A pack without its own art keeps the
        // vanilla tiles, whose pixels index vanilla Red's palette - so remap them onto the new palette, or the
        // blob / bird pick up whatever the pack put in those slots.
        foreach (var raw in rev.RawTileSlots)
        {
            if (Blank(stanza.Get(raw.PackKey)) is null && vanillaNormal is not null)
            {
                RemapVanillaRawTiles(rom, raw, vanillaNormal, normal, edits, results);
                continue;
            }
            TryRawTileSlot(rom, packDir, stanza.Get(raw.PackKey), raw, normal, vanillaNormal, edits, results);
        }

        // --- battle sprites + map icon ---
        TryFrontImage(rom, packDir, stanza.Get("FrontImage"), rev, allocator, edits, results);
        TryBackImage(rom, packDir, stanza.Get("BackImage"), rev, allocator, edits, results);
        TryMapIcon(rom, packDir, stanza.Get("MapIcon"), rev, allocator, edits, results);
        TryIntroImage(rom, packDir, stanza.Get("OakSpeechImage"), rev, allocator, edits, results);

        Log.Information("[player-graphics] gen3 {Rev}: {Applied} applied, {Skipped} skipped",
            rev.Id, results.Count(r => r.Applied), results.Count(r => !r.Applied));

        return (edits, results);
    }

    private static void WritePaletteInPlace(
        byte[] rom, Gen3PlayerGraphicsOffsets.Revision rev, int paletteIndex, GbaPalette pal,
        List<(int Offset, byte[] Data)> edits)
    {
        var off = Gba.ReadPointer(rom, rev.OverworldPalettesTable + paletteIndex * 8);
        if (off < 0 || off + GbaPalette.ByteLength > rom.Length)
        {
            return;
        }
        var bytes = pal.ToBytes();
        if (!rom.AsSpan(off, GbaPalette.ByteLength).SequenceEqual(bytes))
        {
            edits.Add((off, bytes));
        }
    }

    internal static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static void TryOverworldSlot(
        byte[] rom, string packDir, string? fileName,
        Gen3PlayerGraphicsOffsets.Revision rev, Gen3OverworldSlot slot, GbaPalette palette,
        int[]? framePermutation, IReadOnlyList<int>? combinedFrames, int? sourceFrameWidth,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
        => TryOverworldSlotAt(rom, packDir, fileName, rev.OverworldSpriteImagesTable, slot, palette,
            framePermutation, combinedFrames, sourceFrameWidth, edits, results);

    /// <summary>
    /// Writes an overworld sprite slot's frames in place at an arbitrary pic
    /// table base address — not necessarily the player's shared
    /// OverworldSpriteImagesTable. Used both for the player's own overworld
    /// sprites (<see cref="TryOverworldSlot"/>) and for a region champion
    /// NPC's own dedicated (and separate) walk-frame table — see
    /// ChampionGraphicsPatchService, which resolves the table address from a
    /// verified decomp .sym rather than guessing.
    /// </summary>
    public static void TryOverworldSlotAt(
        byte[] rom, string packDir, string? fileName,
        int tableBase, Gen3OverworldSlot slot, GbaPalette palette,
        int[]? framePermutation, IReadOnlyList<int>? combinedFrames, int? sourceFrameWidth,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
        => TryOverworldSlotWith(rom, packDir, fileName, slot, palette,
            framePermutation, combinedFrames, sourceFrameWidth, edits, results,
            targetIndex =>
            {
                var entry = tableBase + targetIndex * 8;
                var frameOffset = Gba.ReadPointer(rom, entry);
                var frameLen = (int)Gba.ReadU32(rom, entry + 4);
                return frameOffset < 0 || frameLen != slot.FrameByteLength || frameOffset + frameLen > rom.Length
                    ? null
                    : frameOffset;
            });

    /// <summary>
    /// Core of <see cref="TryOverworldSlotAt"/>: <paramref name="resolveFrame"/>
    /// maps a target frame index (<c>slot.StartIndex + write order</c>) to the
    /// ROM offset its <c>slot.FrameByteLength</c> bytes live at, or null when
    /// that frame doesn't exist. Retail games resolve through the flat overworld
    /// pic table; pokeemerald-expansion (one ascending-frames entry per sprite)
    /// resolves straight into the sprite's own tile data - see
    /// ExpansionPlayerGraphicsWriter.
    /// </summary>
    internal static void TryOverworldSlotWith(
        byte[] rom, string packDir, string? fileName,
        Gen3OverworldSlot slot, GbaPalette palette,
        int[]? framePermutation, IReadOnlyList<int>? combinedFrames, int? sourceFrameWidth,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results,
        Func<int, int?> resolveFrame)
    {
        var sheet = LoadImage(packDir, fileName, slot.Name, -1, -1, results);
        if (sheet is null)
        {
            return;
        }

        using (sheet)
        {
            // The pack sheet's own frame width: a combined sheet (surfing.png) is
            // a square-framed strip; a fall-back key pins the source width (RSE
            // field_move.png is 32px vs FRLG's 16px Item slot); else detect it
            // from the sheet + slot dimensions.
            var packFrameW = combinedFrames is not null ? sheet.Height
                : sourceFrameWidth ?? PackFrameWidth(sheet.Width, sheet.Height, slot);
            if (packFrameW <= 0 || sheet.Width % packFrameW != 0)
            {
                var want = packFrameW > 0 ? $"{packFrameW}x{sheet.Height}" : $"{slot.FrameWidth}x{slot.FrameHeight}";
                results.Add(new(slot.Name, false,
                    $"expected a strip of {want} frames, got {sheet.Width}x{sheet.Height}"));
                return;
            }

            var sourceCount = sheet.Width / packFrameW;
            var writeOrder = slot.WriteFrameOrder;
            var frameCount = combinedFrames?.Count ?? writeOrder?.Count ?? sourceCount;
            if (framePermutation is not null && framePermutation.Length != frameCount)
            {
                framePermutation = null; // sheet frame count doesn't match the reorder table
            }
            if (slot.MaxFrames > 0 && writeOrder is null && frameCount > slot.MaxFrames)
            {
                frameCount = slot.MaxFrames; // don't spill past this sprite's frames into the next one
            }
            var refit = packFrameW != slot.FrameWidth || sheet.Height != slot.FrameHeight;
            int applied = 0, skipped = 0;
            for (var f = 0; f < frameCount; f++)
            {
                var srcFrame = combinedFrames is not null ? combinedFrames[f] : (framePermutation?[f] ?? f);
                if (srcFrame >= sourceCount)
                {
                    skipped++;
                    continue;
                }

                var targetIndex = slot.StartIndex + (writeOrder?[f] ?? f);
                if (resolveFrame(targetIndex) is not { } frameOffset)
                {
                    skipped++;
                    continue;
                }
                var frameLen = slot.FrameByteLength;

                using var crop = sheet.Clone(ctx => ctx.Crop(
                    new Rectangle(srcFrame * packFrameW, 0, packFrameW, sheet.Height)));
                using var frame = refit ? FitFrame(crop, slot.FrameWidth, slot.FrameHeight) : crop.Clone();
                var encoded = GbaTileCodec.Encode(frame, palette);
                if (!rom.AsSpan(frameOffset, frameLen).SequenceEqual(encoded))
                {
                    edits.Add((frameOffset, encoded));
                }
                applied++;
            }

            var kind = combinedFrames is not null ? "combined " : "";
            var note = refit
                ? $"{applied} {kind}frame(s), re-fit {packFrameW}px->{slot.FrameWidth}px"
                : $"{applied} {kind}frame(s)";
            results.Add(new(slot.Name, applied > 0,
                skipped == 0 ? note : $"{note}, {skipped} skipped (table mismatch)"));
        }
    }

    /// <summary>
    /// The pack sheet's own frame width: the slot's width when it divides evenly,
    /// else 16 for a 32px slot fed a 16px-wide FRLG strip. 0 if neither fits.
    /// The 16px guess only matters for a slot whose key is shared between layouts
    /// (RSE Sit &lt;- FRLG sit_frlg.png); a fall-back key carries an explicit
    /// SourceFrameWidth and never reaches here.
    /// </summary>
    private static int PackFrameWidth(int sheetW, int sheetH, Gen3OverworldSlot slot)
    {
        if (sheetW == 0 || sheetH == 0)
        {
            return 0;
        }
        if (sheetW % slot.FrameWidth == 0 && (sheetH == slot.FrameHeight || sheetH == 16 || sheetH == 32))
        {
            return slot.FrameWidth;
        }
        if (slot.FrameWidth == 32 && sheetW % 16 == 0 && (sheetH == 32 || sheetH == 16))
        {
            return 16;
        }
        return 0;
    }

    /// <summary>Centres <paramref name="src"/> in a transparent w x h canvas
    /// (or centre-crops it when larger) so a cross-layout frame lands in the slot.</summary>
    private static Image<Rgba32> FitFrame(Image<Rgba32> src, int w, int h)
    {
        var dst = new Image<Rgba32>(w, h);
        var x = (w - src.Width) / 2;
        var y = h - src.Height; // feet-align: overworld sprites sit on the bottom edge
        dst.Mutate(ctx => ctx.DrawImage(src, new Point(x, y), 1f));
        return dst;
    }

    private static void TryFrontImage(
        byte[] rom, string packDir, string? fileName,
        Gen3PlayerGraphicsOffsets.Revision rev, RomFreeSpaceAllocator allocator,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
    {
        var (applied, reason) = WriteFrontImageAt(rom, packDir, fileName, rev.Front, allocator, edits);
        results.Add(new("FrontImage", applied, reason));
    }

    /// <summary>
    /// Writes a pack's front battle sprite (LZ10 image + LZ10 palette, relocated
    /// to free space and repointed) at an arbitrary <see cref="Gen3FrontSlot"/> —
    /// not necessarily this game's player index. Used both for the player's own
    /// front sprite (<see cref="TryFrontImage"/>, <c>rev.Front</c>) and for a
    /// region champion NPC's front sprite (see ChampionGraphicsPatchService,
    /// which computes the slot from the champion trainer's own trainerPic byte
    /// against <see cref="Gen3PlayerGraphicsOffsets.Revision.TrainerFrontImagesTable"/>).
    /// </summary>
    public static (bool Applied, string? Reason) WriteFrontImageAt(
        byte[] rom, string packDir, string? fileName, Gen3FrontSlot slot,
        RomFreeSpaceAllocator allocator, List<(int Offset, byte[] Data)> edits)
    {
        var side = slot.TilesWide * 8;
        var loadResults = new List<PlayerGraphicsSlotResult>();
        var img = LoadImage(packDir, fileName, "FrontImage", side, side, loadResults);
        if (img is null)
        {
            return (false, loadResults.Count > 0 ? loadResults[0].Reason : "not provided by pack");
        }

        using (img)
        {
            var palette = DerivePalette(packDir, fileName!);
            if (palette is null)
            {
                return (false, "front image isn't 16-colour indexed and has >15 opaque colours");
            }
            var srcPointerOffset = Gba.ReadPointer(rom, slot.PointerEntry);
            if (srcPointerOffset < 0 || rom[srcPointerOffset] != 0x10)
            {
                return (false, "front-image data isn't LZ10 — wrong revision/romhack?");
            }

            var okImg = RelocateCompressed(rom, GbaTileCodec.Encode(img, palette), slot.PointerEntry, allocator, edits);
            var okPal = RelocateCompressed(rom, palette.ToBytes(), slot.PaletteEntry, allocator, edits);
            return (okImg && okPal, okImg && okPal ? "image + palette relocated" : "no free ROM space");
        }
    }

    private static void TryBackImage(
        byte[] rom, string packDir, string? fileName,
        Gen3PlayerGraphicsOffsets.Revision rev, RomFreeSpaceAllocator allocator,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
    {
        // The back sprite is a vertical strip of 64x64 frames: 5 on FRLG, 4 on
        // RSE. Accept a strip with any frame count and resample to the game's -
        // FRLG->RSE drops a middle wind-up frame, RSE->FRLG repeats the throw -
        // so an FRLG-only pack (e.g. Red) still gets a usable back sprite on RSE.
        var packStrip = LoadImage(packDir, fileName, "BackImage", rev.Back.TilesWide * 8, -1, results);
        if (packStrip is null)
        {
            return;
        }
        var frameH = rev.Back.TilesWide * 8;
        var wantFrames = rev.Back.FrameCount;
        Image<Rgba32> img;
        using (packStrip)
        {
            if (packStrip.Height % frameH != 0 || packStrip.Height == 0)
            {
                results.Add(new("BackImage", false,
                    $"back sprite must be a vertical strip of {frameH}px frames, got {packStrip.Width}x{packStrip.Height}"));
                return;
            }
            var haveFrames = packStrip.Height / frameH;
            img = haveFrames == wantFrames
                ? packStrip.Clone()
                : ResampleFrameStrip(packStrip, frameH, haveFrames, wantFrames);
        }

        using (img)
        {
            var palette = DerivePalette(packDir, fileName!);
            if (palette is null)
            {
                results.Add(new("BackImage", false, "back image isn't 16-colour indexed and has >15 opaque colours"));
                return;
            }

            var encoded = GbaTileCodec.Encode(img, palette);
            bool okImg;
            if (rev.Back.Compressed)
            {
                if (rom[Gba.ReadPointer(rom, rev.Back.PointerLocation)] != 0x10)
                {
                    results.Add(new("BackImage", false, "back-image data isn't LZ10 — wrong revision?"));
                    return;
                }
                okImg = RelocateCompressed(rom, encoded, rev.Back.PointerLocation, allocator, edits);
            }
            else
            {
                var dest = Gba.ReadPointer(rom, rev.Back.PointerLocation);
                var storedLen = (int)Gba.ReadU32(rom, rev.Back.PointerLocation + 4) & 0xFFFF;
                if (dest < 0 || storedLen != rev.Back.ByteLength || dest + rev.Back.ByteLength > rom.Length)
                {
                    results.Add(new("BackImage", false, "back-image table entry doesn't match this revision"));
                    return;
                }
                if (!rom.AsSpan(dest, rev.Back.ByteLength).SequenceEqual(encoded))
                {
                    edits.Add((dest, encoded));
                }
                okImg = true;
            }

            var okPal = RelocateCompressed(rom, palette.ToBytes(), rev.Back.PaletteEntry, allocator, edits);
            results.Add(new("BackImage", okImg && okPal,
                okImg && okPal ? "image + palette" : "no free ROM space"));
        }
    }

    private static void TryMapIcon(
        byte[] rom, string packDir, string? fileName,
        Gen3PlayerGraphicsOffsets.Revision rev, RomFreeSpaceAllocator allocator,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
    {
        var img = LoadImage(packDir, fileName, "MapIcon", 16, 16, results);
        if (img is null)
        {
            return;
        }

        using (img)
        {
            var palette = DerivePalette(packDir, fileName!);
            if (palette is null)
            {
                results.Add(new("MapIcon", false, "map icon isn't 16-colour indexed and has >15 opaque colours"));
                return;
            }
            var encoded = GbaTileCodec.Encode(img, palette);

            bool okImg;
            int palTarget;
            if (rev.MapIcon.Compressed)
            {
                if (Gba.ReadPointer(rom, rev.MapIcon.ImageAddr) is var iloc && (iloc < 0 || rom[iloc] != 0x10))
                {
                    results.Add(new("MapIcon", false, "map-icon data isn't LZ10 — wrong revision/romhack?"));
                    return;
                }
                okImg = RelocateCompressed(rom, encoded, rev.MapIcon.ImageAddr, allocator, edits);
                palTarget = Gba.ReadPointer(rom, rev.MapIcon.PaletteAddr);
            }
            else
            {
                var dest = rev.MapIcon.ImageAddr;
                if (dest + rev.MapIcon.ByteLength > rom.Length)
                {
                    results.Add(new("MapIcon", false, "map-icon offset out of range"));
                    return;
                }
                if (!rom.AsSpan(dest, rev.MapIcon.ByteLength).SequenceEqual(encoded))
                {
                    edits.Add((dest, encoded));
                }
                okImg = true;
                palTarget = rev.MapIcon.PaletteAddr;
            }

            var okPal = palTarget >= 0 && palTarget + GbaPalette.ByteLength <= rom.Length;
            if (okPal && !rom.AsSpan(palTarget, GbaPalette.ByteLength).SequenceEqual(palette.ToBytes()))
            {
                edits.Add((palTarget, palette.ToBytes()));
            }
            results.Add(new("MapIcon", okImg && okPal, okImg && okPal ? "image + palette" : "no free ROM space / bad pointer"));
        }
    }

    /// <summary>
    /// Re-points the vanilla field-effect tiles of <paramref name="slot"/> from <paramref name="vanilla"/>'s
    /// colours to the nearest colours of <paramref name="replacement"/>, in place.
    /// </summary>
    private static void RemapVanillaRawTiles(
        byte[] rom, Gen3RawTileSlot slot, GbaPalette vanilla, GbaPalette replacement,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
    {
        var map = PaletteIndexMap(vanilla, replacement);
        var length = slot.MaxFrames * slot.FrameByteLength;
        if (slot.TilesAddr + length > rom.Length)
        {
            results.Add(new(slot.Name, false, "vanilla tiles out of ROM range"));
            return;
        }
        var remapped = RemapTileIndices(rom.AsSpan(slot.TilesAddr, length), map);
        if (!rom.AsSpan(slot.TilesAddr, length).SequenceEqual(remapped))
        {
            edits.Add((slot.TilesAddr, remapped));
        }
        results.Add(new(slot.Name, true, "vanilla art remapped to the new palette"));
    }

    /// <summary>For each slot of <paramref name="from"/>, the index of its nearest colour in <paramref name="to"/> (0 stays 0).</summary>
    public static byte[] PaletteIndexMap(GbaPalette from, GbaPalette to)
    {
        var map = new byte[GbaPalette.Size];
        for (var i = 1; i < GbaPalette.Size; i++)
        {
            map[i] = (byte)to.NearestIndex(from[i]);
        }
        return map;
    }

    /// <summary>4bpp tile bytes with every pixel's index replaced through <paramref name="map"/>.</summary>
    public static byte[] RemapTileIndices(ReadOnlySpan<byte> tiles, byte[] map)
    {
        var outBytes = new byte[tiles.Length];
        for (var i = 0; i < tiles.Length; i++)
        {
            var b = tiles[i];
            outBytes[i] = (byte)(map[b & 0xF] | (map[b >> 4] << 4));
        }
        return outBytes;
    }

    /// <summary>
    /// Writes a pack's field-effect strip in place. A full strip replaces frames from 0; a strip of exactly
    /// <see cref="Gen3RawTileSlot.PartialFrames"/> frames goes in at <see cref="Gen3RawTileSlot.PartialStartFrame"/>.
    /// Frames the pack doesn't cover keep the vanilla art, remapped onto the new palette when
    /// <paramref name="vanillaPalette"/> is known. The whole run is one edit.
    /// </summary>
    private static void TryRawTileSlot(
        byte[] rom, string packDir, string? fileName, Gen3RawTileSlot slot, GbaPalette palette, GbaPalette? vanillaPalette,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
    {
        // A vertical strip fixes width and lets height carry frames (Fly bird);
        // a horizontal strip does the reverse (surf blob).
        var expW = slot.VerticalStrip ? slot.FrameWidth : -1;
        var expH = slot.VerticalStrip ? -1 : slot.FrameHeight;
        var sheet = LoadImage(packDir, fileName, slot.Name, expW, expH, results);
        if (sheet is null)
        {
            return;
        }

        using (sheet)
        {
            var span = slot.VerticalStrip ? sheet.Height : sheet.Width;
            var unit = slot.VerticalStrip ? slot.FrameHeight : slot.FrameWidth;
            var fixedOk = slot.VerticalStrip ? sheet.Width == slot.FrameWidth : sheet.Height == slot.FrameHeight;
            if (!fixedOk || span == 0 || span % unit != 0)
            {
                results.Add(new(slot.Name, false,
                    $"expected a {(slot.VerticalStrip ? "vertical" : "horizontal")} strip of "
                    + $"{slot.FrameWidth}x{slot.FrameHeight} frames, got {sheet.Width}x{sheet.Height}"));
                return;
            }

            var packFrames = span / unit;
            var start = slot.PartialFrames > 0 && packFrames == slot.PartialFrames ? slot.PartialStartFrame : 0;
            var frames = Math.Min(packFrames, slot.MaxFrames - start);
            var length = slot.MaxFrames * slot.FrameByteLength;
            if (slot.TilesAddr + length > rom.Length)
            {
                results.Add(new(slot.Name, false, "tiles out of ROM range"));
                return;
            }
            var vanilla = rom.AsSpan(slot.TilesAddr, length);
            var buffer = vanillaPalette is not null
                ? RemapTileIndices(vanilla, PaletteIndexMap(vanillaPalette, palette))
                : vanilla.ToArray();
            for (var f = 0; f < frames; f++)
            {
                var rect = slot.VerticalStrip
                    ? new Rectangle(0, f * slot.FrameHeight, slot.FrameWidth, slot.FrameHeight)
                    : new Rectangle(f * slot.FrameWidth, 0, slot.FrameWidth, slot.FrameHeight);
                using var frame = sheet.Clone(ctx => ctx.Crop(rect));
                GbaTileCodec.Encode(frame, palette).CopyTo(buffer, (start + f) * slot.FrameByteLength);
            }
            if (!vanilla.SequenceEqual(buffer))
            {
                edits.Add((slot.TilesAddr, buffer));
            }
            var where = start > 0 ? $"frames {start}-{start + frames - 1}" : $"{frames} frame(s)";
            results.Add(new(slot.Name, frames > 0, $"{where} in place{(vanillaPalette is not null && frames < slot.MaxFrames ? ", rest vanilla remapped" : "")}"));
        }
    }

    private static void TryIntroImage(
        byte[] rom, string packDir, string? fileName,
        Gen3PlayerGraphicsOffsets.Revision rev, RomFreeSpaceAllocator allocator,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
    {
        if (rev.Intro is not { } intro)
        {
            return; // RSE's Birch intro shows no player portrait
        }

        var img = LoadImage(packDir, fileName, "OakSpeechImage",
            Gen3IntroSlot.WidthPx, Gen3IntroSlot.HeightPx, results);
        if (img is null)
        {
            return;
        }

        using (img)
        {
            var built = Gen3IntroImage.Build(img);
            if (built is null)
            {
                results.Add(new("OakSpeechImage", false, "intro portrait has no opaque pixels / wrong size"));
                return;
            }
            var (tiles, palette) = built.Value;

            var tilesLoc = Gba.ReadPointer(rom, intro.TilesPointerLocation);
            if (tilesLoc < 0 || rom[tilesLoc] != 0x10)
            {
                results.Add(new("OakSpeechImage", false, "intro-image data isn't LZ77 - wrong revision/romhack?"));
                return;
            }

            var okImg = RelocateCompressed(rom, tiles, intro.TilesPointerLocation, allocator, edits);

            var okPal = intro.PaletteAddr >= 0 && intro.PaletteAddr + intro.PaletteByteLength <= rom.Length;
            if (okPal && !rom.AsSpan(intro.PaletteAddr, intro.PaletteByteLength).SequenceEqual(palette))
            {
                edits.Add((intro.PaletteAddr, palette));
            }

            results.Add(new("OakSpeechImage", okImg && okPal,
                okImg && okPal ? "image relocated + palette" : "no free ROM space"));
        }
    }

    // ---- helpers ----

    /// <summary>
    /// Rebuilds a vertical 64x64-frame strip for a different generation's frame
    /// count AND order. The back-sprite animation frames are:
    ///   FRLG: [0]=stand [1]=windup [2]=throw1 [3]=throw2 [4]=throw-finish
    ///   RSE : [0]=windup [1]=throw1 [2]=throw2 [3]=stand
    /// So FRLG->RSE = [1,2,3,0] (drop throw-finish); RSE->FRLG = [3,0,1,2,2]
    /// (reuse throw2 as the finish). Other mismatches repeat the last frame.
    /// </summary>
    internal static Image<Rgba32> ResampleFrameStrip(Image<Rgba32> src, int frameH, int have, int want)
    {
        int[] map = (have, want) switch
        {
            (5, 4) => [1, 2, 3, 0],
            (4, 5) => [3, 0, 1, 2, 2],
            _ => Enumerable.Range(0, want).Select(i => Math.Min(i, have - 1)).ToArray(),
        };

        var dst = new Image<Rgba32>(src.Width, want * frameH);
        for (var i = 0; i < want; i++)
        {
            var srcY = map[i] * frameH;
            var dstY = i * frameH;
            using var frame = src.Clone(ctx => ctx.Crop(new Rectangle(0, srcY, src.Width, frameH)));
            dst.Mutate(ctx => ctx.DrawImage(frame, new Point(0, dstY), 1f));
        }
        return dst;
    }

    private static bool RelocateCompressed(
        byte[] rom, byte[] uncompressed, int pointerEntry,
        RomFreeSpaceAllocator allocator, List<(int Offset, byte[] Data)> edits)
    {
        var compressed = Lz10Codec.Compress(uncompressed);
        var dest = allocator.Allocate(compressed.Length);
        if (dest is null || dest.Value + compressed.Length > 0xFFFFFF)
        {
            return false;
        }
        edits.Add((dest.Value, compressed));
        edits.Add((pointerEntry, Gba.PointerBytes(dest.Value)));
        return true;
    }

    internal static GbaPalette? ReadJascPal(string packDir, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }
        var path = Path.Combine(packDir, fileName);
        return File.Exists(path) ? GbaPalette.FromJascPal(File.ReadAllLines(path)) : null;
    }

    /// <summary>The GBA palette for a pack PNG: its 16-colour PLTE chunk, else derived from pixels.</summary>
    public static GbaPalette? DerivePalette(string packDir, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }
        var path = Path.Combine(packDir, fileName);
        if (!File.Exists(path))
        {
            return null;
        }
        var bytes = File.ReadAllBytes(path);
        var fromPlte = GbaPalette.FromIndexedPng(bytes);
        if (fromPlte is not null)
        {
            return fromPlte;
        }
        try
        {
            using var img = Image.Load<Rgba32>(bytes);
            BackgroundRemover.MakeEdgeBackgroundTransparent(img);
            // A non-indexed PNG: use its colours directly if it has <= 15, else
            // keep the 15 most-used (GBA sprites are 4bpp / 16 colours).
            return GbaPalette.FromImage(img, 0) ?? GbaPalette.FromImageQuantized(img, 0);
        }
        catch
        {
            return null;
        }
    }

    /// <param name="expectedW">-1 to accept any width. <paramref name="expectedH"/>: -1 for any height.</param>
    internal static Image<Rgba32>? LoadImage(
        string packDir, string? fileName, string slot, int expectedW, int expectedH,
        List<PlayerGraphicsSlotResult> results)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            results.Add(new(slot, false, "not provided by pack"));
            return null;
        }
        var path = Path.Combine(packDir, fileName);
        if (!File.Exists(path))
        {
            results.Add(new(slot, false, $"file not found: {fileName}"));
            return null;
        }
        try
        {
            var img = Image.Load<Rgba32>(path);
            if ((expectedW >= 0 && img.Width != expectedW) || (expectedH >= 0 && img.Height != expectedH))
            {
                var w = expectedW < 0 ? "N" : expectedW.ToString();
                var h = expectedH < 0 ? "N" : expectedH.ToString();
                results.Add(new(slot, false, $"expected {w}x{h}, got {img.Width}x{img.Height}"));
                img.Dispose();
                return null;
            }
            BackgroundRemover.MakeEdgeBackgroundTransparent(img);
            return img;
        }
        catch (Exception ex)
        {
            results.Add(new(slot, false, $"load failed: {ex.Message}"));
            return null;
        }
    }
}
