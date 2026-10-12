using PKConnect.Core.formats;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using PKConnect.Core.patching;

namespace PKConnect.Core.graphics;

/// <summary>
/// Player graphics for pokeemerald-expansion builds - Brendan on the Emerald
/// build (RSE pack stanza), Red on the FireRed/LeafGreen builds (FRLG stanza),
/// the same sprites as the retail games (<see cref="Gen3PlayerGraphicsWriter"/>).
/// A compiled hack has no fixed addresses and a different data layout, so
/// everything is resolved from the build's own <c>.elf</c> symbol table (next to
/// the ROM) instead:
///
///  - Overworld pic tables reference each sprite's tile blob directly (often one
///    <c>overworld_ascending_frames</c> entry per sprite), so frames are written
///    straight into the <c>gObjectEventPic_*</c> blobs (uncompressed 4bpp,
///    consecutive frames) instead of through a flat per-frame table.
///  - Overworld palettes are raw <c>gObjectEventPal_*</c> arrays, edited in place.
///  - Front pics (and the FRLG Oak-speech portrait) are "smol"-compressed; the
///    loader dispatches on the header's mode nibble and still accepts LZ77
///    (mode 0), so new LZ10 data goes to free space and every pointer to the old
///    data is repointed. Trainer palettes are raw; each pic gets its own
///    relocated copy.
///  - The back pic is uncompressed and edited in place.
///  - The region-map icon is raw image + palette, edited in place.
///
/// Free space sits past 16MB (builds fill the ROM out to ~25MB), which IPS can't
/// address - the launch route writes a .bps for these games.
/// </summary>
public static class ExpansionPlayerGraphicsWriter
{
    private const int FrontTiles = 8; // 64x64

    /// <summary>
    /// Overworld slots: <see cref="Gen3OverworldSlot.StartIndex"/> / WriteFrameOrder
    /// are frame positions inside <see cref="Symbol"/>'s own tile data (source
    /// order), not flat pic-table indices like the retail slots.
    /// </summary>
    private sealed record Slot(string Symbol, Gen3OverworldSlot Overworld);

    private sealed record Layout(
        string RunSpriteOrder,
        string NormalPalette, string ReflectionPalette, string? UnderwaterPalette,
        string FrontPic, string FrontPalette,
        string BackPic, string BackPalette, int BackFrames,
        string MapIconGfx, string MapIconPal,
        string? IntroTiles, string? IntroPalette,
        string ChampionFrontPic, string ChampionFrontPalette,
        string? ChampionOverworldPic, string? ChampionOverworldPalette,
        IReadOnlyList<Slot> Slots)
    {
        /// <summary>Symbols whose ELF bytes must equal the ROM's for the .elf to count as this ROM's build.</summary>
        public string[] MatchSymbols => [NormalPalette, FrontPic, MapIconPal];
    }

