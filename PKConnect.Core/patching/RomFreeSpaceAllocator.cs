namespace PKConnect.Core.patching;

/// <summary>
/// Finds unused (0xFF-padded) space inside a GBA ROM byte array to inject new
/// data into, without moving or overwriting any existing ROM bytes/addresses.
/// GBA ROMs are conventionally padded with 0xFF up to their allocated cartridge
/// size, leaving large contiguous free regions (commonly hundreds of KB to a
/// few MB) that romhacking tools routinely use for this exact purpose.
///
/// Scans the whole ROM once for maximal 0xFF runs, then hands out 4-byte
/// aligned chunks starting from the largest runs first. Never shifts/reuses
/// bytes outside what it explicitly allocates.
/// </summary>
public class RomFreeSpaceAllocator
{
    private const byte FillerByte = 0xFF;
    private const int Alignment = 4;
    private const int MinRunLength = 64;

    private readonly List<(int Start, int Length)> freeRuns;
    private int runIndex;
    private int cursor;

    /// <param name="minOffset">
    /// Only hand out space at or after this ROM file offset. Use it to restrict
    /// allocations to a game's designated trailing free-space region and avoid
    /// 0xFF-looking runs that are actually real data.
    /// </param>
    /// <param name="reservedRanges">
    /// [Start, End) ranges never to hand out even though they read as 0xFF —
    /// bytes an earlier stage of the same launch patch already writes.
    /// </param>
    public RomFreeSpaceAllocator(byte[] rom, int minOffset = 0, IReadOnlyList<(int Start, int End)>? reservedRanges = null)
    {
        freeRuns = FindFreeRuns(rom)
            .SelectMany(r => SubtractReserved(Math.Max(r.Start, minOffset), r.Start + r.Length, reservedRanges))
            .Select(r => (Start: r.Start, Length: r.End - r.Start))
            .Where(r => r.Length >= MinRunLength)
            .OrderByDescending(r => r.Length)
            .ToList();
        runIndex = 0;
        cursor = freeRuns.Count > 0 ? freeRuns[0].Start : 0;
    }

    private static IEnumerable<(int Start, int End)> SubtractReserved(
        int start, int end, IReadOnlyList<(int Start, int End)>? reservedRanges)
    {
        var cursor = start;
        foreach (var (reservedStart, reservedEnd) in (reservedRanges ?? Array.Empty<(int Start, int End)>())
            .Where(r => r.End > start && r.Start < end)
            .OrderBy(r => r.Start))
        {
            if (reservedStart > cursor)
            {
                yield return (cursor, reservedStart);
            }
            cursor = Math.Max(cursor, reservedEnd);
        }
        if (cursor < end)
        {
            yield return (cursor, end);
        }
    }

    /// <summary>
    /// Reserves `length` bytes of free space and returns its ROM file offset
    /// (0-based, i.e. ROM address minus 0x08000000), or null if none found.
    /// </summary>
    public int? Allocate(int length)
    {
        while (runIndex < freeRuns.Count)
        {
            var (start, runLength) = freeRuns[runIndex];
            var runEnd = start + runLength;
            var alignedCursor = Math.Max(AlignUp(cursor), AlignUp(start));

            if (alignedCursor + length <= runEnd)
            {
                cursor = alignedCursor + length;
                return alignedCursor;
            }

            runIndex++;
            if (runIndex < freeRuns.Count)
            {
                cursor = freeRuns[runIndex].Start;
            }
        }

        return null;
    }

    private static int AlignUp(int value) => (value + Alignment - 1) / Alignment * Alignment;

    private static List<(int Start, int Length)> FindFreeRuns(byte[] rom)
    {
        var runs = new List<(int, int)>();
        var i = 0;

        while (i < rom.Length)
        {
            if (rom[i] != FillerByte)
            {
                i++;
                continue;
            }

            var start = i;
            while (i < rom.Length && rom[i] == FillerByte)
            {
                i++;
            }

            var length = i - start;
            if (length >= MinRunLength)
            {
                runs.Add((start, length));
            }
        }

        runs.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return runs;
    }
}
