namespace PKConnect.Core.patching;

/// <summary>
/// Minimal BPS (Beat Patch System) encoder/decoder, mGBA's other auto-loaded
/// patch format alongside IPS. Used only for base patches whose vanilla-to-
/// patched diff is a relink-wide address shift rather than a handful of small
/// edits (currently Ruby/Sapphire: that decomp's linker script has no safe
/// "extra" section, so the Connect base patch's new code shifts almost every
/// absolute-address literal in the ROM - a few hundred KB of genuinely
/// scattered byte differences that a naive record-based format like IPS would
/// have to write out byte-for-byte (multi-MB), but BPS's copy/delta commands
/// handle cheaply since most of the ROM is still byte-identical, just at a
/// shifted address).
///
/// The encoder is a straightforward hash-chained LZ77-style matcher against
/// the source file, plus same-offset "SourceRead" runs for the common case
/// where a byte hasn't moved at all.
/// </summary>
public static class BpsPatchService
{
    private const int HashBytes = 6;
    private const int MinMatch = 8;
    private const int CmdSourceRead = 0;
    private const int CmdTargetRead = 1;
    private const int CmdSourceCopy = 2;
    private const int CmdTargetCopy = 3;

    public static byte[] Encode(byte[] source, byte[] target)
    {
        var hashTable = BuildHashTable(source);

        var actions = new List<byte>(target.Length / 16);
        var srcRel = 0;
        var outPos = 0;
        int? pendingLiteralStart = null;

        void FlushLiteral(int end)
        {
            if (pendingLiteralStart is not { } start || end <= start)
            {
                pendingLiteralStart = null;
                return;
            }

            var length = end - start;
            WriteNumber(actions, ((ulong)(length - 1) << 2) | CmdTargetRead);
            actions.AddRange(new ReadOnlySpan<byte>(target, start, length));
            pendingLiteralStart = null;
        }

        while (outPos < target.Length)
        {
            if (outPos < source.Length && source[outPos] == target[outPos])
            {
                var run = MatchLength(source, outPos, target, outPos, target.Length - outPos);
                if (run >= 4)
                {
                    FlushLiteral(outPos);
                    WriteNumber(actions, ((ulong)(run - 1) << 2) | CmdSourceRead);
                    outPos += run;
                    continue;
                }
            }

            if (outPos + HashBytes <= target.Length
                && hashTable.TryGetValue(HashAt(target, outPos), out var candidate))
            {
                var run = MatchLength(source, candidate, target, outPos, target.Length - outPos);
                if (run >= MinMatch)
                {
                    FlushLiteral(outPos);
                    var delta = candidate - srcRel;
                    WriteNumber(actions, ((ulong)(run - 1) << 2) | CmdSourceCopy);
                    WriteNumber(actions, EncodeSigned(delta));
                    srcRel = candidate + run;
                    outPos += run;
                    continue;
                }
            }

            pendingLiteralStart ??= outPos;
            outPos++;
        }

        FlushLiteral(outPos);

        var header = new List<byte>();
        header.AddRange("BPS1"u8.ToArray());
        WriteNumber(header, (ulong)source.Length);
        WriteNumber(header, (ulong)target.Length);
        WriteNumber(header, 0); // metadata size

        var body = new List<byte>(header.Count + actions.Count + 12);
        body.AddRange(header);
        body.AddRange(actions);

        var sourceCrc = Crc32.Compute(source);
        var targetCrc = Crc32.Compute(target);
        AppendUInt32Le(body, sourceCrc);
        AppendUInt32Le(body, targetCrc);

        var patchCrc = Crc32.Compute(body.ToArray());
        AppendUInt32Le(body, patchCrc);

        return body.ToArray();
    }

