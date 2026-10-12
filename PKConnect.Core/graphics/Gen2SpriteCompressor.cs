namespace PKConnect.Core.graphics;

/// <summary>
/// Minimal encoder for the Generation 2 sprite compression format ("LC_LZ3" /
/// LZ3, documented at https://sneslab.net/wiki/LZ3).
///
/// This emits only the "direct copy" command (command 0): a header byte with
/// the count in its low 5 bits, followed by that many literal bytes, repeated
/// until the input is consumed, then the 0xFF terminator. It is a port of
/// UPR-FVX's <c>Gen2NullCompressor</c>. The output is larger than GameFreak's
/// original compressor produced, but it is a valid LZ3 stream that the in-game
/// decompressor reads back exactly - which is all the ROM patcher needs.
/// </summary>
public static class Gen2SpriteCompressor
{
    private const int MaxDirectCopy = 32; // command 0 count fits in 5 bits (1..32)
    private const byte Terminator = 0xFF;

    public static byte[] Compress(ReadOnlySpan<byte> uncompressed)
    {
        var outBytes = new byte[uncompressed.Length + (uncompressed.Length + MaxDirectCopy - 1) / MaxDirectCopy + 1];
        var size = 0;
        var pos = 0;

        while (pos < uncompressed.Length)
        {
            var chunk = Math.Min(uncompressed.Length - pos, MaxDirectCopy);
            outBytes[size++] = (byte)((chunk - 1) & 0b11111); // command 0, count-1
            uncompressed.Slice(pos, chunk).CopyTo(outBytes.AsSpan(size));
            size += chunk;
            pos += chunk;
        }

        outBytes[size++] = Terminator;
        return outBytes[..size];
    }
}
