using System.Text;
using DlcBuilder.Modules.DlcManifest;
using DlcBuilder.Modules.OtsPsg;
using DlcBuilder.Modules.Race;

namespace DlcBuilder.Modules.Unlocks;

/// Writes the two `data/unlocks/*.unlock` entitlement files. Engine-side
/// `DlcSlot_IsInstalledOrEntitled_GATE` checks against the DLC_PRODUCT
/// registry when filtering content for the online lobby. Without these
/// files the DLC may load offline but its entries are filtered out of the
/// online menu — and `CHALLENGES,&lt;key&gt;` lines must list every authored
/// challenge (OTS / Race / etc.) or the engine surfaces the runtime error
/// "you don't have the current DLC installed to play it" when the user
/// tries to launch a challenge that's listed in the FE but missing from
/// this file.
///
/// Both files are ASCII with LF line endings (no BOM) — matches Art Gallery
/// `play.unlock` / `play0000_product00000000.unlock` byte layout exactly.
/// Verified by diffing AG's shipped files.
public static class UnlockFilesWriter
{
    public static void Write(
        string packageSlug,
        IReadOnlyList<DlcSpec> mapSpecs,
        IReadOnlyList<OtsChallengeSpec> otsChallenges,
        IReadOnlyList<RaceChallengeSpec> raceChallenges,
        string stagingDataDir,
        IList<string> writtenFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageSlug);
        ArgumentNullException.ThrowIfNull(mapSpecs);
        ArgumentNullException.ThrowIfNull(otsChallenges);
        ArgumentNullException.ThrowIfNull(raceChallenges);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDataDir);
        ArgumentNullException.ThrowIfNull(writtenFiles);

        string unlocksDir = Path.Combine(stagingDataDir, "unlocks");
        Directory.CreateDirectory(unlocksDir);

        string slugLc = packageSlug.ToLowerInvariant();

        // Main unlock — one line per registered content key.
        //   WORLDS,<world_slug>                              per map
        //   CHALLENGES,<challenge_key>                       per OTS / Race
        //   SKATEPARK_SET,skatepark_unlock_<challenge_key>   per OTS (only)
        //
        // Race entries DON'T get a SKATEPARK_SET — that's an OTS-specific
        // unlock that hooks into the create-a-spot skatepark system. Race
        // gates aren't skatepark objects, so emitting one would either be
        // ignored or (worse) bind to a nonexistent skatepark set hash.
        var lines = new List<string>(
            mapSpecs.Count + otsChallenges.Count * 2 + raceChallenges.Count);
        foreach (DlcSpec map in mapSpecs)
            lines.Add("WORLDS," + map.Slug.ToLowerInvariant());
        foreach (OtsChallengeSpec ots in otsChallenges)
        {
            lines.Add("CHALLENGES," + ots.ChallengeKey);
            lines.Add("SKATEPARK_SET,skatepark_unlock_" + ots.ChallengeKey);
        }
        foreach (RaceChallengeSpec race in raceChallenges)
        {
            lines.Add("CHALLENGES," + race.ChallengeKey);
        }

        string mainUnlockPath = Path.Combine(unlocksDir, slugLc + ".unlock");
        File.WriteAllText(mainUnlockPath, string.Join("\n", lines) + "\n", Encoding.ASCII);
        writtenFiles.Add(mainUnlockPath);

        // Product unlock — registers DLC_PRODUCT in the entitlement registry.
        // The 16-char product id MUST match dlc_mapping.product_id and the
        // DLC's install folder name. Engine cross-references all three.
        string productSlugUpper = packageSlug.ToUpperInvariant();
        string productId = (productSlugUpper + "0000000000000000").Substring(0, 16);
        string productUnlockPath = Path.Combine(unlocksDir, slugLc + "0000_product00000000.unlock");
        File.WriteAllText(productUnlockPath, "DLC_PRODUCT," + productId + "\n", Encoding.ASCII);
        writtenFiles.Add(productUnlockPath);
    }
}