    /// <summary>
    /// Encodes a BPS patch directly from a known, same-size, non-overlapping
    /// edit list instead of diffing two full files. <see cref="Encode"/>'s
    /// hash-chain matcher is O(source length) with a heavy per-byte
    /// dictionary-lookup constant - fine for a GBA ROM, but a 128MB NDS ROM
    /// took ~15 minutes. Every Gen 4 sprite edit is a same-size in-place
    /// overwrite (see Btx0SpriteCodec), so the source/target diff is
    /// trivially known up front: unchanged runs between edits are plain
    /// SourceRead (same position in source and target, no search needed),
    /// and each edit's bytes are a TargetRead. This is O(edited bytes) for
    /// the matching itself; the only remaining O(file size) work is the
    /// mandatory whole-file CRC32 for the header/footer, a simple byte loop
    /// with none of the matcher's overhead.
    /// </summary>
    public static byte[] EncodeSparse(byte[] source, IReadOnlyList<(int Offset, byte[] Data)> edits)
    {
        var sorted = edits.Where(e => e.Data.Length > 0).OrderBy(e => e.Offset).ToList();

        var target = (byte[])source.Clone();
        foreach (var (offset, data) in sorted)
        {
            Array.Copy(data, 0, target, offset, data.Length);
        }

        var actions = new List<byte>();
        var cursor = 0;
        foreach (var (offset, data) in sorted)
        {
            if (offset > cursor)
            {
                WriteNumber(actions, ((ulong)(offset - cursor - 1) << 2) | CmdSourceRead);
            }
            WriteNumber(actions, ((ulong)(data.Length - 1) << 2) | CmdTargetRead);
            actions.AddRange(data);
            cursor = offset + data.Length;
        }
        if (cursor < source.Length)
        {
            WriteNumber(actions, ((ulong)(source.Length - cursor - 1) << 2) | CmdSourceRead);
        }

        var header = new List<byte>();
        header.AddRange("BPS1"u8.ToArray());
        WriteNumber(header, (ulong)source.Length);
        WriteNumber(header, (ulong)target.Length);
        WriteNumber(header, 0); // metadata size

        var body = new List<byte>(header.Count + actions.Count + 12);
        body.AddRange(header);
        body.AddRange(actions);

        AppendUInt32Le(body, Crc32.Compute(source));
        AppendUInt32Le(body, Crc32.Compute(target));
        AppendUInt32Le(body, Crc32.Compute(body.ToArray()));

        return body.ToArray();
    }

    private const int BlockSize = 32;

