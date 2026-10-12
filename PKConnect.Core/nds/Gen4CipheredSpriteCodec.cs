using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PKConnect.Core.nds;

/// <summary>
/// HGSS's (and DP/Pt's) "duplicate" front/back sprite entries - a second
/// NCGR alongside the normal battle sprite (front a/0/5/8 member 4/9, back
/// a/0/0/6 member 4/9/79/84), reusing the normal entry's palette but storing
/// its own pixel data XOR-enciphered with a seeded LCG stream, not tiled at
/// all: always a flat 160x80 canvas (usually two 80x80 poses side by side -
/// "Throwing"/"Pointing" for Back, a single pose + an unused half for Front),
/// shown during the "send-out" transition animation and the Hall of Fame
/// screen. This is what the community's PokeDSPic tool's Expert Mode edits,
/// and why a pack's normal battle sprite alone doesn't fix the sprite seen
/// during that transition.
///
/// Reverse-engineered by decompiling PokeDsPic_DP.exe's MakeImage/SaveBin
/// (the "_HG SS"-named build actually runs the LCG the wrong direction for
/// this data and produces garbage - the "_DP" build is the one that decodes
/// real HG/SS ROM bytes into a clean image). Every fixed-size constant below
/// (48-byte header, 3200 16-bit words, 0xFFFF-masked 32-bit LCG with
/// multiplier 1103515245 / increment 24691) is copied verbatim from that
/// decompilation, not derived.
/// </summary>
public static class Gen4CipheredSpriteCodec
{
    private const int DataOffset = 48;
    private const int WordCount = 3200;
    private const int Width = 160;
    private const int Height = 80;
    private const uint LcgMultiplier = 1103515245;
    private const uint LcgIncrement = 24691;

    /// <summary>
    /// Decodes the enciphered 160x80 canvas. The very last pixel group
    /// (word 3199, pixels 12796-12799 - the bottom-right corner) always
    /// decodes to 0 regardless of its true stored value, a quirk of the
    /// cipher construction (its seed is that same word, so it always
    /// self-XORs to zero) - harmless in practice since that corner is
    /// background in every real sprite this format stores.
    /// </summary>
    public static Image<Rgba32> Decode(byte[] ncgr, Rgba32[] palette)
    {
        var words = new ushort[WordCount];
        for (var i = 0; i < WordCount; i++)
        {
            words[i] = BitConverter.ToUInt16(ncgr, DataOffset + i * 2);
        }

        var seed = (uint)words[WordCount - 1];
        for (var i = WordCount - 1; i >= 0; i--)
        {
            words[i] = (ushort)(words[i] ^ (ushort)(seed & 0xFFFF));
            seed *= LcgMultiplier;
            seed += LcgIncrement;
        }

        var image = new Image<Rgba32>(Width, Height);
        for (var w = 0; w < WordCount; w++)
        {
            var word = words[w];
            var pixelBase = w * 4;
            for (var n = 0; n < 4; n++)
            {
                var index = (word >> (n * 4)) & 0xF;
                var pixel = pixelBase + n;
                image[pixel % Width, pixel / Width] = palette[index];
            }
        }
        return image;
    }

    /// <summary>
    /// Re-encodes a 160x80 image into <paramref name="ncgr"/> in place
    /// (same length, same 48-byte header preserved). <paramref name="resolveIndex"/>
    /// maps a pixel colour to a 0-15 palette index (the caller already knows
    /// which palette this member pairs with).
    /// </summary>
    public static void Encode(byte[] ncgr, Image<Rgba32> replacement, Func<Rgba32, int> resolveIndex)
    {
        if (replacement.Width != Width || replacement.Height != Height)
        {
            throw new ArgumentException($"Replacement image is {replacement.Width}x{replacement.Height}, expected {Width}x{Height}.");
        }

        var words = new ushort[WordCount];
        for (var w = 0; w < WordCount; w++)
        {
            var pixelBase = w * 4;
            var value = 0;
            for (var n = 0; n < 4; n++)
            {
                var pixel = pixelBase + n;
                var index = resolveIndex(replacement[pixel % Width, pixel / Width]);
                value |= (index & 0xF) << (n * 4);
            }
            words[w] = (ushort)value;
        }

        // The decoder seeds its LCG from the stored last word, which only equals the encoder's seed
        // when that word's plaintext is 0 (see Decode). Any non-background pixel there turns the
        // whole canvas into noise in-game, so force the bottom-right pixel group to slot 0.
        words[WordCount - 1] = 0;

        var seed = 31315u;
        for (var i = WordCount - 1; i >= 0; i--)
        {
            seed += words[i];
        }
        for (var i = WordCount - 1; i >= 0; i--)
        {
            words[i] = (ushort)(words[i] ^ (ushort)(seed & 0xFFFF));
            seed *= LcgMultiplier;
            seed += LcgIncrement;
        }

        for (var i = 0; i < WordCount; i++)
        {
            var bytes = BitConverter.GetBytes(words[i]);
            ncgr[DataOffset + i * 2] = bytes[0];
            ncgr[DataOffset + i * 2 + 1] = bytes[1];
        }
    }
}
