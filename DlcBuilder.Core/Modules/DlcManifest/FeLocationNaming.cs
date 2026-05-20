using System.Text;

namespace DlcBuilder.Modules.DlcManifest;

/// FE menu thumbnail naming. Stock DLCs use the layout
/// `fe\source\images\locations\DLC_Location_*` with `.rps3` on disk and
/// `*.Texture` in the arena TOC. The base name is the map slug (DIST_* stem,
/// after <see cref="DlcSpec.ToSlug"/>) capitalized per-segment.
public static class FeLocationNaming
{
    /// Base asset name without extension, e.g. `DLC_Location_Park` or
    /// `DLC_Location_Pier17`. Built from the map slug.
    public static string FeLocationAssetBaseName(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return "DLC_Location_Unknown";

        string[] parts = slug.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var tail = new StringBuilder();
        foreach (string p in parts)
        {
            if (p.Length == 0) continue;
            tail.Append(char.ToUpperInvariant(p[0]));
            if (p.Length > 1) tail.Append(p, 1, p.Length - 1);
        }
        if (tail.Length == 0) tail.Append("Unknown");
        return "DLC_Location_" + tail;
    }
}