    /// <summary>
    /// <see cref="Encode"/> for 100MB+ ROMs (NDS base patches): <see cref="Encode"/>'s per-byte
    /// hash table would need billions of bytes and ~15 minutes for a 128MB ROM. Here the source is
    /// indexed only every <see cref="BlockSize"/> bytes and the target is scanned with a rolling
    /// hash, rsync-style; a hit is verified and extended forward as far as the bytes keep matching.
    /// A rebuilt NDS ROM is mostly the same files at shifted offsets, so a few long SourceCopy
    /// runs cover almost everything. Offline use only (building a base patch once per decomp build).
    /// </summary>
    public static byte[] EncodeLarge(byte[] source, byte[] target)
    {
        var index = new Dictionary<ulong, int>(source.Length / BlockSize + 1);
        for (var i = 0; i + BlockSize <= source.Length; i += BlockSize)
        {
            index.TryAdd(BlockHash(source, i), i);
        }

        // Rolling polynomial hash over BlockSize bytes: h = sum(b[k] * P^(BlockSize-1-k)).
        const ulong P = 1_099_511_628_211UL;
        ulong pow = 1;
        for (var k = 1; k < BlockSize; k++)
        {
            pow *= P;
        }

        var actions = new List<byte>(1 << 20);
        var srcRel = 0;
        var outPos = 0;
        var literalStart = -1;

        void FlushLiteral(int end)
        {
            if (literalStart < 0 || end <= literalStart)
            {
                literalStart = -1;
                return;
            }
            WriteNumber(actions, ((ulong)(end - literalStart - 1) << 2) | CmdTargetRead);
            actions.AddRange(new ReadOnlySpan<byte>(target, literalStart, end - literalStart));
            literalStart = -1;
        }

        ulong hash = 0;
        var hashFrom = -1; // outPos the rolling hash currently covers, -1 = stale
        while (outPos < target.Length)
        {
            // Same position, unchanged: the cheapest case (header, unmoved files).
            if (outPos < source.Length && source[outPos] == target[outPos])
            {
                var run = MatchLength(source, outPos, target, outPos, target.Length - outPos);
                if (run >= 16)
                {
                    FlushLiteral(outPos);
                    WriteNumber(actions, ((ulong)(run - 1) << 2) | CmdSourceRead);
                    outPos += run;
                    hashFrom = -1;
                    continue;
                }
            }

            if (outPos + BlockSize <= target.Length)
            {
                if (hashFrom != outPos)
                {
                    hash = BlockHash(target, outPos);
                }
                if (index.TryGetValue(hash, out var candidate))
                {
                    var run = MatchLength(source, candidate, target, outPos, target.Length - outPos);
                    if (run >= BlockSize)
                    {
                        FlushLiteral(outPos);
                        WriteNumber(actions, ((ulong)(run - 1) << 2) | CmdSourceCopy);
                        WriteNumber(actions, EncodeSigned(candidate - srcRel));
                        srcRel = candidate + run;
                        outPos += run;
                        hashFrom = -1;
                        continue;
                    }
                }

                // Roll the window one byte forward.
                if (outPos + BlockSize < target.Length)
                {
                    hash = (hash - target[outPos] * pow) * P + target[outPos + BlockSize];
                    hashFrom = outPos + 1;
                }
            }

            if (literalStart < 0)
            {
                literalStart = outPos;
            }
            outPos++;
        }
        FlushLiteral(outPos);

        return Finish(source, target, actions);

        static ulong BlockHash(byte[] data, int offset)
        {
            ulong h = 0;
            for (var k = 0; k < BlockSize; k++)
            {
                h = h * P + data[offset + k];
            }
            return h;
        }
    }

