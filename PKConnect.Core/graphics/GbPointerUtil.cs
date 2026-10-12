namespace PKConnect.Core.graphics;

/// <summary>
/// Shared GB/GBC 2-byte pointer helpers used by the compressed-image writers
/// (Gen 1 front/back, Gen 2 back).
/// </summary>
public static class GbPointerUtil
{
    /// <summary>2-byte little-endian GB pointer value for an absolute ROM offset.</summary>
    public static byte[] PtrBytes(int absOffset)
    {
        var word = (absOffset % GbBankAllocator.BankSize) + 0x4000;
        return [(byte)(word & 0xFF), (byte)((word >> 8) & 0xFF)];
    }

    /// <summary>Resolves the 2-byte pointer at <paramref name="site"/> against a bank.</summary>
    public static int ReadPointer(byte[] rom, int site, int bank)
    {
        if (site < 0 || site + 1 >= rom.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(site));
        }
        var word = rom[site] | (rom[site + 1] << 8);
        return word < 0x4000 ? word : (word % 0x4000) + bank * GbBankAllocator.BankSize;
    }

    /// <summary>True if every site holds the same 2-byte little-endian word.</summary>
    public static bool AllReadSameWord(byte[] rom, int[] sites, out int value)
    {
        value = 0;
        for (var i = 0; i < sites.Length; i++)
        {
            if (sites[i] < 0 || sites[i] + 1 >= rom.Length)
            {
                return false;
            }
            var w = rom[sites[i]] | (rom[sites[i] + 1] << 8);
            if (i == 0)
            {
                value = w;
            }
            else if (w != value)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>True if every site holds the same byte.</summary>
    public static bool AllReadSameByte(byte[] rom, int[] sites, out byte value)
    {
        value = 0;
        for (var i = 0; i < sites.Length; i++)
        {
            if (sites[i] < 0 || sites[i] >= rom.Length)
            {
                return false;
            }
            if (i == 0)
            {
                value = rom[sites[i]];
            }
            else if (rom[sites[i]] != value)
            {
                return false;
            }
        }
        return true;
    }
}
