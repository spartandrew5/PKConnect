using PKHeX.Core;

namespace PKConnect.Core.formats;

/// <summary>
/// SaveBlock1 differences PKHeX can't know about for pokeemerald-expansion FRLG builds. Their saves
/// use the Emerald structs, so PKHeX loads them as SAV3E and reads flags/vars/badges at Emerald's
/// positions. The flag array starts at the same offset (0x1270) but is only FRLG-sized (0x120 bytes,
/// vs Emerald's 0x12C), so the vars that follow start 12 bytes (6 vars) earlier than PKHeX assumes,
/// and the badge flags are FRLG's (0x820+, Emerald's are 0x867+).
/// Emerald expansion saves match vanilla Emerald here and need nothing special.
/// </summary>
public static class ExpansionSaveLayout
{
    private const int FrlgVarsOffset = 0x1390; // gSaveBlock1Ptr->vars, from GetVarPointer's literal pool
    private const int VarsCount = 0x100;       // VARS_COUNT (0x4000-0x40FF)
    private const int FrlgBadgeFlagStart = 0x820; // FLAG_BADGE01_GET

    public static bool IsFrlg(string? expansionGameId) =>
        expansionGameId is ExpansionSaveDetector.FireRedGameId or ExpansionSaveDetector.LeafGreenGameId;

    public static List<int> GetFrlgEventWork(SAV3 sav3)
    {
        var large = sav3.Large;
        var work = new List<int>(VarsCount);
        for (var i = 0; i < VarsCount; i++)
        {
            work.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(large[(FrlgVarsOffset + i * 2)..]));
        }
        return work;
    }

    public static void SetFrlgEventWork(SAV3 sav3, IReadOnlyList<int> work)
    {
        var large = sav3.Large;
        var len = Math.Min(VarsCount, work.Count);
        for (var i = 0; i < len; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                large[(FrlgVarsOffset + i * 2)..], (ushort)Math.Clamp(work[i], ushort.MinValue, ushort.MaxValue));
        }
    }

    public static int GetFrlgBadges(SAV3 sav3)
    {
        var badges = 0;
        for (var i = 0; i < 8; i++)
        {
            if (sav3.GetEventFlag(FrlgBadgeFlagStart + i))
                badges |= 1 << i;
        }
        return badges;
    }

    public static void SetFrlgBadges(SAV3 sav3, int badges)
    {
        for (var i = 0; i < 8; i++)
        {
            sav3.SetEventFlag(FrlgBadgeFlagStart + i, ((badges >> i) & 1) == 1);
        }
    }
}