    /// <summary>
    /// A new patch equal to <paramref name="basePatch"/> followed by same-size in-place
    /// <paramref name="edits"/> on its output, without diffing the whole ROM: the base patch's
    /// actions are replayed and split around the edits, which become TargetRead literals.
    /// <paramref name="baseTarget"/> must be <c>Decode(source, basePatch)</c> (for its literal bytes).
    /// </summary>
    public static byte[] OverlayEdits(byte[] source, byte[] basePatch, byte[] baseTarget, IReadOnlyList<(int Offset, byte[] Data)> edits)
    {
        var sorted = edits.Where(e => e.Data.Length > 0).OrderBy(e => e.Offset).ToList();
        var target = (byte[])baseTarget.Clone();
        foreach (var (offset, data) in sorted)
        {
            Array.Copy(data, 0, target, offset, data.Length);
        }

        // Edited target ranges, merged.
        var ranges = new List<(int Start, int End)>();
        foreach (var (offset, data) in sorted)
        {
            var end = offset + data.Length;
            if (ranges.Count > 0 && offset <= ranges[^1].End)
            {
                ranges[^1] = (ranges[^1].Start, Math.Max(ranges[^1].End, end));
            }
            else
            {
                ranges.Add((offset, end));
            }
        }

        var actions = new List<byte>(basePatch.Length + sorted.Sum(e => e.Data.Length) + 1024);
        var outSrcRel = 0; // SourceCopy cursor of the patch being written
        var rangeIndex = 0;

        void EmitLiteral(int start, int end)
        {
            WriteNumber(actions, ((ulong)(end - start - 1) << 2) | CmdTargetRead);
            actions.AddRange(new ReadOnlySpan<byte>(target, start, end - start));
        }

        // Emits target [start, end) as produced by one base action, minus the edited ranges.
        // sourceAt(pos) = where that output byte came from in the source, or -1 for literal bytes.
        void EmitSegment(int start, int end, int sourceStart, bool sameOffset)
        {
            var pos = start;
            while (pos < end)
            {
                while (rangeIndex < ranges.Count && ranges[rangeIndex].End <= pos)
                {
                    rangeIndex++;
                }
                var next = rangeIndex < ranges.Count ? ranges[rangeIndex] : (Start: int.MaxValue, End: int.MaxValue);
                if (next.Start <= pos)
                {
                    var editEnd = Math.Min(end, next.End);
                    EmitLiteral(pos, editEnd);
                    pos = editEnd;
                    continue;
                }

                var pieceEnd = Math.Min(end, next.Start);
                var length = pieceEnd - pos;
                if (sameOffset)
                {
                    WriteNumber(actions, ((ulong)(length - 1) << 2) | CmdSourceRead);
                }
                else if (sourceStart >= 0)
                {
                    var from = sourceStart + (pos - start);
                    WriteNumber(actions, ((ulong)(length - 1) << 2) | CmdSourceCopy);
                    WriteNumber(actions, EncodeSigned(from - outSrcRel));
                    outSrcRel = from + length;
                }
                else
                {
                    EmitLiteral(pos, pieceEnd);
                }
                pos = pieceEnd;
            }
        }

        var p = 4;
        var sourceSize = (int)ReadNumber(basePatch, ref p);
        var targetSize = (int)ReadNumber(basePatch, ref p);
        var metadataSize = (int)ReadNumber(basePatch, ref p);
        p += metadataSize;
        if (sourceSize != source.Length || targetSize != baseTarget.Length)
        {
            throw new InvalidDataException("Base patch doesn't match the source/target sizes");
        }

        var outPos = 0;
        var srcRel = 0;
        var endPos = basePatch.Length - 12;
        while (p < endPos)
        {
            var value = ReadNumber(basePatch, ref p);
            var cmd = (int)(value & 3);
            var length = (int)(value >> 2) + 1;
            switch (cmd)
            {
                case CmdSourceRead:
                    EmitSegment(outPos, outPos + length, outPos, sameOffset: true);
                    break;
                case CmdTargetRead:
                    p += length;
                    EmitSegment(outPos, outPos + length, -1, sameOffset: false);
                    break;
                case CmdSourceCopy:
                    srcRel += DecodeSigned(ReadNumber(basePatch, ref p));
                    EmitSegment(outPos, outPos + length, srcRel, sameOffset: false);
                    srcRel += length;
                    break;
                case CmdTargetCopy:
                    // Re-emitted as literals: an edit may have changed the bytes it copies from.
                    ReadNumber(basePatch, ref p);
                    EmitSegment(outPos, outPos + length, -1, sameOffset: false);
                    break;
            }
            outPos += length;
        }

        return Finish(source, target, actions);
    }

    private static byte[] Finish(byte[] source, byte[] target, List<byte> actions)
    {
        var header = new List<byte>();
        header.AddRange("BPS1"u8.ToArray());
        WriteNumber(header, (ulong)source.Length);
        WriteNumber(header, (ulong)target.Length);
        WriteNumber(header, 0); // metadata size

        var body = new List<byte>(header.Count + actions.Count + 12);
        body.AddRange(header);
        body.AddRange(actions);
        AppendUInt32Le(body, Crc32.Compute(source));
        AppendUInt32Le(body, Crc32.Compute(target));
        AppendUInt32Le(body, Crc32.Compute(body.ToArray()));
        return body.ToArray();
    }

