using PKConnect.Core.graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PKConnect.Core.nds;

/// <summary>
/// Writes a Gen 5 protagonist sprite set (see <see cref="Gen5PlayerGraphicsExtractor"/>
/// for the format) into a Black/White or Black 2/White 2 ROM. Both the hero's and
/// the heroine's copies are rewritten, so the set shows whichever gender the save plays.
///   Front: the still (and its sheet - for BW the still pasted at (0,0), for B2W2 the
///          set's front_sheet.png, which the idle animation is cut from) + palette.
///   Back:  the parts sheet + palette of the main and ball-in-hand groups, in every
///          copy of the back NARC.
///   Overworld: same-size BTX0 overwrites.
/// Battle sprites are LZ11, so a re-encoded member rarely keeps its size: each
/// touched NARC is rebuilt and placed by <see cref="NdsFilePlacer"/>.
/// </summary>
public static class Gen5PlayerGraphicsWriter
{
    public static (List<(int Offset, byte[] Data)> Edits, List<PlayerGraphicsSlotResult> Results) BuildEdits(
        byte[] rom, GraphicsPackStanza? stanza, string? packDir, Gen5PlayerGraphicsOffsets.Revision rev,
        (GraphicsPackStanza Stanza, string PackDir, Gen5BattleSlot Front, int? OverworldMember)? champion = null)
    {
        var edits = new List<(int Offset, byte[] Data)>();
        var results = new List<PlayerGraphicsSlotResult>();

        // NARC path -> member index -> new bytes, gathered across front/back before rebuilding.
        var narcReplacements = new Dictionary<string, Dictionary<int, byte[]>>();
        var narcs = new Dictionary<string, byte[]>();
        byte[] Narc(string path) => narcs.TryGetValue(path, out var n) ? n : narcs[path] = ReadFile(rom, path);
        void Replace(string path, int member, byte[] data) =>
            (narcReplacements.TryGetValue(path, out var m) ? m : narcReplacements[path] = [])[member] = data;

        if (stanza is not null && packDir is not null)
        {
            WriteFront(stanza, packDir, rev, Narc, Replace, results);
            WriteBack(stanza, packDir, rev, Narc, Replace, results);
        }
        // The League champion's battle sprite lives in the same archive as the player's front, so
        // it has to go into the same rebuild: the champion's own front over the champion's class.
        if (champion is { } c)
        {
            var championResults = new List<PlayerGraphicsSlotResult>();
            WriteFront(c.Stanza, c.PackDir, rev with { Front = c.Front }, Narc, Replace, championResults);
            results.AddRange(championResults.Select(r => r with { Slot = $"champion-{r.Slot}" }));
        }

        var placer = new NdsFilePlacer(rom);
        foreach (var (path, replacements) in narcReplacements)
        {
            try
            {
                var rebuilt = NarcArchive.Rebuild(Narc(path), replacements);
                var (placed, relocated) = placer.Place(path, rebuilt);
                edits.AddRange(placed);
                if (relocated) results.Add(new($"narc:{path}", true, $"relocated ({rebuilt.Length} bytes) into the DSi hash-table area"));
            }
            catch (Exception ex)
            {
                results.Add(new($"narc:{path}", false, ex.Message));
            }
        }

        if (stanza is not null && packDir is not null)
        {
            WriteOverworld(rom, stanza, packDir, rev, edits, results);
            if (rev.TrainerCard is { } card) WriteTrainerCard(rom, stanza, packDir, card, edits, results);
        }
        if (champion is { OverworldMember: { } member } who)
        {
            WriteChampionOverworld(rom, who.Stanza, who.PackDir, rev, member, edits, results);
        }
        return (edits, results);
    }

