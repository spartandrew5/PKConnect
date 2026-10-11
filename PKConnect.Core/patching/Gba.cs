namespace PKConnect.Core.patching;

/// <summary>GBA ROM pointer / word helpers. ROM is mapped at 0x08000000.</summary>
public static class Gba
{
    public const uint RomBase = 0x08000000;

    public static uint ReadU32(byte[] rom, int offset) =>
        (uint)(rom[offset] | (rom[offset + 1] << 8) | (rom[offset + 2] << 16) | (rom[offset + 3] << 24));

    /// <summary>Reads a 32-bit ROM pointer and returns it as a file offset, or -1 if it isn't a ROM pointer.</summary>
    public static int ReadPointer(byte[] rom, int offset)
    {
        var v = ReadU32(rom, offset);
        if (v < RomBase || v >= RomBase + rom.Length)
        {
            return -1;
        }
        return (int)(v - RomBase);
    }

    public static byte[] PointerBytes(int fileOffset)
    {
        var v = RomBase + (uint)fileOffset;
        return [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];
    }
}
