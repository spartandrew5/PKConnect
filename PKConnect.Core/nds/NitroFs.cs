namespace PKConnect.Core.nds;

/// <summary>
/// Minimal reader for the NDS ROM's NitroFS filesystem (the FNT/FAT pair every
/// retail .nds header points at) - just enough to resolve a community-doc
/// style path like "a/0/8/1" to that file's absolute byte range in the ROM,
/// so it can be sliced out (a NARC, typically) for further parsing.
/// </summary>
public static class NitroFs
{
    private const int RootDirId = 0xF000;

    /// <summary>Absolute (offset, length) of the file at <paramref name="path"/> (e.g. "a/0/8/1"), or null if not found.</summary>
    public static (int Offset, int Length)? ResolveFile(byte[] rom, string path)
    {
        var fntOffset = (int)ReadU32(rom, 0x40);
        var fatOffset = (int)ReadU32(rom, 0x48);

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0) return null;

        var dirId = RootDirId;
        for (var i = 0; i < segments.Length; i++)
        {
            var isLast = i == segments.Length - 1;
            var found = FindInDirectory(rom, fntOffset, dirId, segments[i], wantDirectory: !isLast);
            if (found is null) return null;

            if (isLast)
            {
                var fileId = found.Value;
                return ReadFatEntry(rom, fatOffset, fileId);
            }

            dirId = found.Value;
        }

        return null;
    }

    /// <summary>The file's NitroFS id plus its absolute (offset, length), or null if not found.</summary>
    public static (int FileId, int Offset, int Length)? ResolveFileEntry(byte[] rom, string path)
    {
        var fntOffset = (int)ReadU32(rom, 0x40);
        var fatOffset = (int)ReadU32(rom, 0x48);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0) return null;

        var dirId = RootDirId;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var sub = FindInDirectory(rom, fntOffset, dirId, segments[i], wantDirectory: true);
            if (sub is null) return null;
            dirId = sub.Value;
        }
        var fileId = FindInDirectory(rom, fntOffset, dirId, segments[^1], wantDirectory: false);
        if (fileId is null) return null;
        var (offset, length) = ReadFatEntry(rom, fatOffset, fileId.Value);
        return (fileId.Value, offset, length);
    }

    /// <summary>Absolute ROM offset of file <paramref name="fileId"/>'s FAT entry (u32 start, u32 end).</summary>
    public static int FatEntryOffset(byte[] rom, int fileId) => (int)ReadU32(rom, 0x48) + fileId * 8;

    /// <summary>
    /// Where the next thing in the ROM after <paramref name="offset"/> starts - the
    /// nearest FAT file start or header-listed region (ARM9/ARM7 binaries, FNT, FAT,
    /// overlay tables, banner) - i.e. how far a file at <paramref name="offset"/> may grow in place.
    /// </summary>
    public static int NextRegionStart(byte[] rom, int offset)
    {
        var fatOffset = (int)ReadU32(rom, 0x48);
        var fatLength = (int)ReadU32(rom, 0x4C);
        var next = (int)Math.Min(ReadU32(rom, 0x80), (uint)rom.Length); // NTR used size
        for (var p = fatOffset; p + 8 <= fatOffset + fatLength; p += 8)
        {
            var start = (int)ReadU32(rom, p);
            var end = (int)ReadU32(rom, p + 4);
            if (start > offset && start < next && end > start) next = start;
        }
        foreach (var headerField in new[] { 0x20, 0x30, 0x40, 0x48, 0x50, 0x58, 0x68 })
        {
            var start = (int)ReadU32(rom, headerField);
            if (start > offset && start < next) next = start;
        }
        return next;
    }

    /// <summary>
    /// Looks up <paramref name="name"/> inside directory <paramref name="dirId"/>.
    /// Returns the sub-directory id (0xF000|index) when <paramref name="wantDirectory"/>,
    /// otherwise the resolved file id.
    /// </summary>
    private static int? FindInDirectory(byte[] rom, int fntOffset, int dirId, string name, bool wantDirectory)
    {
        var dirIndex = dirId & 0xFFF;
        var mainEntryOffset = fntOffset + dirIndex * 8;
        var subTableOffset = fntOffset + (int)ReadU32(rom, mainEntryOffset);
        var firstFileId = ReadU16(rom, mainEntryOffset + 4);

        var pos = subTableOffset;
        var fileId = firstFileId;
        while (true)
        {
            var typeLength = rom[pos];
            pos++;
            if (typeLength == 0) break;

            var isDirectory = (typeLength & 0x80) != 0;
            var nameLength = typeLength & 0x7F;
            var entryName = System.Text.Encoding.ASCII.GetString(rom, pos, nameLength);
            pos += nameLength;

            if (isDirectory)
            {
                var subDirId = ReadU16(rom, pos);
                pos += 2;
                if (wantDirectory && entryName == name) return subDirId;
            }
            else
            {
                if (!wantDirectory && entryName == name) return fileId;
                fileId++;
            }
        }

        return null;
    }

    /// <summary>Names of every entry directly inside the directory at <paramref name="path"/> ("" for root), directories suffixed with "/".</summary>
    public static List<string>? ListDirectory(byte[] rom, string path)
    {
        var fntOffset = (int)ReadU32(rom, 0x40);
        var dirId = RootDirId;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var segment in segments)
        {
            var found = FindInDirectory(rom, fntOffset, dirId, segment, wantDirectory: true);
            if (found is null) return null;
            dirId = found.Value;
        }

        var dirIndex = dirId & 0xFFF;
        var mainEntryOffset = fntOffset + dirIndex * 8;
        var subTableOffset = fntOffset + (int)ReadU32(rom, mainEntryOffset);

        var names = new List<string>();
        var pos = subTableOffset;
        while (true)
        {
            var typeLength = rom[pos];
            pos++;
            if (typeLength == 0) break;

            var isDirectory = (typeLength & 0x80) != 0;
            var nameLength = typeLength & 0x7F;
            var entryName = System.Text.Encoding.ASCII.GetString(rom, pos, nameLength);
            pos += nameLength;

            if (isDirectory)
            {
                pos += 2; // sub-dir id, unused here
                names.Add(entryName + "/");
            }
            else
            {
                names.Add(entryName);
            }
        }
        return names;
    }

    private static (int Offset, int Length) ReadFatEntry(byte[] rom, int fatOffset, int fileId)
    {
        var entryOffset = fatOffset + fileId * 8;
        var start = (int)ReadU32(rom, entryOffset);
        var end = (int)ReadU32(rom, entryOffset + 4);
        return (start, end - start);
    }

    private static uint ReadU32(byte[] data, int offset) => BitConverter.ToUInt32(data, offset);
    private static ushort ReadU16(byte[] data, int offset) => BitConverter.ToUInt16(data, offset);
}
