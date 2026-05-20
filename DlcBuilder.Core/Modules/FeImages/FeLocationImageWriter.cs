using ArenaBuilder.Core.Psg;
using ArenaBuilder.Texture;
using ArenaBuilder.Texture.Dds;

namespace DlcBuilder.Modules.FeImages;

/// Builds FE menu `.rps3` files for the per-map location thumbnail. Writes
/// 512×256 DXT5 single-mip Pegasus arenas matching stock retail DLC layout.
/// Texture name `&lt;BaseName&gt;.Texture`, VersionData revision 0x02, GUID =
/// FNV-1a over the base name (NOT Lookup8 over the vault path string — that
/// was the EBOOT's hashing strategy verified against retail).
///
/// `fe_locations.Image` attribute on each map row stores the vault path
/// `fe\source\images\locations\&lt;base&gt;` (no extension). Engine resolves the
/// `.Texture` suffix at load.
public static class FeLocationImageWriter
{
    public const int FeLocationTextureWidth = 512;
    public const int FeLocationTextureHeight = 256;

    public static void WriteFromImageFile(
        string imagePath,
        string outputRps3Path,
        string feLocationAssetBaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(feLocationAssetBaseName);

        string ext = Path.GetExtension(imagePath).ToLowerInvariant();
        byte[] inputBytes = File.ReadAllBytes(imagePath);

        DdsTextureInput ddsInput = ext switch
        {
            ".dds" => NormalizeFeLocationDds(DdsReader.Read(inputBytes)),
            ".png" => DdsReader.Read(ImageToDdsConverter.ConvertToDds(
                inputBytes,
                generateMipMaps: false,
                hasAlpha: true,
                forceOpaqueAlpha: false,
                bc1NormalMapArtifactReduction: false,
                maxDimension: null,
                targetWidth: FeLocationTextureWidth,
                targetHeight: FeLocationTextureHeight)),
            ".jpg" or ".jpeg" => DdsReader.Read(ImageToDdsConverter.ConvertToDds(
                inputBytes,
                generateMipMaps: false,
                hasAlpha: true,
                forceOpaqueAlpha: true,
                bc1NormalMapArtifactReduction: false,
                maxDimension: null,
                targetWidth: FeLocationTextureWidth,
                targetHeight: FeLocationTextureHeight)),
            _ => throw new NotSupportedException($"Unsupported image type '{ext}'. Use .png, .jpg, .jpeg, or .dds."),
        };

        ulong textureGuid = TextureGuidStrategy.FeLocationBaseNameToGuid(feLocationAssetBaseName);
        string tocName = feLocationAssetBaseName + ".Texture";
        PsgArenaSpec spec = TexturePsgComposer.Compose(
            ddsInput,
            textureGuid,
            tocName,
            TexturePsgConstants.VersionDataRevisionFeLocation);

        string? dir = Path.GetDirectoryName(Path.GetFullPath(outputRps3Path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        using FileStream fs = File.Create(outputRps3Path);
        GenericArenaWriter.Write(spec, fs);
    }

    private static DdsTextureInput NormalizeFeLocationDds(DdsTextureInput d)
    {
        if (d.Width != FeLocationTextureWidth || d.Height != FeLocationTextureHeight)
            throw new InvalidOperationException(
                $"FE .dds must be {FeLocationTextureWidth}×{FeLocationTextureHeight} (retail FE menu format). Got {d.Width}×{d.Height}.");
        if (d.MipCount != 1)
            throw new InvalidOperationException(
                $"FE .dds must have exactly 1 mip level (no mip chain). Got {d.MipCount}.");
        if (d.Ps3Format != TexturePsgConstants.FormatDxt5)
            throw new InvalidOperationException(
                "FE .dds must be DXT5 (BC3), matching stock fe_locations thumbnails.");
        return d;
    }
}