    public static byte[] Decode(byte[] source, byte[] patch)
    {
        if (patch.Length < 4 || patch[0] != 'B' || patch[1] != 'P' || patch[2] != 'S' || patch[3] != '1')
        {
            throw new InvalidDataException("Not a BPS patch (missing BPS1 header)");
        }

        var pos = 4;
        var sourceSize = (int)ReadNumber(patch, ref pos);
        var targetSize = (int)ReadNumber(patch, ref pos);
        var metadataSize = (int)ReadNumber(patch, ref pos);
        pos += metadataSize;

        if (sourceSize != source.Length)
        {
            throw new InvalidDataException(
                $"BPS source size mismatch: patch expects {sourceSize} bytes, got {source.Length}");
        }

        var target = new byte[targetSize];
        var outPos = 0;
        var srcRel = 0;
        var tgtRel = 0;
        var endPos = patch.Length - 12; // trailing 3 CRC32s

        while (pos < endPos)
        {
            var value = ReadNumber(patch, ref pos);
            var cmd = (int)(value & 3);
            var length = (int)(value >> 2) + 1;

            switch (cmd)
            {
                case CmdSourceRead:
                    Array.Copy(source, outPos, target, outPos, length);
                    outPos += length;
                    break;

                case CmdTargetRead:
                    Array.Copy(patch, pos, target, outPos, length);
                    pos += length;
                    outPos += length;
                    break;

                case CmdSourceCopy:
                    srcRel += DecodeSigned(ReadNumber(patch, ref pos));
                    Array.Copy(source, srcRel, target, outPos, length);
                    srcRel += length;
                    outPos += length;
                    break;

                case CmdTargetCopy:
                    tgtRel += DecodeSigned(ReadNumber(patch, ref pos));
                    for (var i = 0; i < length; i++)
                    {
                        target[outPos + i] = target[tgtRel + i];
                    }
                    tgtRel += length;
                    outPos += length;
                    break;
            }
        }

        if (outPos != targetSize)
        {
            throw new InvalidDataException($"BPS patch produced {outPos} bytes, expected {targetSize}");
        }

        return target;
    }

    private static Dictionary<ulong, int> BuildHashTable(byte[] data)
    {
        var table = new Dictionary<ulong, int>(Math.Max(1, data.Length - HashBytes + 1));
        for (var i = 0; i <= data.Length - HashBytes; i++)
        {
            // Later occurrences overwrite earlier ones - better locality (smaller
            // relative offsets) for the common "shifted but mostly intact" case.
            table[HashAt(data, i)] = i;
        }
        return table;
    }

    private static ulong HashAt(byte[] data, int offset)
    {
        ulong h = 0;
        for (var i = 0; i < HashBytes; i++)
        {
            h = (h << 8) | data[offset + i];
        }
        return h;
    }

    private static int MatchLength(byte[] a, int ai, byte[] b, int bi, int limit)
    {
        var n = 0;
        var maxN = Math.Min(limit, Math.Min(a.Length - ai, b.Length - bi));
        while (n < maxN && a[ai + n] == b[bi + n])
        {
            n++;
        }
        return n;
    }

    private static void WriteNumber(List<byte> output, ulong n)
    {
        while (true)
        {
            var x = n & 0x7f;
            n >>= 7;
            if (n == 0)
            {
                output.Add((byte)(0x80 | x));
                break;
            }
            output.Add((byte)x);
            n--;
        }
    }

    private static ulong ReadNumber(byte[] data, ref int pos)
    {
        ulong n = 0;
        ulong shift = 1;
        while (true)
        {
            var x = data[pos++];
            n += (ulong)(x & 0x7f) * shift;
            if ((x & 0x80) != 0)
            {
                break;
            }
            shift <<= 7;
            n += shift;
        }
        return n;
    }

    private static ulong EncodeSigned(int n) => n < 0 ? ((ulong)(-n) << 1) | 1 : (ulong)n << 1;

    private static int DecodeSigned(ulong n)
    {
        var magnitude = (int)(n >> 1);
        return (n & 1) != 0 ? -magnitude : magnitude;
    }

    private static void AppendUInt32Le(List<byte> output, uint value)
    {
        output.Add((byte)(value & 0xFF));
        output.Add((byte)((value >> 8) & 0xFF));
        output.Add((byte)((value >> 16) & 0xFF));
        output.Add((byte)((value >> 24) & 0xFF));
    }

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                }
                table[i] = c;
            }
            return table;
        }

        public static uint Compute(byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
