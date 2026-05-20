using DlcBuilder.Modules.DlcManifest;
using DlcBuilder.Modules.MissionTemplates;

namespace DlcBuilder.Modules.Freeskate;

/// Writes 12 placeholder asset stubs per map under
/// `&lt;outputDirectory&gt;/content/missions/freeskate_&lt;slug&gt;/`. Without these the
/// engine's mission asset loader fails when the freeskate location is started
/// ("You do not currently have the location needed to perform this action").
///
/// Confirmed via Danny Way DLC: the only freeskate-shaped folder DW ships
/// under `content\missions\` contains the exact 12-file stub set (24-32 bytes
/// per file, magic MMAP/CMAP/CSPA/ATOC).
///
/// Templates live in `&lt;AppContext.BaseDirectory&gt;/lib/mission_template/`. The
/// host app must copy them next to the assembly (usually via a csproj
/// `&lt;None Include="lib\mission_template\**\*"&gt;` entry with
/// CopyToOutputDirectory). When templates are missing this writer silently
/// skips — same forgiving behavior as `OtsMissionFolderWriter` so a broken
/// template setup doesn't fail the whole build.
///
/// Folder name convention: `freeskate_&lt;slug&gt;` matches the locator NAME minus
/// the `_locator` suffix that `WorldLocatorFilesWriter` emits.
public static class MissionStubsWriter
{
    private static readonly string[] Suffixes =
    {
        "_Pres.pmm", "_Pres.psm", "_Pres.pss", "_Pres.pst",
        "_Sim.pmm",  "_Sim.psm",  "_Sim.pss",  "_Sim.pst",
        "_Tex.pmm",  "_Tex.psm",  "_Tex.pss",  "_Tex.pst",
    };

    public static void Write(DlcSpec map, string outputDirectory, IList<string> writtenFiles)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(writtenFiles);

        string worldLc = map.Slug.ToLowerInvariant();
        string missionName = "freeskate_" + worldLc;
        string missionDir = Path.Combine(outputDirectory, "content", "missions", missionName);
        Directory.CreateDirectory(missionDir);

        foreach (string suffix in Suffixes)
        {
            string dstPath = Path.Combine(missionDir, missionName + suffix);
            if (!MissionTemplateProvider.TryGetTemplateBytes(suffix, out byte[] templateBytes))
                throw new FileNotFoundException(
                    $"Mission stub template not found: mission_template{suffix}. " +
                    "Expected either an embedded resource or a disk fallback under lib\\mission_template\\.");
            File.WriteAllBytes(dstPath, templateBytes);
            writtenFiles.Add(dstPath);
        }
    }
}
