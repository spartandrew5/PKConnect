namespace PKConnect.Core.nds;

/// <summary>
/// Writes a replacement for a NitroFS file whose size may have changed (e.g. a
/// NARC rebuilt around re-compressed Gen 5 sprites), as plain (offset, bytes)
/// edits for the sparse BPS encoder:
///   - in place when it fits before the next file/region (FAT end updated if the size changed);
///   - otherwise relocated into free space, with its FAT entry repointed.
/// Free space is the DSi sector hash table of a DSi-enhanced ROM (header 0x1F0/0x1F4):
/// it sits inside the NTR region, but only DSi mode ever reads it - to verify the
/// ROM, which any NitroFS edit already breaks - so patched ROMs must run in DS mode.
/// One placer per patch, so relocations never overlap.
/// </summary>
public sealed class NdsFilePlacer(byte[] rom)
{
    private const int Alignment = 0x200;

    private int freeStart = -1;
    private int freeEnd = -1;

    /// <summary>Edits that make <paramref name="path"/> contain <paramref name="data"/>, plus whether it was relocated.</summary>
    public (List<(int Offset, byte[] Data)> Edits, bool Relocated) Place(string path, byte[] data)
    {
        var entry = NitroFs.ResolveFileEntry(rom, path)
            ?? throw new InvalidOperationException($"{path} not found in NitroFS");
        var edits = new List<(int Offset, byte[] Data)>();

        var capacity = NitroFs.NextRegionStart(rom, entry.Offset) - entry.Offset;
        int start;
        var relocated = false;
        if (data.Length <= capacity)
        {
            start = entry.Offset;
            // Pad back over the old file's tail so a shrunken file leaves no stale bytes.
            var padded = data.Length >= entry.Length ? data : PadTo(data, entry.Length);
            edits.Add((start, padded));
        }
        else
        {
            start = Allocate(data.Length, path);
            edits.Add((start, data));
            relocated = true;
        }

        if (relocated || data.Length != entry.Length)
        {
            var fat = new byte[8];
            BitConverter.TryWriteBytes(fat.AsSpan(0), (uint)start);
            BitConverter.TryWriteBytes(fat.AsSpan(4), (uint)(start + data.Length));
            edits.Add((NitroFs.FatEntryOffset(rom, entry.FileId), fat));
        }
        return (edits, relocated);
    }

    /// <summary>
    /// Keeps this placer clear of what another placer already relocated: edits made by it that
    /// lie in the free space are skipped over.
    /// </summary>
    public void Reserve(IEnumerable<(int Offset, byte[] Data)> edits)
    {
        if (!FindFreeSpace()) return;
        foreach (var (offset, data) in edits)
        {
            var end = offset + data.Length;
            if (end > freeStart && offset < freeEnd) freeStart = AlignUp(end);
        }
    }

    private bool FindFreeSpace()
    {
        if (freeStart >= 0) return true;
        var hashTable = (int)BitConverter.ToUInt32(rom, 0x1F0);
        var hashLength = (int)BitConverter.ToUInt32(rom, 0x1F4);
        if (rom[0x12] < 2 || hashTable <= 0 || hashLength <= 0 || hashTable + hashLength > rom.Length) return false;
        freeStart = AlignUp(hashTable);
        freeEnd = hashTable + hashLength;
        return true;
    }

    private int Allocate(int length, string path)
    {
        if (!FindFreeSpace())
        {
            throw new InvalidOperationException(
                $"{path} grew past its slot and this ROM has no DSi hash table to relocate it into.");
        }

        var start = freeStart;
        if (start + length > freeEnd)
        {
            throw new InvalidOperationException($"Out of relocation space for {path} ({length} bytes).");
        }
        freeStart = AlignUp(start + length);
        return start;
    }

    private static int AlignUp(int value) => (value + Alignment - 1) / Alignment * Alignment;

    private static byte[] PadTo(byte[] data, int length)
    {
        var padded = new byte[length];
        Array.Fill(padded, (byte)0xFF);
        data.CopyTo(padded, 0);
        return padded;
    }
}
