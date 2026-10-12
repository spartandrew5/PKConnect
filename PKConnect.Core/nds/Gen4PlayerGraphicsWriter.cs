using PKConnect.Core.graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

using PKConnect.Core.patching;

namespace PKConnect.Core.nds;

/// <summary>
/// Writes a trainer profile's chosen Gen 4 sprites into an NDS ROM. Overworld
/// slots are a same-dimensions BTX0 overwrite (see <see cref="Btx0SpriteCodec"/>);
/// composite slots (front/back battle sprites, HM-use) reverse
/// <see cref="NcerCompositor"/>'s per-bank compositing to rebuild the NCGR
/// tile data and a fresh NCLR palette from the pack's strip image. Either
/// way there's never any relocation/free-space bookkeeping like the GBA
/// writers need - the edit is always exactly the member's own byte range, in
/// place.
/// </summary>
public static class Gen4PlayerGraphicsWriter
{
    public static (List<(int Offset, byte[] Data)> Edits, List<PlayerGraphicsSlotResult> Results)
        BuildEdits(byte[] rom, GraphicsPackStanza stanza, string packDir, Gen4PlayerGraphicsOffsets.Revision rev)
    {
        var edits = new List<(int Offset, byte[] Data)>();
        var results = new List<PlayerGraphicsSlotResult>();

        var narcRange = NitroFs.ResolveFile(rom, rev.OverworldNarcPath);
        if (narcRange is null)
        {
            results.Add(new("*", false, $"Overworld sprite NARC not found at {rev.OverworldNarcPath}"));
        }
        else
        {
            var narc = new byte[narcRange.Value.Length];
            Array.Copy(rom, narcRange.Value.Offset, narc, 0, narc.Length);

            foreach (var slot in rev.OverworldSlots)
            {
                TryOverworldSlot(rom, narc, narcRange.Value.Offset, packDir, stanza, slot, edits, results);
            }
        }

        List<Rgba32>? frontPalette = null;
        List<Rgba32>? backPalette = null;
        if (rev.Front is { } front) frontPalette = TryCompositeSlot(rom, packDir, stanza, "Front", front, edits, results);
        if (rev.Back is { } back) backPalette = TryCompositeSlot(rom, packDir, stanza, "Back", back, edits, results);
        // Each back group's freshly-built palette, keyed by its palette member, so every enciphered
        // duplicate is encoded against the palette its own group actually loads.
        var backPaletteByMember = new Dictionary<int, List<Rgba32>>();
        if (rev.Back is { } mainBack && backPalette is not null) backPaletteByMember[mainBack.HeroPalette] = backPalette;
        foreach (var extraBack in rev.ExtraBacks ?? [])
        {
            if (TryCompositeSlot(rom, packDir, stanza, $"Back[{extraBack.HeroSprite}]", extraBack, edits, results) is { } extraPalette)
            {
                backPaletteByMember[extraBack.HeroPalette] = extraPalette;
            }
        }
        if (rev.HmUse is { } hmUse) TryCompositeSlot(rom, packDir, stanza, "HmUse", hmUse, edits, results);

        if (rev.TrainerCard is { } trainerCard) TryTrainerCardSlot(rom, packDir, stanza, trainerCard, edits, results);

        if (rev.FrontDuplicate is { } frontDup && frontPalette is not null)
        {
            TryFrontDuplicateSlot(rom, packDir, stanza, frontDup, frontPalette, edits, results, "FrontDuplicate");
        }
        if (rev.BackDuplicates is { } backDups && backPalette is not null)
        {
            foreach (var backDup in backDups)
            {
                var dupPalette = backPaletteByMember.GetValueOrDefault(backDup.HeroPalette) ?? backPalette;
                TryBackDuplicateSlot(rom, packDir, stanza, backDup, dupPalette, edits, results, $"BackDuplicate[{backDup.HeroSprite}]");
            }
        }

        // Diamond/Pearl: Front has no NCER composite counterpart to borrow a
        // palette from, so build one fresh from front.png itself, same as
        // TryCompositeSlot does for the NCER games.
        if (rev.CipheredFront is { } cipheredFront)
        {
            var freshFrontPalette = BuildPaletteFromPack(packDir, stanza, "FrontImage", "Front", results);
            if (freshFrontPalette is not null)
            {
                TryFrontDuplicateSlot(rom, packDir, stanza, cipheredFront, freshFrontPalette, edits, results, "Front");
            }
        }

        // Diamond/Pearl: the real Back animation lives in batt_obj.narc, not
        // an NCER composite in trbgra.narc - build its own fresh palette from
        // back.png, then share that same palette with trbgra's "duplicate"
        // transition layer so the two never drift apart in colour.
        if (rev.BattObjBack is { } battObjBack)
        {
            var freshBackPalette = TryBattObjBackSlot(rom, packDir, stanza, battObjBack, edits, results);
            if (freshBackPalette is not null)
            {
                var dup = new Gen4DuplicateSlot(battObjBack.DuplicateNarcPath, battObjBack.HeroDuplicateSprite,
                    battObjBack.HeroPalette, battObjBack.HeroineDuplicateSprite, battObjBack.HeroinePalette);
                TryBackDuplicateSlot(rom, packDir, stanza, dup, freshBackPalette, edits, results, "BackDuplicate");
            }
        }

        return (edits, results);
    }

