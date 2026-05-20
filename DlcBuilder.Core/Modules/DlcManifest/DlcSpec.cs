using System.IO;
using System.Text;

namespace DlcBuilder.Modules.DlcManifest;

/// Per-map derived spec: the canonical names, slugs, HAL strings, and category
/// keys that flow into the DLC manifest VLT and accompanying artifacts. Built
/// from a `MapInput.DistFolderPath` using `FromDistPath` — the heavy lifting is
/// extracting the slug from the `DIST_*` folder name and producing the
/// matching set of in-game IDs the manifest writers reference.
///
/// Public so callers can pre-compute the spec for inspection / overrides.
/// Manifest builders normally just call `FromMapInput`.
public sealed record DlcSpec(
    string DistPath,
    string DistFolderName,
    string Slug,
    string PackageName,
    string DistKey,
    string WorldHalName,
    string LocationHalName,
    string LocationDescHalName,
    string LocationHelperHalName,
    string MapCategoryKey,
    string MapFilterKey,
    string MapListingProgressionKey,
    string WorldStreamName,
    string ShortName,
    string DisplayName,
    string DescriptionText,
    float SpawnX = 0f,
    float SpawnY = 0f,
    float SpawnZ = 0f,
    float SpawnYaw = 0f,
    string? SectionLabel = null,
    string? FeImageSourcePath = null,
    string? FeLocationImageVaultPath = null,
    string SkyBoxModelPath = "world/models/DIST_skybox_downtown",
    string SkyBoxTexturePath = "world/models/DIST_skybox_downtown_Textures")
{
    /// Standard skybox model used when callers don't supply their own.
    public const string DefaultSkyBoxModelPath = "world/models/DIST_skybox_downtown";
    public const string DefaultSkyBoxTexturePath = "world/models/DIST_skybox_downtown_Textures";

    /// Build a DlcSpec straight from a public-API <see cref="Inputs.MapInput"/>.
    public static DlcSpec FromMapInput(Inputs.MapInput map, string prefix) =>
        FromDistPath(
            distPath: map.DistFolderPath,
            prefix: prefix,
            displayOverride: map.DisplayName,
            descOverride: map.DescriptionOverride,
            spawnX: map.SpawnX,
            spawnY: map.SpawnY,
            spawnZ: map.SpawnZ,
            spawnYaw: map.SpawnYaw,
            sectionLabel: map.Section,
            feImageSourcePath: map.FeImageSourcePath,
            slugSuffix: map.SlugSuffix);

    /// Build a DlcSpec from a `DIST_*` folder path. Throws on a path that
    /// doesn't begin with `DIST_` or that produces an empty slug.
    public static DlcSpec FromDistPath(
        string distPath,
        string prefix,
        string? displayOverride = null,
        string? descOverride = null,
        float? spawnX = null,
        float? spawnY = null,
        float? spawnZ = null,
        float? spawnYaw = null,
        string? sectionLabel = null,
        string? feImageSourcePath = null,
        string? slugSuffix = null,
        string? skyBoxModelPath = null,
        string? skyBoxTexturePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        string trimmedPath = distPath.Trim().Trim('"');
        string folderName = Path.GetFileName(trimmedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName))
            throw new InvalidOperationException("Could not extract DIST folder name.");
        if (!folderName.StartsWith("DIST_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("DIST folder name must begin with DIST_ (e.g. DIST_MilkMan10).");

        string distSuffix = folderName["DIST_".Length..];
        if (string.IsNullOrWhiteSpace(distSuffix))
            throw new InvalidOperationException("DIST name after DIST_ is empty.");

        string baseSlug = ToSlug(distSuffix);
        if (string.IsNullOrWhiteSpace(baseSlug))
            throw new InvalidOperationException("DIST name did not produce a valid slug.");

        string suffixSlug = string.IsNullOrWhiteSpace(slugSuffix) ? "" : ToSlug(slugSuffix);
        string slug = string.IsNullOrWhiteSpace(suffixSlug) ? baseSlug : baseSlug + "_" + suffixSlug;

        string prefixSlug = ToSlug(prefix);
        if (string.IsNullOrWhiteSpace(prefixSlug))
            throw new InvalidOperationException("Package prefix must include at least one letter or digit.");

        string slugUpper = slug.ToUpperInvariant();
        string displayName = displayOverride ?? distSuffix;
        string description = descOverride ?? displayName + " freeskate location.";
        string? feImg = string.IsNullOrWhiteSpace(feImageSourcePath) ? null : feImageSourcePath.Trim().Trim('"');
        string feLocBase = FeLocationNaming.FeLocationAssetBaseName(slug);
        string? feVaultPath = feImg == null ? null : $"fe\\source\\images\\locations\\{feLocBase}";
        string skyModel = string.IsNullOrWhiteSpace(skyBoxModelPath) ? DefaultSkyBoxModelPath : skyBoxModelPath.Trim().Trim('"');
        string skyTexture = string.IsNullOrWhiteSpace(skyBoxTexturePath) ? DefaultSkyBoxTexturePath : skyBoxTexturePath.Trim().Trim('"');

        return new DlcSpec(
            DistPath: trimmedPath,
            DistFolderName: folderName,
            Slug: slug,
            PackageName: $"{prefixSlug}_{slug}_minimal",
            DistKey: $"dist_{slug}dlc",
            WorldHalName: "ID_WORLD_" + slugUpper,
            LocationHalName: "ID_LOCATION_" + slugUpper,
            LocationDescHalName: "ID_MAP_LOCATION_DESC_" + slugUpper,
            LocationHelperHalName: "ID_MAP_HELPER_LOCATION_" + slugUpper,
            MapCategoryKey: slug + "dlc",
            MapFilterKey: slug + "dlc",
            MapListingProgressionKey: "progression_locations_dlc_" + slug,
            WorldStreamName: folderName,
            ShortName: slug.Length >= 4 ? slug[..4] : slug,
            DisplayName: displayName,
            DescriptionText: description,
            SpawnX: spawnX ?? 0f,
            SpawnY: spawnY ?? 0f,
            SpawnZ: spawnZ ?? 0f,
            SpawnYaw: spawnYaw ?? 0f,
            SectionLabel: string.IsNullOrWhiteSpace(sectionLabel) ? null : sectionLabel.Trim(),
            FeImageSourcePath: feImg,
            FeLocationImageVaultPath: feVaultPath,
            SkyBoxModelPath: skyModel,
            SkyBoxTexturePath: skyTexture);
    }

    /// Lower-case-letters/digits + underscores; collapse runs of underscores;
    /// trim leading/trailing. Used to derive every slug-shaped identifier in
    /// the DLC manifest.
    public static string ToSlug(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (c == ' ' || c == '-' || c == '_') sb.Append('_');
        }
        string s = sb.ToString();
        while (s.Contains("__", StringComparison.Ordinal))
            s = s.Replace("__", "_", StringComparison.Ordinal);
        return s.Trim('_');
    }
}