    /// <summary>
    /// The trainer-card figure, from the set's TrainerCardImage or else its front sprite
    /// (see <see cref="Gen5TrainerCardCodec"/>). Everything in the card NARC is
    /// uncompressed, so these are same-size overwrites of the sheet, both tilemaps and
    /// the recoloured palettes.
    /// </summary>
    private static void WriteTrainerCard(
        byte[] rom, GraphicsPackStanza stanza, string packDir, Gen5TrainerCardSlot slot,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
    {
        const string name = "trainer_card";
        var key = string.IsNullOrWhiteSpace(stanza.Get(slot.PackKey)) ? "FrontImage" : slot.PackKey;
        using var image = Load(stanza, packDir, key, name, results, required: false);
        if (image is null) return;

        try
        {
            var range = NitroFs.ResolveFile(rom, slot.NarcPath) ?? throw new InvalidOperationException($"{slot.NarcPath} not found");
            var narc = rom.AsSpan(range.Offset, range.Length).ToArray();
            var sheet = Member(narc, slot.SheetMember);
            var heroMap = Member(narc, slot.HeroMapMember);
            var heroineMap = Member(narc, slot.HeroineMapMember);
            var palettes = slot.PaletteMembers.Select(m => Member(narc, m)).ToList();

            var result = Gen5TrainerCardCodec.Inject(sheet, palettes, heroMap, heroineMap, image);

            void Edit(int member, byte[] data) => edits.Add((range.Offset + NarcArchive.GetMemberRange(narc, member).Offset, data));
            Edit(slot.SheetMember, sheet);
            Edit(slot.HeroMapMember, heroMap);
            Edit(slot.HeroineMapMember, heroineMap);
            foreach (var p in result.WrittenPalettes) Edit(slot.PaletteMembers[p], palettes[p]);

            results.Add(new(name, true, result.ColoursWanted > result.ColoursUsed
                ? $"{result.ColoursWanted} colours reduced to the card's {result.ColoursUsed}"
                : null));
        }
        catch (Exception ex)
        {
            results.Add(new(name, false, ex.Message));
        }
    }

    private static void WriteFront(
        GraphicsPackStanza stanza, string packDir, Gen5PlayerGraphicsOffsets.Revision rev,
        Func<string, byte[]> narc, Action<string, int, byte[]> replace, List<PlayerGraphicsSlotResult> results)
    {
        using var front = Load(stanza, packDir, "FrontImage", "front", results, required: false);
        if (front is null) return;

        // BW fronts are static and share one layout, so only the art changes. B2W2 fronts are
        // animated: the set's layout (cells + idle animation) has to come with its sheet. A BW set's
        // static layout + its still on an empty sheet also works there.
        Dictionary<int, byte[]>? layout = null;
        Image<Rgba32>? sheet;
        if (rev.AnimatedFront)
        {
            layout = LoadLayout(stanza, packDir, "FrontLayout", "front", results);
            if (layout is null)
            {
                results.Add(new("front", false, $"{rev.RomType} fronts are animated - the set needs a front.layout"));
                return;
            }
            sheet = Load(stanza, packDir, "FrontSheetImage", "front_sheet", results, required: false)
                ?? Gen5TrainerSpriteCodec.StillOnSheet(front);
        }
        else
        {
            sheet = Gen5TrainerSpriteCodec.StillOnSheet(front);
        }
        using var _ = sheet;

        try
        {
            var palette = new Gen5TrainerSpriteCodec.GroupPalette([front, sheet]);
            foreach (var path in rev.Front.NarcPaths)
            {
                foreach (var group in new[] { rev.Front.HeroGroup, rev.Front.HeroineGroup })
                {
                    var first = group * Gen5TrainerSpriteCodec.GroupSize;
                    var n = narc(path);
                    replace(path, first + Gen5TrainerSpriteCodec.StillMember,
                        Gen5TrainerSpriteCodec.EncodeStill(Member(n, first + Gen5TrainerSpriteCodec.StillMember), front, palette));
                    replace(path, first + Gen5TrainerSpriteCodec.SheetMember,
                        Gen5TrainerSpriteCodec.EncodeSheet(Member(n, first + Gen5TrainerSpriteCodec.SheetMember), sheet, palette));
                    replace(path, first + Gen5TrainerSpriteCodec.PaletteMember,
                        Gen5TrainerSpriteCodec.EncodePalette(Member(n, first + Gen5TrainerSpriteCodec.PaletteMember), palette));
                    foreach (var (member, data) in layout ?? []) replace(path, first + member, data);
                }
            }
            results.Add(new("front", true, null));
        }
        catch (Exception ex)
        {
            results.Add(new("front", false, ex.Message));
        }
    }

    private static void WriteBack(
        GraphicsPackStanza stanza, string packDir, Gen5PlayerGraphicsOffsets.Revision rev,
        Func<string, byte[]> narc, Action<string, int, byte[]> replace, List<PlayerGraphicsSlotResult> results)
    {
        using var back = Load(stanza, packDir, "BackImage", "back", results, required: false);
        if (back is null) return;
        // The sheet is only correct with the cells/animation it was drawn for (writing Hilda's
        // sheet over Hilbert's cells cuts her arm in the wrong place), so its layout comes along.
        var backLayout = LoadLayout(stanza, packDir, "BackLayout", "back", results);
        // No ball-in-hand sheet in the set: the main sheet + layout stand in for it.
        using var alt = Load(stanza, packDir, "BackAltImage", "back_alt", results, required: false);
        var altLayout = alt is null ? backLayout : LoadLayout(stanza, packDir, "BackAltLayout", "back_alt", results);

        void WriteGroups(Gen5BattleSlot slot, Image<Rgba32> image, Dictionary<int, byte[]>? layout, string name)
        {
            if (layout is null)
            {
                results.Add(new(name, false, "the set has no .layout for this sheet, and the game's own cells would cut it up wrong"));
                return;
            }
            try
            {
                var palette = new Gen5TrainerSpriteCodec.GroupPalette([image]);
                foreach (var path in slot.NarcPaths)
                {
                    foreach (var group in new[] { slot.HeroGroup, slot.HeroineGroup })
                    {
                        var first = group * Gen5TrainerSpriteCodec.GroupSize;
                        var n = narc(path);
                        replace(path, first + Gen5TrainerSpriteCodec.SheetMember,
                            Gen5TrainerSpriteCodec.EncodeSheet(Member(n, first + Gen5TrainerSpriteCodec.SheetMember), image, palette));
                        replace(path, first + Gen5TrainerSpriteCodec.PaletteMember,
                            Gen5TrainerSpriteCodec.EncodePalette(Member(n, first + Gen5TrainerSpriteCodec.PaletteMember), palette));
                        foreach (var (member, data) in layout) replace(path, first + member, data);
                    }
                }
                results.Add(new(name, true, null));
            }
            catch (Exception ex)
            {
                results.Add(new(name, false, ex.Message));
            }
        }

        WriteGroups(rev.Back, back, backLayout, "back");
        if (rev.AltBack is { } altSlot) WriteGroups(altSlot, alt ?? back, altLayout, "back_alt");
    }

    private static void WriteOverworld(
        byte[] rom, GraphicsPackStanza stanza, string packDir, Gen5PlayerGraphicsOffsets.Revision rev,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
    {
        var range = NitroFs.ResolveFile(rom, rev.OverworldNarcPath);
        if (range is null)
        {
            results.Add(new("overworld", false, $"{rev.OverworldNarcPath} not found"));
            return;
        }
        var narc = rom.AsSpan(range.Value.Offset, range.Value.Length).ToArray();

        foreach (var slot in rev.OverworldSlots)
        {
            using var image = Load(stanza, packDir, slot.PackKey, slot.Name, results, required: false);
            if (image is null) continue;
            try
            {
                foreach (var member in new[] { slot.HeroMember, slot.HeroineMember })
                {
                    var (offset, length) = NarcArchive.GetMemberRange(narc, member);
                    var btx = narc.AsSpan(offset, length).ToArray();
                    using var fitted = FitFrames(image, Btx0SpriteCodec.Decode(btx));
                    Btx0SpriteCodec.Encode(btx, fitted ?? image);
                    edits.Add((range.Value.Offset + offset, btx));
                }
                results.Add(new(slot.Name, true, null));
            }
            catch (Exception ex)
            {
                results.Add(new(slot.Name, false, ex.Message));
            }
        }
    }

    /// <summary>
    /// The champion's walk sheet over the League champion's own overworld sheet. An NPC's sheet
    /// starts with the same twelve walking frames as the player's; whatever else the NPC has (its
    /// own poses) becomes the champion standing, facing the camera.
    /// </summary>
    private static void WriteChampionOverworld(
        byte[] rom, GraphicsPackStanza stanza, string packDir, Gen5PlayerGraphicsOffsets.Revision rev, int member,
        List<(int Offset, byte[] Data)> edits, List<PlayerGraphicsSlotResult> results)
    {
        const string name = "champion-walk";
        using var walk = Load(stanza, packDir, "WalkSprite", name, results, required: false);
        if (walk is null) return;
        try
        {
            var range = NitroFs.ResolveFile(rom, rev.OverworldNarcPath)
                ?? throw new InvalidOperationException($"{rev.OverworldNarcPath} not found");
            var narc = rom.AsSpan(range.Offset, range.Length).ToArray();
            var (offset, length) = NarcArchive.GetMemberRange(narc, member);
            var btx = narc.AsSpan(offset, length).ToArray();

            using var vanilla = Btx0SpriteCodec.Decode(btx);
            var frame = vanilla.Width;
            if (walk.Width != frame || walk.Height < Gen5PlayerGraphicsOffsets.NpcWalkFrames * frame)
                throw new InvalidOperationException($"walk sheet is {walk.Width}x{walk.Height}; expected {frame} wide with at least {Gen5PlayerGraphicsOffsets.NpcWalkFrames} frames");

            using var sheet = new Image<Rgba32>(vanilla.Width, vanilla.Height);
            for (var i = 0; i < vanilla.Height / frame; i++)
            {
                var from = i < Gen5PlayerGraphicsOffsets.NpcWalkFrames ? i : Gen5PlayerGraphicsOffsets.WalkFrameFacingDown;
                for (var y = 0; y < frame; y++)
                    for (var x = 0; x < frame; x++)
                        sheet[x, i * frame + y] = walk[x, from * frame + y];
            }
            Btx0SpriteCodec.Encode(btx, sheet);
            edits.Add((range.Offset + offset, btx));
            results.Add(new(name, true, null));
        }
        catch (Exception ex)
        {
            results.Add(new(name, false, ex.Message));
        }
    }

    /// <summary>
    /// A strip with a different frame count than the game's: BW's walk sheet is 32 frames
    /// whose last 8 are blank, B2W2's is 24. Pads with backdrop frames, or trims frames
    /// that are pure backdrop; anything else is left for the encoder's size check to reject.
    /// Null when no change is needed.
    /// </summary>
    private static Image<Rgba32>? FitFrames(Image<Rgba32> image, Image<Rgba32> vanilla)
    {
        using (vanilla)
        {
            if (image.Width != vanilla.Width || image.Height == vanilla.Height) return null;
            var background = image[0, 0];
            for (var y = vanilla.Height; y < image.Height; y++)
                for (var x = 0; x < image.Width; x++)
                    if (image[x, y] != background) return null;

            var fitted = new Image<Rgba32>(vanilla.Width, vanilla.Height, background);
            for (var y = 0; y < Math.Min(image.Height, vanilla.Height); y++)
                for (var x = 0; x < image.Width; x++)
                    fitted[x, y] = image[x, y];
            return fitted;
        }
    }

    private static Dictionary<int, byte[]>? LoadLayout(
        GraphicsPackStanza stanza, string packDir, string packKey, string slot, List<PlayerGraphicsSlotResult> results)
    {
        var fileName = stanza.Get(packKey);
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        try
        {
            return Gen5TrainerSpriteCodec.UnpackLayout(File.ReadAllBytes(Path.Combine(packDir, fileName)));
        }
        catch (Exception ex)
        {
            results.Add(new(slot, false, $"couldn't read {fileName}: {ex.Message}"));
            return null;
        }
    }

    /// <summary>The stanza's image for <paramref name="packKey"/>, or null (with a result note when it's set but unreadable).</summary>
    private static Image<Rgba32>? Load(
        GraphicsPackStanza stanza, string packDir, string packKey, string slot, List<PlayerGraphicsSlotResult> results, bool required)
    {
        var fileName = stanza.Get(packKey);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            if (required) results.Add(new(slot, false, "not in this sprite set"));
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
            return Image.Load<Rgba32>(path);
        }
        catch (Exception ex)
        {
            results.Add(new(slot, false, $"couldn't read {fileName}: {ex.Message}"));
            return null;
        }
    }

    private static byte[] ReadFile(byte[] rom, string path)
    {
        var range = NitroFs.ResolveFile(rom, path) ?? throw new InvalidOperationException($"{path} not found in NitroFS");
        return rom.AsSpan(range.Offset, range.Length).ToArray();
    }

    private static byte[] Member(byte[] narc, int member)
    {
        var (offset, length) = NarcArchive.GetMemberRange(narc, member);
        return narc.AsSpan(offset, length).ToArray();
    }
}
