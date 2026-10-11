using PKHeX.Core;

namespace PKConnect.Core.nds;

/// <summary>
/// In-place string replacement inside one Gen 4 message bank (a member of msgdata/*_msg.narc).
/// Format (pokeplatinum tools/msgenc): u16 count, u16 key; then count (offset, length) entries,
/// each XORed with (765 * n * key) (n = 1-based index, low 16 bits duplicated); then the strings
/// as u16 codes, each string XORed with a rolling seed starting at n * 596947 (+18749 per char),
/// terminated by 0xFFFF. Only same-size edits are made: the new text must fit the existing slot
/// (it's truncated otherwise) and the rest of the slot is filled with terminators, so nothing
/// else in the bank - or the ROM - moves.
/// </summary>
public static class Gen4MessageBank
{
    /// <summary>Byte offset (within the bank) and u16 length of string <paramref name="index"/> (0-based).</summary>
    public static (int Offset, int Length)? GetSlot(ReadOnlySpan<byte> bank, int index)
    {
        if (bank.Length < 4) return null;
        var count = BitConverter.ToUInt16(bank[..2]);
        var key = BitConverter.ToUInt16(bank[2..4]);
        if (index < 0 || index >= count) return null;

        var n = index + 1;
        var allocKey = (uint)((765 * n * key) & 0xFFFF);
        allocKey |= allocKey << 16;
        var entry = 4 + index * 8;
        var offset = (int)(BitConverter.ToUInt32(bank[entry..]) ^ allocKey);
        var length = (int)(BitConverter.ToUInt32(bank[(entry + 4)..]) ^ allocKey);
        return offset + length * 2 <= bank.Length ? (offset, length) : null;
    }

    /// <summary>
    /// Bytes to write at the slot's offset so string <paramref name="index"/> reads
    /// <paramref name="text"/> (Gen 4 character set, truncated to the slot).
    /// </summary>
    public static (int OffsetInBank, byte[] Data)? EncodeReplacement(ReadOnlySpan<byte> bank, int index, string text)
    {
        if (GetSlot(bank, index) is not { } slot || slot.Length < 1) return null;

        var codes = new ushort[slot.Length];
        Array.Fill(codes, (ushort)0xFFFF);
        var buffer = new byte[(slot.Length) * 2];
        // PKHeX's Gen 4 string encoder writes the codes + terminator.
        var encoded = new byte[(text.Length + 1) * 2];
        StringConverter4.SetString(encoded, text, text.Length, (int)LanguageID.English, StringConverterOption.None);
        var chars = new List<ushort>();
        for (var i = 0; i * 2 + 1 < encoded.Length; i++)
        {
            var c = BitConverter.ToUInt16(encoded, i * 2);
            if (c == 0xFFFF) break;
            chars.Add(c);
        }

        if (DecodeCodes(bank, index, slot) is [0xF100, ..])
        {
            // Trainer names are stored compressed ({TRNAME} in msgenc): 0xF100, then 9-bit character
            // codes packed LSB-first into 15-bit words, padded with 1 bits (0x1FF = end), then 0xFFFF.
            // Keep the slot's word count: as many characters as fit, the rest end markers.
            var words = slot.Length - 2;
            var maxChars = words * 15 / 9;
            chars = chars.Where(c => c < 0x1FF).Take(maxChars).ToList();
            codes[0] = 0xF100;
            ulong bits = 0;
            var bitCount = 0;
            var w = 1;
            foreach (var c in chars)
            {
                bits |= (ulong)c << bitCount;
                bitCount += 9;
                while (bitCount >= 15 && w <= words)
                {
                    codes[w++] = (ushort)(bits & 0x7FFF);
                    bits >>= 15;
                    bitCount -= 15;
                }
            }
            while (w <= words)
            {
                bits |= ~0UL << bitCount; // fill with end-marker bits
                codes[w++] = (ushort)(bits & 0x7FFF);
                bits = ~0UL;
                bitCount = 0;
            }
            codes[slot.Length - 1] = 0xFFFF;
        }
        else
        {
            // Plain string: the codes, truncated so the terminator still fits.
            for (var i = 0; i < chars.Count && i < slot.Length - 1; i++)
            {
                codes[i] = chars[i];
            }
        }

        var seed = (ushort)((index + 1) * 596947);
        for (var i = 0; i < codes.Length; i++)
        {
            var enc = (ushort)(codes[i] ^ seed);
            seed = (ushort)(seed + 18749);
            buffer[i * 2] = (byte)enc;
            buffer[i * 2 + 1] = (byte)(enc >> 8);
        }
        return (slot.Offset, buffer);
    }

    /// <summary>Decodes string <paramref name="index"/> (for verification/logging), compressed names included.</summary>
    public static string? Read(ReadOnlySpan<byte> bank, int index)
    {
        if (GetSlot(bank, index) is not { } slot) return null;
        var codes = DecodeCodes(bank, index, slot);
        var chars = new List<ushort>();
        if (codes is [0xF100, ..])
        {
            ulong bits = 0;
            var bitCount = 0;
            foreach (var word in codes.Skip(1))
            {
                if (word == 0xFFFF) break;
                bits |= (ulong)(word & 0x7FFF) << bitCount;
                bitCount += 15;
                while (bitCount >= 9)
                {
                    var c = (ushort)(bits & 0x1FF);
                    bits >>= 9;
                    bitCount -= 9;
                    if (c == 0x1FF) goto done;
                    chars.Add(c);
                }
            }
        }
        else
        {
            chars.AddRange(codes.TakeWhile(c => c != 0xFFFF));
        }
    done:
        var raw = new byte[(chars.Count + 1) * 2];
        for (var i = 0; i < chars.Count; i++)
        {
            raw[i * 2] = (byte)chars[i];
            raw[i * 2 + 1] = (byte)(chars[i] >> 8);
        }
        raw[^2] = 0xFF;
        raw[^1] = 0xFF;
        return StringConverter4.GetString(raw);
    }

    private static ushort[] DecodeCodes(ReadOnlySpan<byte> bank, int index, (int Offset, int Length) slot)
    {
        var seed = (ushort)((index + 1) * 596947);
        var codes = new ushort[slot.Length];
        for (var i = 0; i < slot.Length; i++)
        {
            codes[i] = (ushort)(BitConverter.ToUInt16(bank[(slot.Offset + i * 2)..]) ^ seed);
            seed = (ushort)(seed + 18749);
        }
        return codes;
    }
}