    private static readonly Layout Rse = new(
        RunSpriteOrder: "rse",
        NormalPalette: "gObjectEventPal_Brendan",
        ReflectionPalette: "gObjectEventPal_BrendanReflection",
        UnderwaterPalette: "gObjectEventPal_PlayerUnderwater",
        FrontPic: "gTrainerFrontPic_Brendan", FrontPalette: "gTrainerPalette_Brendan",
        // TRAINER_BACK_PIC(4, gTrainerBackPic_Brendan, gTrainerPalette_Brendan, ...) - front and back share it.
        BackPic: "gTrainerBackPic_Brendan", BackPalette: "gTrainerPalette_Brendan", BackFrames: 4,
        MapIconGfx: "sRegionMapPlayerIcon_BrendanGfx", MapIconPal: "sRegionMapPlayerIcon_BrendanPal",
        IntroTiles: null, IntroPalette: null, // Birch's intro shows no player portrait
        // TRAINER_PIC_CHAMPION_WALLACE: only the League champion battle (and its intro mugshot) uses it.
        ChampionFrontPic: "gTrainerFrontPic_ChampionWallace", ChampionFrontPalette: "gTrainerPalette_ChampionWallace",
        // Connect builds: the Champion's Room / Hall of Fame placeholder (src/data/object_events/connect_champion.h).
        ChampionOverworldPic: "gConnectChampionOwPic", ChampionOverworldPalette: "gConnectChampionOwPal",
        Slots:
        [
            // walking.4bpp + running.4bpp are one blob: 9 walk frames, then 9 run.
            new("gObjectEventPic_BrendanNormalRunning", new("Walk", "WalkSprite", 0, 16, 32, MaxFrames: 9)),
            new("gObjectEventPic_BrendanNormalRunning", new("Run", "RunSprite", 9, 16, 32, MaxFrames: 9)),
            new("gObjectEventPic_BrendanMachBike", new("Bike", "BikeSprite", 0, 32, 32, [new("MachBikeSprite", 32)], MaxFrames: 9)),
            new("gObjectEventPic_BrendanAcroBike", new("AcroBike", "AcroBikeSprite", 0, 32, 32, MaxFrames: 27)),
            // surfing.png source order: sit-S, jump-S, sit-N, jump-N, sit-W, jump-W.
            new("gObjectEventPic_BrendanSurfing", new("Sit", "SitSprite", 0, 32, 32,
                Combined: new("SurfingSprite", [0, 2, 4]), WriteFrameOrder: [0, 2, 4])),
            new("gObjectEventPic_BrendanSurfing", new("SitJump", "SitJumpSprite", 0, 32, 32,
                Combined: new("SurfingSprite", [1, 3, 5]), WriteFrameOrder: [1, 3, 5])),
            // 3 real poses (S/N/W); the 4th source frame is unused.
            new("gObjectEventPic_BrendanUnderwater", new("Underwater", "UnderwaterSprite", 0, 32, 32, WriteFrameOrder: [0, 1, 2])),
            new("gObjectEventPic_BrendanFieldMove", new("FieldMove", "FieldMoveSprite", 0, 32, 32, [new("ItemSprite", 16)], MaxFrames: 5)),
            new("gObjectEventPic_BrendanFishing", new("Fish", "FishSprite", 0, 32, 32)),
            new("gObjectEventPic_BrendanWatering", new("WateringCan", "WateringCanSprite", 0, 32, 32, MaxFrames: 6)),
            new("gObjectEventPic_BrendanDecorating", new("Decorate", "DecorateSprite", 0, 16, 32, MaxFrames: 1)),
        ]);

    private static readonly Layout Frlg = new(
        RunSpriteOrder: "frlg",
        // OBJ_EVENT_PAL_TAG_PLAYER_RED - shared with Green, as on retail FRLG.
        NormalPalette: "gObjectEventPal_PlayerFrlg",
        ReflectionPalette: "gObjectEventPal_PlayerReflectionFrlg",
        UnderwaterPalette: null,
        FrontPic: "gTrainerFrontPic_Red", FrontPalette: "gTrainerPalette_Red",
        BackPic: "gTrainerBackPic_Red", BackPalette: "gTrainerBackPicPalette_Red", BackFrames: 5,
        MapIconGfx: "sRegionMapPlayerIcon_RedGfx", MapIconPal: "sRegionMapPlayerIcon_RedPal",
        IntroTiles: "sOakSpeech_Red_Tiles", IntroPalette: "sOakSpeech_Red_Pal",
        // "Champion Rival Frlg": the League champion battles (first and rematch) only.
        ChampionFrontPic: "gTrainerFrontPic_ChampionRivalFrlg", ChampionFrontPalette: "gTrainerPalette_ChampionRivalFrlg",
        // The Champion's Room rival object uses the Connect placeholder (a copy of Blue) in FRLG builds.
        ChampionOverworldPic: "gConnectChampionOwPic", ChampionOverworldPalette: "gConnectChampionOwPal",
        Slots:
        [
            // Same blobs as retail: sPicTable_RedNormal = red_normal 0..8 + red_surf_run 3..;
            // sPicTable_RedSurf = red_surf_run 0..2 (the seated surf pose).
            new("gObjectEventPic_RedNormal", new("Walk", "WalkSprite", 0, 16, 32, MaxFrames: 9)),
            new("gObjectEventPic_RedSurfRun", new("Run", "RunSprite", 3, 16, 32)),
            new("gObjectEventPic_RedSurfRun", new("Sit", "SitSprite", 0, 16, 32,
                Combined: new("SurfingSprite", [0, 2, 4]), WriteFrameOrder: [0, 1, 2])),
            new("gObjectEventPic_RedBike", new("Bike", "BikeSprite", 0, 32, 32, [new("MachBikeSprite", 32)])),
            // FRLG's item/field-move sprite is 16px wide; RSE's field_move.png is 32px.
            new("gObjectEventPic_RedItem", new("Item", "ItemSprite", 0, 16, 32, [new("FieldMoveSprite", 32)], MaxFrames: 9)),
            new("gObjectEventPic_RedVSSeekerBike", new("ItemBike", "ItemBikeSprite", 0, 32, 32, MaxFrames: 6)),
            new("gObjectEventPic_RedFish", new("Fish", "FishSprite", 0, 32, 32)),
        ]);