    /// <summary>
    /// Writes the pack's 64x64 trainer-card pose into the hero's two slots of
    /// the flat 8bpp sheet (see <see cref="Gen4TrainerCardCodec"/>) and syncs
    /// the character's palette sub-range across every matching 256-colour
    /// member so the card's per-badge tier cycling never recolours it. Each
    /// touched member is a distinct NARC member, hence a distinct in-place
    /// edit. Heroine's slots and palette indices are left vanilla, matching
    /// the "always reskin the hero" convention every other slot follows.
    /// </summary>
    private static void TryTrainerCardSlot(
        byte[] rom, string packDir, GraphicsPackStanza stanza, Gen4TrainerCardSlot slot,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
    {
        const string name = "TrainerCard";
        // No dedicated card art? Auto-generate from the pack's front sprite -
        // Gen4TrainerCardCodec.FitToCanvas crops/centres it to the figure, so
        // every protagonist gets a card with no per-pack asset.
        var fileName = stanza.Get(slot.PackKey);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = stanza.Get("FrontImage");
        if (string.IsNullOrWhiteSpace(fileName))
        {
            results.Add(new(name, false, "no TrainerCardImage or FrontImage in pack"));
            return;
        }

        var path = Path.Combine(packDir, fileName);
        if (!File.Exists(path))
        {
            results.Add(new(name, false, $"file not found: {fileName}"));
            return;
        }

        var narcRange = NitroFs.ResolveFile(rom, slot.NarcPath);
        if (narcRange is null)
        {
            results.Add(new(name, false, $"NARC not found at {slot.NarcPath}"));
            return;
        }

        var narc = new byte[narcRange.Value.Length];
        Array.Copy(rom, narcRange.Value.Offset, narc, 0, narc.Length);

        byte[] ncgr;
        byte[] heroNscr, heroineNscr;
        (int Offset, int Length) spriteRange;
        var paletteMembers = new List<(int Member, int Offset, byte[] Data)>();
        try
        {
            spriteRange = NarcArchive.GetMemberRange(narc, slot.SpriteMember);
            ncgr = new byte[spriteRange.Length];
            Array.Copy(narc, spriteRange.Offset, ncgr, 0, ncgr.Length);

            heroNscr = CopyMember(narc, slot.HeroNscrMember);
            heroineNscr = CopyMember(narc, slot.HeroineNscrMember);

            for (var m = slot.PaletteMemberStart; m <= slot.PaletteMemberEnd; m++)
            {
                var (pOff, pLen) = NarcArchive.GetMemberRange(narc, m);
                var pData = new byte[pLen];
                Array.Copy(narc, pOff, pData, 0, pLen);
                paletteMembers.Add((m, pOff, pData));
            }
        }
        catch (Exception ex)
        {
            results.Add(new(name, false, $"couldn't locate NARC members: {ex.Message}"));
            return;
        }

        Image<Rgba32> pose;
        try
        {
            pose = Image.Load<Rgba32>(path);
        }
        catch (Exception ex)
        {
            results.Add(new(name, false, $"couldn't read {fileName}: {ex.Message}"));
            return;
        }

        Gen4TrainerCardCodec.InjectResult injectResult;
        using (pose)
        {
            try
            {
                injectResult = Gen4TrainerCardCodec.Inject(
                    ncgr, paletteMembers.Select(p => p.Data).ToList(), pose, heroNscr, heroineNscr,
                    slot.VerticalOffset, slot.HorizontalOffset);
            }
            catch (Exception ex)
            {
                results.Add(new(name, false, ex.Message));
                return;
            }
        }

        edits.Add((narcRange.Value.Offset + spriteRange.Offset, ncgr));
        foreach (var p in injectResult.WrittenPalettes)
        {
            edits.Add((narcRange.Value.Offset + paletteMembers[p].Offset, paletteMembers[p].Data));
        }
        if (injectResult.NscrChanged)
        {
            var (heroNscrOffset, _) = NarcArchive.GetMemberRange(narc, slot.HeroNscrMember);
            edits.Add((narcRange.Value.Offset + heroNscrOffset, heroNscr));
        }
        results.Add(new(name, true, null));
    }

    private static byte[] CopyMember(byte[] narc, int member)
    {
        var (off, len) = NarcArchive.GetMemberRange(narc, member);
        var data = new byte[len];
        Array.Copy(narc, off, data, 0, len);
        return data;
    }

    private static List<Rgba32>? BuildPaletteFromPack(
        string packDir, GraphicsPackStanza stanza, string packKey, string name, List<PlayerGraphicsSlotResult> results)
    {
        var fileName = stanza.Get(packKey);
        if (string.IsNullOrWhiteSpace(fileName)) return null; // the slot itself will report "not provided by pack"

        var path = Path.Combine(packDir, fileName);
        if (!File.Exists(path)) return null;

        try
        {
            using var image = Image.Load<Rgba32>(path);
            var background = image[0, 0];
            return PaletteQuantizer.BuildPalette(image, background, 16);
        }
        catch (Exception ex)
        {
            results.Add(new(name, false, $"couldn't read {fileName}: {ex.Message}"));
            return null;
        }
    }

    private static void TryOverworldSlot(
        byte[] rom, byte[] narc, int narcRomOffset, string packDir, GraphicsPackStanza stanza,
        Gen4OverworldSlot slot, List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
    {
        var fileName = stanza.Get(slot.PackKey);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            results.Add(new(slot.Name, false, "not provided by pack"));
            return;
        }

        var path = Path.Combine(packDir, fileName);
        if (!File.Exists(path))
        {
            results.Add(new(slot.Name, false, $"file not found: {fileName}"));
            return;
        }

        (int MemberOffset, int MemberLength) member;
        byte[] btx;
        try
        {
            member = NarcArchive.GetMemberRange(narc, slot.HeroMember);
            btx = new byte[member.MemberLength];
            Array.Copy(narc, member.MemberOffset, btx, 0, btx.Length);
        }
        catch (Exception ex)
        {
            results.Add(new(slot.Name, false, $"couldn't locate NARC member {slot.HeroMember}: {ex.Message}"));
            return;
        }

        Image<Rgba32> replacement;
        try
        {
            replacement = Image.Load<Rgba32>(path);
        }
        catch (Exception ex)
        {
            results.Add(new(slot.Name, false, $"couldn't read {fileName}: {ex.Message}"));
            return;
        }

        using (replacement)
        {
            try
            {
                var adjusted = AdjustFrameCountIfMismatched(btx, replacement, slot.Name);
                using (adjusted)
                {
                    Btx0SpriteCodec.Encode(btx, adjusted ?? replacement);
                }
            }
            catch (Exception ex)
            {
                results.Add(new(slot.Name, false, ex.Message));
                return;
            }
        }

        edits.Add((narcRomOffset + member.MemberOffset, btx));
        results.Add(new(slot.Name, true, null));
    }

    /// <summary>
    /// The only overworld pose with a confirmed frame-count mismatch between
    /// games sharing the same pack asset is Bike: HGSS's is 22 frames,
    /// Diamond/Pearl/Platinum's own is 20 - two extra "hold" frames HGSS
    /// inserts for timing that don't exist in the DP/Pt sheet. Rather than
    /// bake a game-specific length into the asset file itself (which then
    /// breaks the OTHER game's own native use of that same file), remap on
    /// the fly here using the exact frame correspondence the user identified
    /// by eye against both real sheets (not a generic pad/truncate guess -
    /// an earlier version of this method guessed "repeat/drop trailing
    /// frames", which was wrong):
    ///   - HGSS (22) -&gt; DP/Pt (20): drop indices 16 and 21.
    ///   - DP/Pt (20) -&gt; HGSS (22): insert a duplicate of DP/Pt's own frame 1
    ///     at slot 16, and a duplicate of DP/Pt's own frame 18 at slot 21 -
    ///     the exact inverse of the drop above (slots 17-20 shift back to
    ///     the removed frames' old neighbours, matching what dropping 16/21
    ///     would have produced).
    /// Returns null (no remapping needed/possible) when the image already
    /// matches or isn't one of these two known lengths, in which case the
    /// caller uses the original image unchanged.
    /// </summary>
    private static Image<Rgba32>? AdjustFrameCountIfMismatched(byte[] btx, Image<Rgba32> replacement, string slotName)
    {
        // A pack's player walk sheet (24 frames: walk + run) drawn over a standard 12-frame NPC
        // model (the Connect champion placeholder, a copy of Cynthia's). Mapped by matching frame
        // silhouettes of the NPC models against the player's own sheet: sides 1-6 line up,
        // NPC 0 = north stand, 7/8 = north steps, 9-11 = south stand/steps (player 21-23).
        if (slotName == "Walk")
        {
            var walkHeader = Btx0SpriteCodec.ReadHeader(btx);
            if (walkHeader.Width == 32 && walkHeader.Height == 32 * 12 && replacement.Width == 32 && replacement.Height == 32 * 24)
            {
                int[] npcFromPlayer = [0, 1, 2, 3, 4, 5, 6, 9, 20, 21, 22, 23];
                return SelectFrames(replacement, 32, npcFromPlayer);
            }
            return null;
        }

        if (slotName != "Bike") return null;

        var header = Btx0SpriteCodec.ReadHeader(btx);
        if (replacement.Width != header.Width || replacement.Height == header.Height) return null;
        if (header.Width == 0 || header.Height % header.Width != 0 || replacement.Height % header.Width != 0) return null;

        var frameSize = header.Width;
        var haveFrames = replacement.Height / frameSize;
        var needFrames = header.Height / frameSize;

        if (haveFrames == 22 && needFrames == 20)
        {
            int[] keep = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 17, 18, 19, 20];
            return SelectFrames(replacement, frameSize, keep);
        }
        if (haveFrames == 20 && needFrames == 22)
        {
            int[] withInserts = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 1, 16, 17, 18, 19, 18];
            return SelectFrames(replacement, frameSize, withInserts);
        }

