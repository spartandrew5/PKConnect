namespace PKConnect.Core.nds;

/// <summary>
/// A Gen 5 text file (a member of the message archives, e.g. Black/White a/0/0/2): a 16-byte header
/// (sections u16, lines u16, section size u32, 0, section offset u32), then per section its size and
/// a table of (offset from the section start u32, length in 16-bit units u16, unknown u16) per line,
/// then the lines. A line is UTF-16 ending in 0xFFFF, each unit XORed with a key that turns left by
/// three bits from one unit to the next; the key a line starts with is its own, so it is taken from
/// the line as it stands (its last unit is the encrypted 0xFFFF).
///
/// Only single-section files are handled, which is what the name lists are.
/// </summary>
public static class Gen5TextFile
{
    private const ushort Terminator = 0xFFFF;
    /// <summary>Marks a line stored as 9-bit characters; such a line is read as empty here.</summary>
    private const ushort Compressed = 0xF100;

    public static int LineCount(ReadOnlySpan<byte> file) => U16(file, 2);

    /// <summary>The line's text, or null when it is stored compressed.</summary>
    public static string? ReadLine(ReadOnlySpan<byte> file, int line)
    {
        var (offset, length) = Line(file, line);
        if (length == 0) return "";
        var key = (ushort)(U16(file, offset + (length - 1) * 2) ^ Terminator);
        var chars = new char[length];
        for (var i = length - 1; i >= 0; i--)
        {
            chars[i] = (char)(U16(file, offset + i * 2) ^ key);
            key = (ushort)((key >> 3) | (key << 13));
        }
        if (chars[0] == Compressed) return null;
        return new string(chars, 0, length - 1);
    }

    /// <summary>
    /// The file with one line's text replaced (stored plain). The other lines keep their bytes;
    /// the file's size changes with the line's length.
    /// </summary>
    public static byte[] ReplaceLine(ReadOnlySpan<byte> file, int line, string text)
    {
        if (U16(file, 0) != 1) throw new InvalidOperationException("text file has more than one section");
        var lines = LineCount(file);
        if (line < 0 || line >= lines) throw new ArgumentOutOfRangeException(nameof(line));
        var section = (int)U32(file, 12);
        var table = section + 4;
        var dataStart = table + lines * 8;

        var (oldOffset, oldLength) = Line(file, line);
        if (oldLength == 0) throw new InvalidOperationException("the line is empty: it has no key to encrypt with");
        // Back from the key of the last unit to the key of the first.
        var key = (ushort)(U16(file, oldOffset + (oldLength - 1) * 2) ^ Terminator);
        for (var i = 1; i < oldLength; i++) key = (ushort)((key >> 3) | (key << 13));

        var units = new ushort[text.Length + 1];
        for (var i = 0; i < text.Length; i++) units[i] = text[i];
        units[^1] = Terminator;
        var encrypted = new byte[units.Length * 2];
        for (var i = 0; i < units.Length; i++)
        {
            var value = (ushort)(units[i] ^ key);
            encrypted[i * 2] = (byte)value;
            encrypted[i * 2 + 1] = (byte)(value >> 8);
            key = (ushort)((key << 3) | (key >> 13));
        }

        // Lines laid out again in order, back to back as the original has them.
        using var body = new MemoryStream();
        var header = file[..dataStart].ToArray();
        for (var i = 0; i < lines; i++)
        {
            var (offset, length) = Line(file, i);
            var bytes = i == line ? encrypted : file.Slice(offset, length * 2).ToArray();
            WriteU32(header, table + i * 8, (uint)(dataStart - section + body.Length));
            WriteU16(header, table + i * 8 + 4, (ushort)(bytes.Length / 2));
            body.Write(bytes);
        }
        var sectionSize = (uint)(dataStart - section + body.Length);
        WriteU32(header, 4, sectionSize);
        WriteU32(header, section, sectionSize);
        return [.. header, .. body.ToArray()];
    }

    private static (int Offset, int Length) Line(ReadOnlySpan<byte> file, int line)
    {
        var section = (int)U32(file, 12);
        var entry = section + 4 + line * 8;
        return (section + (int)U32(file, entry), U16(file, entry + 4));
    }

    private static ushort U16(ReadOnlySpan<byte> d, int o) => (ushort)(d[o] | d[o + 1] << 8);
    private static uint U32(ReadOnlySpan<byte> d, int o) => (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24);
    private static void WriteU16(byte[] d, int o, ushort v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); }
    private static void WriteU32(byte[] d, int o, uint v) { WriteU16(d, o, (ushort)v); WriteU16(d, o + 2, (ushort)(v >> 16)); }
}