    private static Layout LayoutFor(string gameId) =>
        ExpansionSaveDetector.BaseVersion(gameId) is PKHeX.Core.GameVersion.FR or PKHeX.Core.GameVersion.LG ? Frlg : Rse;

    /// <summary>
    /// Finds the build's <c>.elf</c> next to <paramref name="romPath"/> (same
    /// name first, then any other .elf in that folder) whose data matches this
    /// ROM. Null when there isn't one - without symbols nothing can be located.
    /// </summary>
    public static ElfSymbolTable? FindSymbols(string gameId, string romPath, byte[] rom)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(romPath));
        if (dir is null)
        {
            return null;
        }
        var sameName = Path.ChangeExtension(Path.GetFullPath(romPath), ".elf");
        var candidates = new List<string>();
        if (File.Exists(sameName))
        {
            candidates.Add(sameName);
        }
        candidates.AddRange(Directory.EnumerateFiles(dir, "*.elf")
            .Where(p => !string.Equals(p, sameName, StringComparison.OrdinalIgnoreCase)));

        var layout = LayoutFor(gameId);
        foreach (var candidate in candidates)
        {
            if (ElfSymbolTable.TryLoad(candidate) is { } elf && MatchesRom(elf, rom, layout))
            {
                return elf;
            }
        }
        return null;
    }

    /// <summary>The ROM came from this build: a few player symbols' bytes match the ELF's.</summary>
    private static bool MatchesRom(ElfSymbolTable elf, byte[] rom, Layout layout)
    {
        foreach (var name in layout.MatchSymbols)
        {
            var sym = elf.Get(name);
            var off = elf.RomOffset(name);
            if (sym is null || off is null || off.Value + 32 > rom.Length)
            {
                return false;
            }
            var expected = elf.ReadBytes(sym, 32);
            if (expected is null || !rom.AsSpan(off.Value, 32).SequenceEqual(expected))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>First ROM offset the build doesn't use (<c>__rom_end</c>), else past the last non-0xFF byte.</summary>
    public static int FreeSpaceStart(ElfSymbolTable elf, byte[] rom)
    {
        if (elf.Get("__rom_end") is { } end && end.Address > Gba.RomBase && end.Address - Gba.RomBase < rom.Length)
        {
            return (int)(end.Address - Gba.RomBase + 3) & ~3;
        }
        var last = rom.Length - 1;
        while (last > 0 && rom[last] == 0xFF)
        {
            last--;
        }
        return (last + 4) & ~3;
    }

    public static (List<(int Offset, byte[] Data)> Edits, List<PlayerGraphicsSlotResult> Results)
        BuildEdits(string gameId, byte[] rom, string? romPath, GraphicsPackStanza stanza, string packDir,
            IReadOnlyList<(int Start, int End)>? reservedRanges = null)
    {
        var edits = new List<(int Offset, byte[] Data)>();
        var results = new List<PlayerGraphicsSlotResult>();
        var layout = LayoutFor(gameId);

        var elf = romPath is null ? null : FindSymbols(gameId, romPath, rom);
        if (elf is null)
        {
            results.Add(new("*", false,
                "No matching pokeemerald-expansion build .elf next to the ROM - the sprite addresses of an expansion build can only be found from its symbols"));
            return (edits, results);
        }

        var allocator = new RomFreeSpaceAllocator(rom, FreeSpaceStart(elf, rom), reservedRanges);

        // --- overworld palettes (raw, in place) ---
        var normal = Gen3PlayerGraphicsWriter.ReadJascPal(packDir, stanza.Get("SpriteNormalPalette"))
                     ?? Gen3PlayerGraphicsWriter.DerivePalette(packDir, stanza.Get("WalkSprite"));
        if (normal is null)
        {
            results.Add(new("*", false, "no usable normal sprite palette (walk sheet isn't 16-colour indexed)"));
            return (edits, results);
        }
        var reflection = Gen3PlayerGraphicsWriter.ReadJascPal(packDir, stanza.Get("SpriteReflectionPalette")) ?? normal.DeriveReflection();
        var underwater = layout.UnderwaterPalette is null ? null
            : Gen3PlayerGraphicsWriter.DerivePalette(packDir, stanza.Get("UnderwaterSprite")) ?? normal;

        var palettesWritten = new List<string>();
        foreach (var (symbol, pal, label) in new[]
        {
            (layout.NormalPalette, normal, "normal"),
            (layout.ReflectionPalette, reflection, "reflection"),
            (layout.UnderwaterPalette, underwater, "underwater"),
        })
        {
            if (symbol is not null && pal is not null && WriteInPlace(rom, elf, symbol, pal.ToBytes(), edits))
            {
                palettesWritten.Add(label);
            }
        }
        results.Add(new("SpritePalette", palettesWritten.Count > 0, string.Join(" + ", palettesWritten)));

        // --- overworld frames (in place, straight into each sprite's tile data) ---
        var runPerm = Gen3PlayerGraphicsOffsets.RunFramePermutation(stanza.Get("RunSpriteMode"), layout.RunSpriteOrder);
        foreach (var (symbolName, slot) in layout.Slots)
        {
            var symbol = elf.Get(symbolName);
            var baseOff = elf.RomOffset(symbolName);
            if (symbol is null || baseOff is null)
            {
                results.Add(new(slot.Name, false, $"symbol {symbolName} not in this build"));
                continue;
            }

            var pal = slot.Name == "Underwater" && underwater is not null ? underwater : normal;
            var perm = slot.Name == "Run" ? runPerm : null;

            IReadOnlyList<int>? combinedFrames = null;
            int? sourceFrameWidth = null;
            string? file = null;
            if (slot.Combined is { } cs && Gen3PlayerGraphicsWriter.Blank(stanza.Get(cs.PackKey)) is { } combinedFile)
            {
                file = combinedFile;
                combinedFrames = cs.Frames;
            }
            file ??= Gen3PlayerGraphicsWriter.Blank(stanza.Get(slot.PackKey));
            if (file is null && slot.AltPackKeys is not null)
            {
                foreach (var alt in slot.AltPackKeys)
                {
                    if (Gen3PlayerGraphicsWriter.Blank(stanza.Get(alt.Key)) is { } altFile)
                    {
                        file = altFile;
                        sourceFrameWidth = alt.SourceFrameWidth;
                        break;
                    }
                }
            }

            var frameLen = slot.FrameByteLength;
            var symbolSize = (int)symbol.Size;
            var start = baseOff.Value;
            Gen3PlayerGraphicsWriter.TryOverworldSlotWith(rom, packDir, file, slot, pal, perm, combinedFrames,
                sourceFrameWidth, edits, results,
                targetIndex => (targetIndex + 1) * frameLen <= symbolSize && start + (targetIndex + 1) * frameLen <= rom.Length
                    ? start + targetIndex * frameLen
                    : null);
        }

        // --- battle sprites, map icon, intro portrait ---
        TryFrontImage(rom, elf, layout.FrontPic, layout.FrontPalette, packDir, stanza.Get("FrontImage"), allocator, edits, results);
        TryBackImage(rom, elf, layout, packDir, stanza.Get("BackImage"), allocator, edits, results);
        TryMapIcon(rom, elf, layout, packDir, stanza.Get("MapIcon"), edits, results);
        TryIntroImage(rom, elf, layout, packDir, stanza.Get("OakSpeechImage"), allocator, edits, results);

        Log.Information("[player-graphics] {Game} ({Elf}): {Applied} applied, {Skipped} skipped",
            gameId, Path.GetFileName(elf.Path), results.Count(r => r.Applied), results.Count(r => !r.Applied));

        return (edits, results);
    }

    private static bool WriteInPlace(byte[] rom, ElfSymbolTable elf, string symbol, byte[] data, List<(int Offset, byte[] Data)> edits)
    {
        var sym = elf.Get(symbol);
        var off = elf.RomOffset(symbol);
        if (sym is null || off is null || sym.Size < data.Length || off.Value + data.Length > rom.Length)
        {
            return false;
        }
        if (!rom.AsSpan(off.Value, data.Length).SequenceEqual(data))
        {
            edits.Add((off.Value, data));
        }
        return true;
    }

    /// <summary>Every 4-byte-aligned word in the ROM that is a pointer to <paramref name="fileOffset"/>.</summary>
    private static List<int> FindPointersTo(byte[] rom, int fileOffset)
    {
        var target = Gba.RomBase + (uint)fileOffset;
        var hits = new List<int>();
        for (var i = 0; i + 4 <= rom.Length; i += 4)
        {
            if (Gba.ReadU32(rom, i) == target)
            {
                hits.Add(i);
            }
        }
        return hits;
    }

    /// <summary>
    /// Repoints each <paramref name="picPointers"/> entry's palette: the first
    /// pointer to <paramref name="paletteOffset"/> within the same struct (the
    /// next 16 bytes - trainer pic structs keep the image and palette pointers
    /// together in every expansion layout seen so far).
    /// </summary>
    private static int RepointPalettes(byte[] rom, IEnumerable<int> picPointers, int paletteOffset, int newPalette,
        List<(int Offset, byte[] Data)> edits)
    {
        var target = Gba.RomBase + (uint)paletteOffset;
        var count = 0;
        foreach (var p in picPointers)
        {
            for (var q = p + 4; q <= p + 16 && q + 4 <= rom.Length; q += 4)
            {
                if (Gba.ReadU32(rom, q) == target)
                {
                    edits.Add((q, Gba.PointerBytes(newPalette)));
                    count++;
                    break;
                }
            }
        }
        return count;
    }

    private static int? AllocateAndWrite(RomFreeSpaceAllocator allocator, byte[] data, List<(int Offset, byte[] Data)> edits)
    {
        var dest = allocator.Allocate(data.Length);
        if (dest is null)
        {
            return null;
        }
        edits.Add((dest.Value, data));
        return dest;
    }

    /// <summary>
    /// The region's current champion's look on the League champion's battle front pic (Wallace /
    /// the FRLG champion rival): the champion trainer profile's pack FrontImage, relocated and
    /// repointed exactly like the player's own front pic.
    /// </summary>
    public static (List<(int Offset, byte[] Data)> Edits, List<PlayerGraphicsSlotResult> Results)
        BuildChampionEdits(string gameId, byte[] rom, string? romPath, GraphicsPackStanza stanza, string packDir,
            IReadOnlyList<(int Start, int End)>? reservedRanges = null)
    {
        var edits = new List<(int Offset, byte[] Data)>();
        var results = new List<PlayerGraphicsSlotResult>();
        var layout = LayoutFor(gameId);

        var elf = romPath is null ? null : FindSymbols(gameId, romPath, rom);
        if (elf is null)
        {
            results.Add(new("*", false, "No matching pokeemerald-expansion build .elf next to the ROM"));
            return (edits, results);
        }

        var allocator = new RomFreeSpaceAllocator(rom, FreeSpaceStart(elf, rom), reservedRanges);
        TryFrontImage(rom, elf, layout.ChampionFrontPic, layout.ChampionFrontPalette, packDir, stanza.Get("FrontImage"),
            allocator, edits, results);

        // Overworld: the Connect placeholder's own walking frames + palette, rewritten in place.
        if (layout.ChampionOverworldPic is { } owPic && layout.ChampionOverworldPalette is { } owPal
            && elf.Get(owPic) is { } picSym && elf.RomOffset(owPic) is { } picOff)
        {
            var palette = Gen3PlayerGraphicsWriter.ReadJascPal(packDir, stanza.Get("SpriteNormalPalette"))
                          ?? Gen3PlayerGraphicsWriter.DerivePalette(packDir, stanza.Get("WalkSprite"));
            if (palette is null || !WriteInPlace(rom, elf, owPal, palette.ToBytes(), edits))
            {
                results.Add(new("ChampionOverworld", false, "no usable walk-sheet palette / palette symbol"));
            }
            else
            {
                var slot = new Gen3OverworldSlot("ChampionWalk", "WalkSprite", 0, 16, 32, MaxFrames: 9);
                var frameLen = slot.FrameByteLength;
                var symbolSize = (int)picSym.Size;
                Gen3PlayerGraphicsWriter.TryOverworldSlotWith(rom, packDir, stanza.Get("WalkSprite"), slot, palette,
                    null, null, null, edits, results,
                    i => (i + 1) * frameLen <= symbolSize ? picOff + i * frameLen : null);
            }
        }
        return (edits, results);
    }

    private static void TryFrontImage(
        byte[] rom, ElfSymbolTable elf, string frontPic, string frontPalette, string packDir, string? fileName,
        RomFreeSpaceAllocator allocator, List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
    {
        var img = Gen3PlayerGraphicsWriter.LoadImage(packDir, fileName, "FrontImage", FrontTiles * 8, FrontTiles * 8, results);
        if (img is null)
        {
            return;
        }
        using (img)
        {
            var palette = Gen3PlayerGraphicsWriter.DerivePalette(packDir, fileName);
            var picOff = elf.RomOffset(frontPic);
            var palOff = elf.RomOffset(frontPalette);
            if (palette is null)
            {
                results.Add(new("FrontImage", false, "front image isn't 16-colour indexed and has >15 opaque colours"));
                return;
            }
            if (picOff is null || palOff is null)
            {
                results.Add(new("FrontImage", false, $"symbol {frontPic} / {frontPalette} not in this build"));
                return;
            }

            var refs = FindPointersTo(rom, picOff.Value);
            if (refs.Count == 0)
            {
                results.Add(new("FrontImage", false, "no references to the front pic found"));
                return;
            }

            var newImage = AllocateAndWrite(allocator, Lz10Codec.Compress(GbaTileCodec.Encode(img, palette)), edits);
            var newPalette = AllocateAndWrite(allocator, palette.ToBytes(), edits);
            if (newImage is null || newPalette is null)
            {
                results.Add(new("FrontImage", false, "no free ROM space"));
                return;
            }
            foreach (var r in refs)
            {
                edits.Add((r, Gba.PointerBytes(newImage.Value)));
            }
            var palRefs = RepointPalettes(rom, refs, palOff.Value, newPalette.Value, edits);
            results.Add(new("FrontImage", true, $"image relocated ({refs.Count} ref(s)), palette relocated ({palRefs} ref(s))"));
        }
    }

    private static void TryBackImage(
        byte[] rom, ElfSymbolTable elf, Layout layout, string packDir, string? fileName, RomFreeSpaceAllocator allocator,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
    {
        const int frameH = FrontTiles * 8;
        var strip = Gen3PlayerGraphicsWriter.LoadImage(packDir, fileName, "BackImage", frameH, -1, results);
        if (strip is null)
        {
            return;
        }

        Image<Rgba32> img;
        using (strip)
        {
            if (strip.Height == 0 || strip.Height % frameH != 0)
            {
                results.Add(new("BackImage", false,
                    $"back sprite must be a vertical strip of {frameH}px frames, got {strip.Width}x{strip.Height}"));
                return;
            }
            var have = strip.Height / frameH;
            img = have == layout.BackFrames
                ? strip.Clone()
                : Gen3PlayerGraphicsWriter.ResampleFrameStrip(strip, frameH, have, layout.BackFrames);
        }

        using (img)
        {
            var palette = Gen3PlayerGraphicsWriter.DerivePalette(packDir, fileName);
            if (palette is null)
            {
                results.Add(new("BackImage", false, "back image isn't 16-colour indexed and has >15 opaque colours"));
                return;
            }
            var picOff = elf.RomOffset(layout.BackPic);
            var palOff = elf.RomOffset(layout.BackPalette);
            if (picOff is null || palOff is null)
            {
                results.Add(new("BackImage", false, $"symbol {layout.BackPic} / {layout.BackPalette} not in this build"));
                return;
            }

            var encoded = GbaTileCodec.Encode(img, palette);
            if (!WriteInPlace(rom, elf, layout.BackPic, encoded, edits))
            {
                results.Add(new("BackImage", false, $"back pic symbol is smaller than a {layout.BackFrames}-frame strip"));
                return;
            }

            var newPalette = AllocateAndWrite(allocator, palette.ToBytes(), edits);
            var palRefs = newPalette is null ? 0
                : RepointPalettes(rom, FindPointersTo(rom, picOff.Value), palOff.Value, newPalette.Value, edits);
            results.Add(new("BackImage", palRefs > 0,
                palRefs > 0 ? $"image in place, palette relocated ({palRefs} ref(s))" : "image written, but its palette couldn't be repointed"));
        }
    }

    private static void TryMapIcon(
        byte[] rom, ElfSymbolTable elf, Layout layout, string packDir, string? fileName,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
    {
        var img = Gen3PlayerGraphicsWriter.LoadImage(packDir, fileName, "MapIcon", 16, 16, results);
        if (img is null)
        {
            return;
        }
        using (img)
        {
            var palette = Gen3PlayerGraphicsWriter.DerivePalette(packDir, fileName);
            if (palette is null)
            {
                results.Add(new("MapIcon", false, "map icon isn't 16-colour indexed and has >15 opaque colours"));
                return;
            }
            var okImg = WriteInPlace(rom, elf, layout.MapIconGfx, GbaTileCodec.Encode(img, palette), edits);
            var okPal = okImg && WriteInPlace(rom, elf, layout.MapIconPal, palette.ToBytes(), edits);
            results.Add(new("MapIcon", okImg && okPal, okImg && okPal ? "image + palette in place" : "map-icon symbols not in this build"));
        }
    }

    /// <summary>
    /// FRLG Oak-speech portrait: 64x96, 8bpp / 32 colours loaded at BG palette 4
    /// with a sequential tilemap - same as retail (see Gen3IntroImage). The tiles
    /// are "smol"-compressed, so LZ10 tiles go to free space and every pointer
    /// to them is repointed; the raw palette is overwritten in place.
    /// </summary>
    private static void TryIntroImage(
        byte[] rom, ElfSymbolTable elf, Layout layout, string packDir, string? fileName, RomFreeSpaceAllocator allocator,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
    {
        if (layout.IntroTiles is null || layout.IntroPalette is null)
        {
            return;
        }

        var img = Gen3PlayerGraphicsWriter.LoadImage(packDir, fileName, "OakSpeechImage",
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

            var tilesOff = elf.RomOffset(layout.IntroTiles);
            if (tilesOff is null)
            {
                results.Add(new("OakSpeechImage", false, $"symbol {layout.IntroTiles} not in this build"));
                return;
            }
            var refs = FindPointersTo(rom, tilesOff.Value);
            if (refs.Count == 0)
            {
                results.Add(new("OakSpeechImage", false, "no references to the intro portrait found"));
                return;
            }
            if (!WriteInPlace(rom, elf, layout.IntroPalette, palette, edits))
            {
                results.Add(new("OakSpeechImage", false, $"palette symbol {layout.IntroPalette} missing or too small"));
                return;
            }
            var newTiles = AllocateAndWrite(allocator, Lz10Codec.Compress(tiles), edits);
            if (newTiles is null)
            {
                results.Add(new("OakSpeechImage", false, "no free ROM space"));
                return;
            }
            foreach (var r in refs)
            {
                edits.Add((r, Gba.PointerBytes(newTiles.Value)));
            }
            results.Add(new("OakSpeechImage", true, $"tiles relocated ({refs.Count} ref(s)) + palette in place"));
        }
    }
}
