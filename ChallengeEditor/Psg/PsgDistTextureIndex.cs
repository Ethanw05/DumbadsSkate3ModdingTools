using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ChallengeEditor.Psg;

/// <summary>
/// Indexes standalone texture PSG files under a DIST folder. Runtime packages store them as
/// <c>{GUID:016X}.psg</c> under <c>cPres_*</c> / <c>cTex_*</c> / <c>cPres_Global</c> (same layout as BlenRose export).
/// </summary>
public static class PsgDistTextureIndex
{
    /// <summary>
    /// Maps texture GUID (from material channels / TOC) to the first matching file path on disk.
    /// Later paths overwrite earlier ones — last wins (typically tile-local overrides).
    /// </summary>
    public static Dictionary<ulong, string> Scan(string distFolder)
    {
        var map = new Dictionary<ulong, string>();
        if (string.IsNullOrEmpty(distFolder) || !Directory.Exists(distFolder))
            return map;

        foreach (string path in Directory.EnumerateFiles(distFolder, "*.psg", SearchOption.AllDirectories))
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            if (stem.Length < 16) continue;
            if (!ulong.TryParse(stem, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong g))
                continue;
            map[g] = path;
        }

        return map;
    }
}
