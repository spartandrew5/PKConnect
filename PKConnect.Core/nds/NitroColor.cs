using SixLabors.ImageSharp.PixelFormats;

namespace PKConnect.Core.nds;

/// <summary>BGR555 (5 bits/channel) colour conversion shared by every Nitro sprite codec.</summary>
public static class NitroColor
{
    public static Rgba32 FromBgr555(int bgr555)
    {
        var r = (byte)((bgr555 & 0x1F) << 3);
        var g = (byte)(((bgr555 >> 5) & 0x1F) << 3);
        var b = (byte)(((bgr555 >> 10) & 0x1F) << 3);
        return new Rgba32(r, g, b, 255);
    }

    public static int ToBgr555(Rgba32 color)
    {
        var r = Math.Min(31, (int)Math.Round(color.R / 8.0));
        var g = Math.Min(31, (int)Math.Round(color.G / 8.0));
        var b = Math.Min(31, (int)Math.Round(color.B / 8.0));
        return r | (g << 5) | (b << 10);
    }
}
