namespace PKConnect.Core.nds;

/// <summary>
/// Minimal reader for the NARC archive format (NitroFS files are frequently
/// themselves a NARC bundling many related sub-files - sprite sheets, in our
/// case). Only what's needed to turn a member index into a byte range inside
/// the archive: the FATB ("BTAF") table. FNTB ("BTNF", names) and GMIF
/// ("FIMG" reversed, the actual data) are not parsed by name - callers
/// address members purely by index, matching how the community sprite-
/// location docs identify them.
/// </summary>
public static class NarcArchive
{
    /// <summary>(offset, length) of member <paramref name="index"/>, relative to the start of <paramref name="narc"/>.</summary>
    public static (int Offset, int Length) GetMemberRange(byte[] narc, int index)
    {
        // Outer container header: magic "NARC", bom+version (u16+u16), file_size (u32),
        // header_size (u16), num_sections (u16) - sections (FATB, FNTB, GMIF) follow immediately.
        const int fatbOffset = 0x10;
        if (Tag(narc, fatbOffset) != "BTAF")
        {
            throw new InvalidDataException($"Expected FATB ('BTAF') chunk at 0x{fatbOffset:X}, found '{Tag(narc, fatbOffset)}'.");
        }

        var fatbSectionSize = (int)ReadU32(narc, fatbOffset + 4);
        var numFiles = ReadU16(narc, fatbOffset + 8);
        if (index < 0 || index >= numFiles)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"NARC has {numFiles} members, index {index} is out of range.");
        }

        var entryOffset = fatbOffset + 0x0C + index * 8;
        var startRelative = (int)ReadU32(narc, entryOffset);
        var endRelative = (int)ReadU32(narc, entryOffset + 4);

        var fntbOffset = fatbOffset + fatbSectionSize;
        var fntbSectionSize = (int)ReadU32(narc, fntbOffset + 4);
        var gmifOffset = fntbOffset + fntbSectionSize;
        if (Tag(narc, gmifOffset) != "GMIF")
        {
            throw new InvalidDataException($"Expected FIMG ('GMIF') chunk at 0x{gmifOffset:X}, found '{Tag(narc, gmifOffset)}'.");
        }

        // FATB offsets are relative to the start of FIMG's own data, i.e. right after its 8-byte header.
        var fimgDataOffset = gmifOffset + 8;
        return (fimgDataOffset + startRelative, endRelative - startRelative);
    }

    /// <summary>Number of members in the archive.</summary>
    public static int MemberCount(byte[] narc) => ReadU16(narc, 0x10 + 8);

    /// <summary>
    /// Rebuilds the archive with some members' contents replaced (their sizes may
    /// change). Members are laid out back to back, 4-byte aligned (0xFF pad, as
    /// retail files are); FNTB is copied unchanged, the header and GMIF sizes
    /// and every FATB entry are recomputed.
    /// </summary>
    public static byte[] Rebuild(byte[] narc, IReadOnlyDictionary<int, byte[]> replacements)
    {
        const int fatbOffset = 0x10;
        var fatbSize = (int)ReadU32(narc, fatbOffset + 4);
        var count = MemberCount(narc);
        var fntbOffset = fatbOffset + fatbSize;
        var fntbSize = (int)ReadU32(narc, fntbOffset + 4);

        var members = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            if (replacements.TryGetValue(i, out var replaced))
            {
                members[i] = replaced;
                continue;
            }
            var (offset, length) = GetMemberRange(narc, i);
            members[i] = narc.AsSpan(offset, length).ToArray();
        }

        using var data = new MemoryStream();
        var fat = new byte[count * 8];
        for (var i = 0; i < count; i++)
        {
            while (data.Length % 4 != 0) data.WriteByte(0xFF);
            BitConverter.TryWriteBytes(fat.AsSpan(i * 8), (uint)data.Length);
            data.Write(members[i]);
            BitConverter.TryWriteBytes(fat.AsSpan(i * 8 + 4), (uint)data.Length);
        }
        while (data.Length % 4 != 0) data.WriteByte(0xFF);

        var gmifSize = 8 + (int)data.Length;
        var total = fntbOffset + fntbSize + gmifSize;
        var output = new byte[total];
        // Outer header and FATB header (count etc.) as before, then the new table.
        Array.Copy(narc, 0, output, 0, fatbOffset + 12);
        Array.Copy(fat, 0, output, fatbOffset + 12, fat.Length);
        Array.Copy(narc, fntbOffset, output, fntbOffset, fntbSize);
        var gmifOffset = fntbOffset + fntbSize;
        System.Text.Encoding.ASCII.GetBytes("GMIF").CopyTo(output, gmifOffset);
        BitConverter.TryWriteBytes(output.AsSpan(gmifOffset + 4), (uint)gmifSize);
        data.ToArray().CopyTo(output, gmifOffset + 8);
        BitConverter.TryWriteBytes(output.AsSpan(0x08), (uint)total);
        return output;
    }

    private static string Tag(byte[] data, int offset) => System.Text.Encoding.ASCII.GetString(data, offset, 4);
    private static uint ReadU32(byte[] data, int offset) => BitConverter.ToUInt32(data, offset);
    private static ushort ReadU16(byte[] data, int offset) => BitConverter.ToUInt16(data, offset);
}
