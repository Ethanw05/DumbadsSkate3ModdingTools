using System.Text;

namespace DlcBuilder.Modules.DlcManifest.Xml;

/// `vaultlist.xml` — the boot-time manifest the engine reads to know which
/// `.vlt` files in this DLC pack to load. Every retail DLC ships one
/// (DannyWay, ArtGallery, Maloof, Creator).
///
/// Contains four entries: the main vlt + framework + challengebanks +
/// progressionbanks. A 3-entry version (without progressionbanks) caused a
/// load-time access violation in `load_thread` (CIA 0x73a848 reading 0xd14f4c58
/// while resolving `freeskate_dlc_wash`), so we always emit all four.
public static class VaultListXmlBuilder
{
    public static string Build(string packageName, string frameworkKey, bool includeOtsAnchor = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkKey);

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<VaultList>");
        sb.AppendLine($"  <VaultFile>{packageName}.vlt</VaultFile>");
        sb.AppendLine($"  <VaultFile>{frameworkKey}_local_data_framework.vlt</VaultFile>");
        sb.AppendLine($"  <VaultFile>challengebanks\\{frameworkKey}.vlt</VaultFile>");
        sb.AppendLine($"  <VaultFile>progressionbanks\\{frameworkKey}.vlt</VaultFile>");
        if (includeOtsAnchor)
            sb.AppendLine($"  <VaultFile>challenge_local_data\\{frameworkKey}_own_the_spots.vlt</VaultFile>");
        sb.AppendLine("</VaultList>");
        return sb.ToString();
    }
}