        return null;
    }

    /// <summary>Rebuilds a frame strip by picking (and possibly repeating) frames from <paramref name="source"/> by index.</summary>
    private static Image<Rgba32> SelectFrames(Image<Rgba32> source, int frameSize, int[] sourceFrameIndices)
    {
        var result = new Image<Rgba32>(frameSize, frameSize * sourceFrameIndices.Length);
        for (var f = 0; f < sourceFrameIndices.Length; f++)
        {
            var srcFrame = sourceFrameIndices[f];
            for (var y = 0; y < frameSize; y++)
                for (var x = 0; x < frameSize; x++)
                    result[x, f * frameSize + y] = source[x, srcFrame * frameSize + y];
        }
        return result;
    }

    /// <summary>
    /// Reverse-composites a front/back/HM-use slot: rebuilds a fresh NCLR
    /// palette from the pack's whole strip image (all frames share one
    /// palette, same as the vanilla file), then re-encodes every bank's
    /// cells from the matching strip frame via <see cref="NcerCompositor.EncodeBank"/>.
    /// Always targets the ROM's hero sprite/palette/cell group, same as
    /// <see cref="TryOverworldSlot"/> - a pack only ever supplies one image
    /// per slot, matching the character it reskins.
    /// </summary>
    /// <returns>The freshly-built palette (null if the slot wasn't applied), so a "duplicate" sprite pairing with the same palette member can reuse the exact same colours instead of re-quantizing.</returns>
    private static List<Rgba32>? TryCompositeSlot(
        byte[] rom, string packDir, GraphicsPackStanza stanza, string name,
        Gen4CompositeSlot slot, List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
    {
        var fileName = stanza.Get(slot.PackKey);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            results.Add(new(name, false, "not provided by pack"));
            return null;
        }

        var path = Path.Combine(packDir, fileName);
        if (!File.Exists(path))
        {
            results.Add(new(name, false, $"file not found: {fileName}"));
            return null;
        }

        var narcRange = NitroFs.ResolveFile(rom, slot.NarcPath);
        if (narcRange is null)
        {
            results.Add(new(name, false, $"NARC not found at {slot.NarcPath}"));
            return null;
        }

        var narc = new byte[narcRange.Value.Length];
        Array.Copy(rom, narcRange.Value.Offset, narc, 0, narc.Length);

        (int Offset, int Length) spriteRange, paletteRange, cellRange;
        byte[] ncgr, nclr, ncer;
        try
        {
            spriteRange = NarcArchive.GetMemberRange(narc, slot.HeroSprite);
            ncgr = new byte[spriteRange.Length];
            Array.Copy(narc, spriteRange.Offset, ncgr, 0, ncgr.Length);

            paletteRange = NarcArchive.GetMemberRange(narc, slot.HeroPalette);
            nclr = new byte[paletteRange.Length];
            Array.Copy(narc, paletteRange.Offset, nclr, 0, nclr.Length);

            cellRange = NarcArchive.GetMemberRange(narc, slot.HeroCell);
            ncer = new byte[cellRange.Length];
            Array.Copy(narc, cellRange.Offset, ncer, 0, ncer.Length);
        }
        catch (Exception ex)
        {
            results.Add(new(name, false, $"couldn't locate NARC members: {ex.Message}"));
            return null;
        }

        var lh = NcgrSpriteCodec.ReadNclrHeader(nclr);
        var originalPalette = new Rgba32[lh.ColorCount];
        for (var i = 0; i < lh.ColorCount; i++)
        {
            originalPalette[i] = NitroColor.FromBgr555(BitConverter.ToUInt16(nclr, lh.ColorsOffset + i * 2));
        }

        List<NcerCompositor.Bank> banks;
        int[] groups;
        try
        {
            banks = NcerCompositor.ReadBanks(ncer);
            groups = NcerCompositor.GroupDuplicateBanks(ncgr, originalPalette, banks, slot.CanvasSize);
        }
        catch (Exception ex)
        {
            results.Add(new(name, false, $"couldn't read cell data: {ex.Message}"));
            return null;
        }

        var frameCount = groups.Length == 0 ? 0 : groups[^1] + 1;
        var expectedHeight = slot.CanvasSize * frameCount;

        Image<Rgba32> replacement;
        try
        {
            replacement = Image.Load<Rgba32>(path);
        }
        catch (Exception ex)
        {
            results.Add(new(name, false, $"couldn't read {fileName}: {ex.Message}"));
            return null;
        }

        // A single pose for a multi-frame animation (e.g. the pack's 80x80 player front pic drawn
        // over the Connect champion's 4-frame trainer-class pic): hold that pose on every frame.
        if (frameCount > 1 && replacement.Width == slot.CanvasSize && replacement.Height == slot.CanvasSize)
        {
            var single = replacement;
            replacement = new Image<Rgba32>(slot.CanvasSize, expectedHeight);
            // The pack's backdrop colour (its corner) is the player palette's own index 0, not
            // necessarily this sprite's, so turn it transparent - alpha 0 always maps to slot 0.
            var backdrop = single[0, 0];
            for (var f = 0; f < frameCount; f++)
            {
                for (var y = 0; y < slot.CanvasSize; y++)
                {
                    for (var x = 0; x < slot.CanvasSize; x++)
                    {
                        var px = single[x, y];
                        replacement[x, f * slot.CanvasSize + y] = px == backdrop ? new Rgba32(0, 0, 0, 0) : px;
                    }
                }
            }
            single.Dispose();
        }

        List<Rgba32> newPalette;
        using (replacement)
        {
            if (replacement.Width != slot.CanvasSize || replacement.Height != expectedHeight)
            {
                results.Add(new(name, false,
                    $"{fileName} is {replacement.Width}x{replacement.Height}, expected {slot.CanvasSize}x{expectedHeight} ({frameCount} frame(s))"));
                return null;
            }

            // Background is whatever color index 0 actually stores in the
            // ROM's own palette - NOT the strip's own corner pixel. Those
            // usually agree (a composite's corner is typically outside every
            // cell's coverage, hence index-0-coloured), but data/field_cutin.narc's
            // hero/heroine sprite has a big index-0-filled "photo backdrop"
            // cell that does NOT reach the canvas corner - so the corner is a
            // truly uncovered pixel (alpha 0 from the extractor's own canvas
            // default) while the backdrop is a separate, opaque-looking tan.
            // Sampling the corner alone left the backdrop's tan uncategorised,
            // so the quantizer gave it its own real slot (1) instead of
            // recognising it as the hardware-transparent slot 0 - the backdrop
            // then rendered as a solid box in-game instead of see-through,
            // and index 1's real (unrelated) colour got overwritten. Using
            // the ROM's own index-0 colour as the reference catches the
            // backdrop; the ResolveIndex check below catches the corner-style
            // case (alpha 0) directly, so either convention resolves right.
            var background = originalPalette[0];

            // The pack's own opaque backdrop (its corner) can differ from this ROM's slot-0 colour -
            // e.g. art extracted from a ROM hack that recoloured the palette's slot 0 (green instead
            // of vanilla tan). Left alone, that backdrop would take a real palette slot and draw as a
            // solid box, so fold it into the background colour first.
            var corner = replacement[0, 0];
            if (corner.A != 0 && corner != background)
            {
                for (var y = 0; y < replacement.Height; y++)
                    for (var x = 0; x < replacement.Width; x++)
                        if (replacement[x, y] == corner) replacement[x, y] = background;
            }

            // Some sprites (confirmed for data/field_cutin.narc's Fly-cutscene
            // actor) don't use every one of their 16 palette slots - the
            // leftovers are reserved for a companion object (the bird/clouds)
            // sharing the same OBJ palette bank on real hardware. Quantizing
            // and writing only the slots this sprite's own tiles actually
            // reference, and leaving everything else exactly as the ROM had
            // it, keeps that companion object's colours intact instead of
            // letting the quantizer repurpose its reserved slots.
            var usedIndices = NcerCompositor.ComputeUsedIndices(ncgr, banks).OrderBy(x => x).ToList();
            var quantized = PaletteQuantizer.BuildPalette(replacement, background, usedIndices.Count);
            var exactIndex = new Dictionary<Rgba32, int>();
            for (var i = 0; i < quantized.Count; i++) exactIndex[quantized[i]] = i;

            int ResolveIndex(Rgba32 color)
            {
                // A fully-transparent pixel is the extractor's OWN canvas
                // default for "no cell covers this spot at all" (distinct
                // from an opaque-but-background-tinted pixel, handled by the
                // exact/nearest match below via originalPalette[0]) - always
                // the hardware-transparent slot regardless of which used
                // index that happens to be.
                if (color.A == 0) return 0;
                var pos = exactIndex.TryGetValue(color, out var exact) ? exact : PaletteQuantizer.NearestIndex(quantized, color);
                return usedIndices[pos];
            }

            for (var b = 0; b < banks.Count; b++)
            {
                using var frame = replacement.Clone(ctx => ctx.Crop(new Rectangle(0, groups[b] * slot.CanvasSize, slot.CanvasSize, slot.CanvasSize)));
                NcerCompositor.EncodeBank(ncgr, ResolveIndex, frame, banks[b], slot.CanvasSize);
            }

            // BuildPalette can return fewer colours than asked for when the
            // replacement image doesn't have that many distinct ones - bound
            // by its actual count, not the requested usedIndices.Count, or
            // this indexes past the end of quantized.
            newPalette = [.. originalPalette];
            for (var i = 0; i < quantized.Count; i++)
            {
                var rawIndex = usedIndices[i];
                newPalette[rawIndex] = quantized[i];
                var bgr555 = NitroColor.ToBgr555(quantized[i]);
                nclr[lh.ColorsOffset + rawIndex * 2] = (byte)bgr555;
                nclr[lh.ColorsOffset + rawIndex * 2 + 1] = (byte)(bgr555 >> 8);
            }
        }

        edits.Add((narcRange.Value.Offset + spriteRange.Offset, ncgr));
        edits.Add((narcRange.Value.Offset + paletteRange.Offset, nclr));
        results.Add(new(name, true, null));
        return newPalette;
    }

    /// <summary>
    /// Writes the enciphered Front sprite (see <see cref="Gen4CipheredSpriteCodec"/>):
    /// the pack's front.png in the left 80x80 half, palette index 0 filling
    /// the right half (unused for Front - vanilla's own right half doesn't
    /// resemble anything recognisable either). Serves two roles depending on
    /// <paramref name="palette"/>'s source: HGSS/Platinum's "duplicate" layer
    /// (borrowing the NCER composite Front's own palette) and Diamond/Pearl's
    /// primary Front (no NCER composite exists there, so the caller builds a
    /// fresh palette instead) - see <see cref="BuildPaletteFromPack"/>.
    /// </summary>
    private static void TryFrontDuplicateSlot(
        byte[] rom, string packDir, GraphicsPackStanza stanza, Gen4DuplicateSlot slot, List<Rgba32> palette,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results, string name)
    {
        var fileName = stanza.Get("FrontImage");
        if (string.IsNullOrWhiteSpace(fileName)) { results.Add(new(name, false, "not provided by pack")); return; }

        var path = Path.Combine(packDir, fileName);
        if (!File.Exists(path)) { results.Add(new(name, false, $"file not found: {fileName}")); return; }

        var narcRange = NitroFs.ResolveFile(rom, slot.NarcPath);
        if (narcRange is null) { results.Add(new(name, false, $"NARC not found at {slot.NarcPath}")); return; }

        var narc = new byte[narcRange.Value.Length];
        Array.Copy(rom, narcRange.Value.Offset, narc, 0, narc.Length);

        (int Offset, int Length) spriteRange, paletteRange;
        byte[] ncgr, nclr;
        try
        {
            spriteRange = NarcArchive.GetMemberRange(narc, slot.HeroSprite);
            ncgr = new byte[spriteRange.Length];
            Array.Copy(narc, spriteRange.Offset, ncgr, 0, ncgr.Length);

            paletteRange = NarcArchive.GetMemberRange(narc, slot.HeroPalette);
            nclr = new byte[paletteRange.Length];
            Array.Copy(narc, paletteRange.Offset, nclr, 0, nclr.Length);
        }
        catch (Exception ex)
        {
            results.Add(new(name, false, $"couldn't locate NARC members: {ex.Message}"));
            return;
        }

        using var front = Image.Load<Rgba32>(path);
        if (front.Width != 80 || front.Height != 80)
        {
            results.Add(new(name, false, $"front.png is {front.Width}x{front.Height}, expected 80x80"));
            return;
        }

        using var canvas = new Image<Rgba32>(160, 80);
        for (var y = 0; y < 80; y++)
            for (var x = 0; x < 80; x++)
                canvas[x, y] = front[x, y];
        for (var y = 0; y < 80; y++)
            for (var x = 80; x < 160; x++)
                canvas[x, y] = palette[0];

        // The pack's backdrop (its corner colour) is transparent slot 0 even when the target
        // palette's slot 0 is a different colour (the Connect champion drawn over Cynthia's pic).
        var backdrop = front[0, 0];
        int ResolveIndex(Rgba32 color)
        {
            if (color.A == 0 || color == backdrop) return 0;
            for (var i = 0; i < palette.Count; i++) if (palette[i] == color) return i;
            return PaletteQuantizer.NearestIndex(palette, color);
        }

        Gen4CipheredSpriteCodec.Encode(ncgr, canvas, ResolveIndex);

        edits.Add((narcRange.Value.Offset + spriteRange.Offset, ncgr));
        AddPaletteEditIfNotAlreadyWritten(edits, narcRange.Value.Offset + paletteRange.Offset, nclr, palette);
        results.Add(new(name, true, null));
    }

    /// <summary>
    /// Writes one enciphered "duplicate" Back sprite - the pack's back.png
    /// frame 0 (its idle/starting pose) in BOTH 80x80 halves, not the
    /// "Throwing"/"Pointing" poses the member names suggest. This layer is
    /// drawn underneath the real animated NCER sprite for the first couple
    /// frames of the send-out (confirmed against real hardware: a mismatched
    /// pose here shows as a permanent ghosted double, for as long as the
    /// underlying animation's current frame doesn't happen to match whatever
    /// static pose we put here) - matching the main animation's own first
    /// held frame is what makes the two layers overlap invisibly instead of
    /// visibly doubling. Called once per <see cref="Gen4PlayerGraphicsOffsets.Revision.BackDuplicates"/>
    /// entry (HGSS has two, both wanting this). Diamond/Pearl also uses this
    /// for trbgra's pair, sharing the fresh palette <see cref="TryBattObjBackSlot"/>
    /// just built for the real batt_obj animation instead of borrowing from
    /// an NCER composite Back slot (DP has none).
    /// </summary>
    private static void TryBackDuplicateSlot(
        byte[] rom, string packDir, GraphicsPackStanza stanza, Gen4DuplicateSlot slot, List<Rgba32> palette,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results, string name)
    {
        var fileName = stanza.Get("BackImage");
        if (string.IsNullOrWhiteSpace(fileName)) { results.Add(new(name, false, "not provided by pack")); return; }

        var path = Path.Combine(packDir, fileName);
        if (!File.Exists(path)) { results.Add(new(name, false, $"file not found: {fileName}")); return; }

        var narcRange = NitroFs.ResolveFile(rom, slot.NarcPath);
        if (narcRange is null) { results.Add(new(name, false, $"NARC not found at {slot.NarcPath}")); return; }

        var narc = new byte[narcRange.Value.Length];
        Array.Copy(rom, narcRange.Value.Offset, narc, 0, narc.Length);

        (int Offset, int Length) spriteRange, paletteRange;
        byte[] ncgr, nclr;
        try
        {
            spriteRange = NarcArchive.GetMemberRange(narc, slot.HeroSprite);
            ncgr = new byte[spriteRange.Length];
            Array.Copy(narc, spriteRange.Offset, ncgr, 0, ncgr.Length);

            paletteRange = NarcArchive.GetMemberRange(narc, slot.HeroPalette);
            nclr = new byte[paletteRange.Length];
            Array.Copy(narc, paletteRange.Offset, nclr, 0, nclr.Length);
        }
        catch (Exception ex)
        {
            results.Add(new(name, false, $"couldn't locate NARC members: {ex.Message}"));
            return;
        }

        using var back = Image.Load<Rgba32>(path);
        if (back.Width != 80 || back.Height % 80 != 0 || back.Height == 0)
        {
            results.Add(new(name, false, $"back.png is {back.Width}x{back.Height}, expected 80-wide, a multiple of 80 tall"));
            return;
        }

        using var canvas = new Image<Rgba32>(160, 80);
        for (var y = 0; y < 80; y++)
        {
            for (var x = 0; x < 80; x++)
            {
                var pixel = back[x, y]; // frame 0 - the animation's own starting pose
                canvas[x, y] = pixel;
                canvas[80 + x, y] = pixel;
            }
        }

        // The pack's backdrop (its corner colour) is transparent slot 0 even when it isn't the
        // palette's slot-0 colour (e.g. art extracted from a hack with a recoloured slot 0) - same as
        // TryFrontDuplicateSlot.
        var backdrop = back[0, 0];
        int ResolveIndex(Rgba32 color)
        {
            if (color.A == 0 || color == backdrop) return 0;
            for (var i = 0; i < palette.Count; i++) if (palette[i] == color) return i;
            return PaletteQuantizer.NearestIndex(palette, color);
        }

        Gen4CipheredSpriteCodec.Encode(ncgr, canvas, ResolveIndex);

        edits.Add((narcRange.Value.Offset + spriteRange.Offset, ncgr));
        AddPaletteEditIfNotAlreadyWritten(edits, narcRange.Value.Offset + paletteRange.Offset, nclr, palette);
        results.Add(new(name, true, null));
    }

    /// <summary>
    /// Diamond/Pearl's real Back animation (see <see cref="Gen4BattObjBackSlot"/>):
    /// unlike every other Back slot, its sprite data is LZ10-compressed
    /// inside battle/graphic/batt_obj.narc rather than a bare NCGR. The cell
    /// layout (<see cref="Gen4BattObjBackSlot.CellMember"/>) is read but never
    /// rewritten - only pixel content changes, not per-bank positioning, so
    /// re-encoding via <see cref="NcerCompositor.EncodeBank"/> against the
    /// existing decompressed cell data is enough. The recompressed sprite is
    /// zero-padded up to the ORIGINAL compressed member's length (LZ10's
    /// decoder stops exactly at its own header's declared decompressed size,
    /// so trailing zero bytes past the real stream are simply never read) -
    /// if the freshly-compressed data doesn't fit in that budget the slot is
    /// skipped rather than growing the NARC (every other Gen4 edit in this
    /// codebase is a same-size in-place overwrite; there's no relocation/
    /// free-space bookkeeping here to support a larger member).
    /// </summary>
    /// <returns>The freshly-built palette (null if the slot wasn't applied), so trbgra's "duplicate" pairing can reuse the exact same colours.</returns>
    private static List<Rgba32>? TryBattObjBackSlot(
        byte[] rom, string packDir, GraphicsPackStanza stanza, Gen4BattObjBackSlot slot,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
    {
        const string name = "Back";
        var fileName = stanza.Get("BackImage");
        if (string.IsNullOrWhiteSpace(fileName)) { results.Add(new(name, false, "not provided by pack")); return null; }

        var path = Path.Combine(packDir, fileName);
        if (!File.Exists(path)) { results.Add(new(name, false, $"file not found: {fileName}")); return null; }

        var battObjRange = NitroFs.ResolveFile(rom, slot.BattObjNarcPath);
        if (battObjRange is null) { results.Add(new(name, false, $"NARC not found at {slot.BattObjNarcPath}")); return null; }

        var battObjNarc = new byte[battObjRange.Value.Length];
        Array.Copy(rom, battObjRange.Value.Offset, battObjNarc, 0, battObjNarc.Length);

        var trbgraRange = NitroFs.ResolveFile(rom, slot.DuplicateNarcPath);
        if (trbgraRange is null) { results.Add(new(name, false, $"NARC not found at {slot.DuplicateNarcPath}")); return null; }

        var trbgraNarc = new byte[trbgraRange.Value.Length];
        Array.Copy(rom, trbgraRange.Value.Offset, trbgraNarc, 0, trbgraNarc.Length);

        (int Offset, int Length) spriteMemberRange, cellMemberRange, paletteRange;
        byte[] ncgr, ncer, nclr;
        try
        {
            spriteMemberRange = NarcArchive.GetMemberRange(battObjNarc, slot.HeroSpriteMember);
            (ncgr, _) = Lz10Codec.Decompress(battObjNarc, spriteMemberRange.Offset);

            cellMemberRange = NarcArchive.GetMemberRange(battObjNarc, slot.CellMember);
            (ncer, _) = Lz10Codec.Decompress(battObjNarc, cellMemberRange.Offset);

            paletteRange = NarcArchive.GetMemberRange(trbgraNarc, slot.HeroPalette);
            nclr = new byte[paletteRange.Length];
            Array.Copy(trbgraNarc, paletteRange.Offset, nclr, 0, nclr.Length);
        }
        catch (Exception ex)
        {
            results.Add(new(name, false, $"couldn't locate NARC members: {ex.Message}"));
            return null;
        }

        List<NcerCompositor.Bank> banks;
        try
        {
            banks = NcerCompositor.ReadBanks(ncer);
        }
        catch (Exception ex)
        {
            results.Add(new(name, false, $"couldn't read cell data: {ex.Message}"));
            return null;
        }

        var expectedHeight = slot.CanvasSize * banks.Count;

        Image<Rgba32> replacement;
        try
        {
            replacement = Image.Load<Rgba32>(path);
        }
        catch (Exception ex)
        {
            results.Add(new(name, false, $"couldn't read {fileName}: {ex.Message}"));
            return null;
        }

        List<Rgba32> newPalette;
        byte[] recompressed;
        using (replacement)
        {
            if (replacement.Width != slot.CanvasSize || replacement.Height != expectedHeight)
            {
                results.Add(new(name, false,
                    $"{fileName} is {replacement.Width}x{replacement.Height}, expected {slot.CanvasSize}x{expectedHeight} ({banks.Count} frame(s))"));
                return null;
            }

            var background = replacement[0, 0];
            var lh = NcgrSpriteCodec.ReadNclrHeader(nclr);
            newPalette = PaletteQuantizer.BuildPalette(replacement, background, lh.ColorCount);
            var exactIndex = new Dictionary<Rgba32, int>();
            for (var i = 0; i < newPalette.Count; i++) exactIndex[newPalette[i]] = i;

            int ResolveIndex(Rgba32 color) =>
                exactIndex.TryGetValue(color, out var exact) ? exact : PaletteQuantizer.NearestIndex(newPalette, color);

            for (var b = 0; b < banks.Count; b++)
            {
                using var frame = replacement.Clone(ctx => ctx.Crop(new Rectangle(0, b * slot.CanvasSize, slot.CanvasSize, slot.CanvasSize)));
                NcerCompositor.EncodeBank(ncgr, ResolveIndex, frame, banks[b], slot.CanvasSize);
            }

            for (var c = 0; c < newPalette.Count; c++)
            {
                var bgr555 = NitroColor.ToBgr555(newPalette[c]);
                nclr[lh.ColorsOffset + c * 2] = (byte)bgr555;
                nclr[lh.ColorsOffset + c * 2 + 1] = (byte)(bgr555 >> 8);
            }

            recompressed = Lz10Codec.Compress(ncgr);
            if (recompressed.Length > spriteMemberRange.Length)
            {
                results.Add(new(name, false,
                    $"recompressed sprite is {recompressed.Length} bytes, ROM only has {spriteMemberRange.Length}"));
                return null;
            }
        }

        var padded = new byte[spriteMemberRange.Length];
        Array.Copy(recompressed, padded, recompressed.Length);

        edits.Add((battObjRange.Value.Offset + spriteMemberRange.Offset, padded));
        AddPaletteEditIfNotAlreadyWritten(edits, trbgraRange.Value.Offset + paletteRange.Offset, nclr, newPalette);
        results.Add(new(name, true, null));
        return newPalette;
    }

    /// <summary>
    /// A "duplicate" sprite's palette member is often the exact same member
    /// its normal composited sprite already wrote (e.g. HGSS's Front and its
    /// FrontDuplicate both use member 1) - adding a second edit at that same
    /// offset would corrupt <see cref="BpsPatchService.EncodeSparse"/>'s
    /// output (it assumes a non-overlapping edit list; two edits at the
    /// identical offset make its second TargetRead land past where the first
    /// one already advanced the cursor, shifting everything after it). Only
    /// write the palette here when nothing else already claimed that offset.
    /// </summary>
    private static void AddPaletteEditIfNotAlreadyWritten(
        List<(int Offset, byte[] Data)> edits, int absoluteOffset, byte[] nclr, List<Rgba32> palette)
    {
        if (edits.Any(e => e.Offset == absoluteOffset)) return;

        var lh = NcgrSpriteCodec.ReadNclrHeader(nclr);
        for (var c = 0; c < Math.Min(palette.Count, lh.ColorCount); c++)
        {
            var bgr555 = NitroColor.ToBgr555(palette[c]);
            nclr[lh.ColorsOffset + c * 2] = (byte)bgr555;
            nclr[lh.ColorsOffset + c * 2 + 1] = (byte)(bgr555 >> 8);
        }
        edits.Add((absoluteOffset, nclr));
    }
}
