using System.Globalization;
using System.Text;
using DlcBuilder.Builders;

namespace DlcBuilder.Modules.LocXml;

/// Emits the `.loc` companion XML for a locator `.psg`. Format pinned to
/// shipping retail Skate 3 DLCs (verified across multiple shipping packs):
///
///   &lt;LocationTable&gt;
///       &lt;Location&gt;
///           &lt;Name&gt;freeskate_&lt;world&gt;_locator&lt;/Name&gt;
///           &lt;Category /&gt;
///           &lt;Transform&gt;1.000000, 0.000000, ..., tx, ty, tz, 1.000000&lt;/Transform&gt;
///       &lt;/Location&gt;
///       ...
///   &lt;/LocationTable&gt;
///
/// Stock `.loc` files do NOT contain `&lt;Description&gt;`, do NOT nest
/// `&lt;SubLocations&gt;`, and do NOT include the sub-spawn entries (those live
/// only in the `.psg`). We follow that convention.
///
/// Uses CRLF line endings and `F6` invariant culture for floats — those are
/// not just style choices, they're load-bearing for byte-identical retail
/// parity (the DLC verifier hashes `.loc` content as part of pack validation).
public static class LocXmlBuilder
{
    /// Build a `.loc` containing exactly one Location entry.
    /// `pairedCategory=false` (default) emits `<Category />` self-closing —
    /// matches retail mission `<key>_Sim.loc` and world `<world>_Sim.loc`.
    /// `pairedCategory=true` emits `<Category></Category>` paired — matches
    /// retail loose-root `[hashes]_Proc_Proc_Container_*_Sim_<id>.loc`.
    public static string Build(string locatorName, Transform44 transform, bool pairedCategory = false)
    {
        var sb = new StringBuilder(256);
        sb.Append("<LocationTable>\r\n");
        AppendLocation(sb, locatorName, transform, pairedCategory);
        sb.Append("</LocationTable>\r\n");
        return sb.ToString();
    }

    /// Build a `.loc` with the main Location followed by N sibling locations.
    /// Used by maps that ship multiple sub-spawns sharing one `.loc` file.
    /// See <see cref="Build"/> for the `pairedCategory` flag semantics.
    public static string BuildWithSiblings(
        string mainLocatorName,
        Transform44 mainTransform,
        IEnumerable<(string Name, Transform44 Transform)> additionalSiblings,
        bool pairedCategory = false)
    {
        ArgumentNullException.ThrowIfNull(additionalSiblings);
        var sb = new StringBuilder(512);
        sb.Append("<LocationTable>\r\n");
        AppendLocation(sb, mainLocatorName, mainTransform, pairedCategory);
        foreach (var (name, t) in additionalSiblings)
            AppendLocation(sb, name, t, pairedCategory);
        sb.Append("</LocationTable>\r\n");
        return sb.ToString();
    }

    private static void AppendLocation(StringBuilder sb, string name, Transform44 t, bool pairedCategory)
    {
        sb.Append("    <Location>\r\n");
        sb.Append("        <Name>").Append(name).Append("</Name>\r\n");
        // Retail uses two forms depending on filename pattern (verified by
        // raw-byte diff against shipping DW DLC):
        //   • mission `<key>_Sim.loc`  → `<Category />`           self-closing
        //   • world   `<world>_Sim.loc`→ `<Category />`           self-closing
        //   • loose-root `[hashes]_Proc_Proc_Container_*_Sim_<id>.loc`
        //                              → `<Category></Category>` paired
        // Both are valid XML; the bigfile packer hashes file content so the
        // form is load-bearing for byte-identical retail parity on the loose
        // root duplicate.
        sb.Append(pairedCategory
            ? "        <Category></Category>\r\n"
            : "        <Category />\r\n");
        sb.Append("        <Transform>").Append(FormatTransform(t)).Append("</Transform>\r\n");
        sb.Append("    </Location>\r\n");
    }

    private static string FormatTransform(Transform44 t)
    {
        var sb = new StringBuilder(16 * 11);
        for (int i = 0; i < 16; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(t.Rows[i].ToString("F6", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
