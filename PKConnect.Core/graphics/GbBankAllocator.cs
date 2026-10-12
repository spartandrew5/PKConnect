namespace PKConnect.Core.graphics;

/// <summary>
/// Minimal Game Boy free-space allocator. Gen 1/2 ROMs are bank-switched with
/// 2-byte pointers that only reach within one 0x4000 bank, so relocated data
/// must fit entirely inside a single bank. This hands out sequential runs from
/// the ROM's unused banks (0x00-filled in retail carts — the pret free-space
/// byte), never crossing a bank boundary.
///
/// <c>reservedRanges</c> are bytes an earlier stage of the same launch patch
/// already occupies (e.g. a Connect base patch's data at the start of an
/// otherwise unused bank). They are never handed out, and a bank stays usable
/// as long as everything outside them is still free.
///
/// Mirrors the intent of UPR-FVX's <c>BankDividedFreedSpace</c> / <c>freeUnusedBanks</c>,
/// pared down to what the player-graphics writer needs (a handful of allocations
/// per launch).
/// </summary>
public sealed class GbBankAllocator
{
    public const int BankSize = 0x4000;
    public const byte FreeByte = 0x00;

    private readonly List<int> _banks = [];

    // bank -> [Start, End) segments still available, in ROM order
    private readonly Dictionary<int, List<(int Start, int End)>> _freeByBank = [];

    public GbBankAllocator(
        byte[] rom,
        IEnumerable<int> unusedBanks,
        IReadOnlyList<(int Start, int End)>? reservedRanges = null)
    {
        foreach (var bank in unusedBanks.Distinct().OrderBy(b => b))
        {
            if (FreeSegments(rom, bank, reservedRanges) is { Count: > 0 } segments)
            {
                _banks.Add(bank);
                _freeByBank[bank] = segments;
            }
        }
    }

    /// <summary>
    /// The bank's segments outside <paramref name="reservedRanges"/>, or null
    /// when the bank isn't present or any of those bytes is already in use.
    /// </summary>
    private static List<(int Start, int End)>? FreeSegments(
        byte[] rom, int bank, IReadOnlyList<(int Start, int End)>? reservedRanges)
    {
        var bankStart = bank * BankSize;
        var bankEnd = bankStart + BankSize;
        if (bankStart < 0 || bankEnd > rom.Length)
        {
            return null;
        }

        var segments = new List<(int Start, int End)>();
        var cursor = bankStart;
        foreach (var (start, end) in (reservedRanges ?? Array.Empty<(int Start, int End)>())
            .Where(r => r.End > bankStart && r.Start < bankEnd)
            .OrderBy(r => r.Start))
        {
            if (start > cursor)
            {
                segments.Add((cursor, start));
            }
            cursor = Math.Max(cursor, end);
        }
        if (cursor < bankEnd)
        {
            segments.Add((cursor, bankEnd));
        }

        foreach (var (start, end) in segments)
        {
            for (var i = start; i < end; i++)
            {
                if (rom[i] != FreeByte)
                {
                    return null;
                }
            }
        }

        return segments;
    }

    /// <summary>
    /// Reserves <paramref name="length"/> contiguous bytes within one unused
    /// bank and returns the ROM file offset, or null if no bank has room.
    /// <paramref name="preferBank"/>, if free, is tried first.
    /// </summary>
    public int? Allocate(int length, int? preferBank = null)
    {
        if (length <= 0 || length > BankSize)
        {
            return null;
        }

        var order = _banks.AsEnumerable();
        if (preferBank is { } pref && _banks.Contains(pref))
        {
            order = new[] { pref }.Concat(_banks.Where(b => b != pref));
        }

        foreach (var bank in order)
        {
            var segments = _freeByBank[bank];
            for (var i = 0; i < segments.Count; i++)
            {
                var (start, end) = segments[i];
                if (end - start >= length)
                {
                    segments[i] = (start + length, end);
                    return start;
                }
            }
        }
        return null;
    }
}
