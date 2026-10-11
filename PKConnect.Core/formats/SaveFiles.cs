using System.Diagnostics.CodeAnalysis;
using PKHeX.Core;

namespace PKConnect.Core.formats;

/// <summary>Reading a save file the way Connect needs it: knowing where it came from.</summary>
public static class SaveFiles
{
    /// <summary>
    /// Reads a save from its bytes and records its path on it. PKHeX takes the path as a hint
    /// only and doesn't keep it for every kind of save, and much depends on it here: which
    /// expansion build wrote a save, and which game of a pair it belongs to, is told by its name.
    /// </summary>
    public static bool TryLoad(byte[] bytes, [NotNullWhen(true)] out SaveFile? save, string? path = null)
    {
        if (!SaveUtil.TryGetSaveFile(bytes, out save, path) || save is null)
        {
            save = null;
            return false;
        }
        if (!string.IsNullOrEmpty(path) && string.IsNullOrEmpty(save.Metadata.FilePath))
            save.Metadata.SetExtraInfo(path);
        return true;
    }
}