namespace PKConnect.Core.patching;

/// <summary>
/// IPS patch reading, in-memory application and writing. The launch flow
/// composes the single patch mGBA auto-loads next to the ROM from several edit
/// sources: the static decomp-built base patch, landing-zone team edits, and
/// player graphics.
/// </summary>
public static class IpsPatchService
{
    public const string IpsMagic = "PATCH";
    public const string IpsEof = "EOF";

    private const int MaxRecordLength = 0xFFFF;
    private const int MaxOffset = 0xFFFFFF;

    // A record whose 3 offset bytes are 'E','O','F' reads as the terminator.
    private const int EofMarkerOffset = 0x454F46;

    /// <summary>Parses an IPS patch into (offset, bytes) edits. RLE records are expanded.</summary>
    public static List<(int Offset, byte[] Data)> Parse(byte[] ips)
    {
        if (ips.Length < IpsMagic.Length + IpsEof.Length
            || System.Text.Encoding.ASCII.GetString(ips, 0, IpsMagic.Length) != IpsMagic)
        {
            throw new InvalidDataException("Not an IPS patch (missing PATCH header)");
        }

        var edits = new List<(int Offset, byte[] Data)>();
        var p = IpsMagic.Length;

        while (true)
        {
            if (p + 3 > ips.Length)
            {
                throw new InvalidDataException("IPS patch is truncated before its EOF marker");
            }

            if (ips[p] == 'E' && ips[p + 1] == 'O' && ips[p + 2] == 'F')
            {
                return edits;
            }

            if (p + 5 > ips.Length)
            {
                throw new InvalidDataException($"IPS record header at 0x{p:X} is truncated");
            }

            var offset = (ips[p] << 16) | (ips[p + 1] << 8) | ips[p + 2];
            var length = (ips[p + 3] << 8) | ips[p + 4];
            p += 5;

            if (length == 0)
            {
                if (p + 3 > ips.Length)
                {
                    throw new InvalidDataException($"IPS RLE record at 0x{p:X} is truncated");
                }

                var runLength = (ips[p] << 8) | ips[p + 1];
                var run = new byte[runLength];
                Array.Fill(run, ips[p + 2]);
                edits.Add((offset, run));
                p += 3;
                continue;
            }

            if (p + length > ips.Length)
            {
                throw new InvalidDataException($"IPS record at 0x{offset:X} runs past the end of the patch");
            }

            edits.Add((offset, ips.AsSpan(p, length).ToArray()));
            p += length;
        }
    }

    /// <summary>Writes <paramref name="edits"/> into <paramref name="rom"/> in order.</summary>
    public static void ApplyInPlace(byte[] rom, IEnumerable<(int Offset, byte[] Data)> edits)
    {
        foreach (var (offset, data) in edits)
        {
            if (offset < 0 || offset + data.Length > rom.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(edits),
                    $"Edit at 0x{offset:X} (len {data.Length}) is outside the {rom.Length}-byte ROM");
            }

            data.CopyTo(rom, offset);
        }
    }

    /// <summary>
    /// Builds one IPS patch covering every byte range <paramref name="edits"/>
    /// touch, emitted from <paramref name="finalRom"/> (the ROM with all of
    /// those edits already applied in order). Overlapping edits therefore
    /// resolve exactly as they were applied, records are split to IPS's
    /// 65,535-byte limit, and no record starts at the offset that would read as
    /// the EOF marker.
    /// </summary>
    public static byte[] BuildIps(IEnumerable<(int Offset, byte[] Data)> edits, byte[] finalRom)
    {
        var merged = new List<(int Start, int End)>();
        foreach (var (start, end) in edits
            .Where(e => e.Data.Length > 0)
            .Select(e => (Start: e.Offset, End: e.Offset + e.Data.Length))
            .OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            }
            else
            {
                merged.Add((start, end));
            }
        }

        using var ms = new MemoryStream();
        WriteAscii(ms, IpsMagic);

        foreach (var (start, end) in merged)
        {
            if (start < 0 || end > finalRom.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(edits),
                    $"Edit range 0x{start:X}-0x{end:X} is outside the {finalRom.Length}-byte ROM");
            }

            if (end - 1 > MaxOffset)
            {
                throw new InvalidOperationException(
                    $"Edit range ending at 0x{end:X} is beyond IPS's 16 MB addressable range");
            }

            var cursor = start;
            while (cursor < end)
            {
                // Re-emit the preceding byte (already final) rather than start a
                // record whose offset spells "EOF".
                var recordStart = cursor == EofMarkerOffset ? cursor - 1 : cursor;
                var length = Math.Min(MaxRecordLength, end - recordStart);
                WriteRecord(ms, recordStart, finalRom.AsSpan(recordStart, length));
                cursor = recordStart + length;
            }
        }

        WriteAscii(ms, IpsEof);
        return ms.ToArray();
    }

    private static void WriteRecord(Stream stream, int offset, ReadOnlySpan<byte> data)
    {
        stream.WriteByte((byte)((offset >> 16) & 0xFF));
        stream.WriteByte((byte)((offset >> 8) & 0xFF));
        stream.WriteByte((byte)(offset & 0xFF));
        stream.WriteByte((byte)((data.Length >> 8) & 0xFF));
        stream.WriteByte((byte)(data.Length & 0xFF));
        stream.Write(data);
    }

    private static void WriteAscii(Stream stream, string value)
    {
        foreach (var c in value)
        {
            stream.WriteByte((byte)c);
        }
    }
}
