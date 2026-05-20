using ArenaBuilder.Core.Platforms.PS3.Pegasus.Mesh;
using System.Text.Json;

namespace ArenaBuilder.Texture;

/// <summary>
/// Reads BlenRose material JSON and exposes per-material texture metadata.
/// Expected shape:
/// {
///   "MaterialKey": {
///     "material_name": "MaterialName",
///     "textures": {
///       "diffuse": { "image_path": null, "image_name": "..." },
///       "normal":  { "image_path": null, "image_name": "..." }
///     }
///   }
/// }
/// Textures are resolved from the GLB; image_path is retained only for backward compatibility.
/// </summary>
internal static class BlenroseMaterialJsonReader
{
    internal sealed record ChannelTextureInfo(string? ImagePath, string? ImageName);

    internal sealed record MaterialTextureConfig(
        string MaterialName,
        string SourceJsonPath,
        IReadOnlyDictionary<string, string?> ChannelImagePaths,
        IReadOnlyDictionary<string, string?> ChannelImageNames,
        IReadOnlyDictionary<string, float> Scalars,
        IReadOnlyDictionary<string, bool> ChannelAlphaFlags);

    public static IReadOnlyDictionary<string, MaterialTextureConfig> Read(string jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
            throw new ArgumentException("JSON path is required.", nameof(jsonPath));
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException("JSON file not found.", jsonPath);

        using var fs = File.OpenRead(jsonPath);
        using var doc = JsonDocument.Parse(fs);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Materials JSON root must be an object.");

        string sourceJsonPath = Path.GetFullPath(jsonPath);
        var byName = new Dictionary<string, MaterialTextureConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var materialProp in doc.RootElement.EnumerateObject())
        {
            if (materialProp.Value.ValueKind != JsonValueKind.Object)
                continue;

            string materialName = materialProp.Name;
            if (materialProp.Value.TryGetProperty("material_name", out var materialNameEl) &&
                materialNameEl.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(materialNameEl.GetString()))
            {
                materialName = materialNameEl.GetString()!;
            }

            var channelImagePaths = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var channelImageNames = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var channelAlphaFlags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (materialProp.Value.TryGetProperty("textures", out var texturesEl) &&
                texturesEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var textureProp in texturesEl.EnumerateObject())
                {
                    string? imagePath = null;
                    string? imageName = null;
                    bool? hasAlpha = null;
                    if (textureProp.Value.ValueKind == JsonValueKind.Object)
                    {
                        if (textureProp.Value.TryGetProperty("image_path", out var imagePathEl) &&
                            imagePathEl.ValueKind == JsonValueKind.String)
                        {
                            var path = imagePathEl.GetString();
                            imagePath = string.IsNullOrWhiteSpace(path) ? null : path;
                        }
                        if (textureProp.Value.TryGetProperty("image_name", out var imageNameEl) &&
                            imageNameEl.ValueKind == JsonValueKind.String)
                        {
                            var name = imageNameEl.GetString();
                            imageName = string.IsNullOrWhiteSpace(name) ? null : name;
                        }
                        // Optional per-channel alpha hint, for example:
                        //   "diffuse": { "image_name": "...", "has_alpha": true }
                        if (textureProp.Value.TryGetProperty("has_alpha", out var hasAlphaEl) &&
                            hasAlphaEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            hasAlpha = hasAlphaEl.GetBoolean();
                        }
                    }

                    string channelKey = textureProp.Name;
                    channelImagePaths[channelKey] = imagePath;
                    channelImageNames[channelKey] = imageName;
                    if (hasAlpha.HasValue)
                        channelAlphaFlags[channelKey] = hasAlpha.Value;
                }
            }

            var scalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            if (materialProp.Value.TryGetProperty("scalars", out var scalarsEl) &&
                scalarsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var scalarProp in scalarsEl.EnumerateObject())
                {
                    if (scalarProp.Value.ValueKind == JsonValueKind.Number &&
                        scalarProp.Value.TryGetSingle(out float val))
                    {
                        scalars[scalarProp.Name] = val;
                    }
                }
            }

            // Optional top-level diffuse_has_alpha flag (exported by BlenRose in future).
            if (materialProp.Value.TryGetProperty("diffuse_has_alpha", out var diffuseAlphaEl) &&
                diffuseAlphaEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                channelAlphaFlags["diffuse"] = diffuseAlphaEl.GetBoolean();
            }

            var config = new MaterialTextureConfig(materialName, sourceJsonPath, channelImagePaths, channelImageNames, scalars, channelAlphaFlags);
            byName[materialProp.Name] = config;
            byName[materialName] = config;
        }

        return byName;
    }

    /// <summary>
    /// Maps JSON texture key to PSG shader name.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> JsonTextureToPsg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["diffuse"] = "diffuse",
        ["lightmap"] = "lightmap",
        ["specular"] = "specular",
        ["normal"] = "normal",
        ["detail"] = "detail",
        ["macro_overlay"] = "macrooverlay",
        ["environment"] = "environment",
        ["decal"] = "decal",
        ["transparent"] = "transparent",
        ["noise"] = "noise",
    };

    /// <summary>
    /// Maps JSON scalar key to PSG shader name.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> JsonScalarToPsg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["detail_normal_uv_scale"] = "detailNormalUVScale",
        ["macro_overlay_opacity"] = "macroOverlayOpacity",
        ["macro_overlay_uv_scale"] = "macroOverlayUVScale",
        ["embedded_decal"] = "embeddedDecal",
    };

    /// <summary>
    /// Canonical order for texture channels (matches real PSG layout).
    /// </summary>
    private static readonly string[] TextureChannelOrder = ["environment", "lightmap", "decal", "specular", "diffuse", "detail", "macrooverlay", "normal", "transparent", "noise"];

    /// <summary>
    /// Canonical order for scalar channels.
    /// </summary>
    private static readonly string[] ScalarChannelOrder = ["detailNormalUVScale", "macroOverlayOpacity", "macroOverlayUVScale", "embeddedDecal"];

    /// <summary>
    /// Converts MaterialTextureConfig to BlenroseChannelConfig for RenderMaterialDataBuilder.
    /// </summary>
    public static RenderMaterialDataBuilder.BlenroseChannelConfig? ToChannelConfig(MaterialTextureConfig? config)
    {
        if (config == null || (config.ChannelImagePaths.Count == 0 && config.Scalars.Count == 0))
            return null;

        var textureNames = new List<string>();
        foreach (var psgName in TextureChannelOrder)
        {
            foreach (var (jsonKey, mappedPsg) in JsonTextureToPsg)
            {
                if (string.Equals(mappedPsg, psgName, StringComparison.OrdinalIgnoreCase) &&
                    config.ChannelImagePaths.ContainsKey(jsonKey))
                {
                    textureNames.Add(psgName);
                    break;
                }
            }
        }

        var scalarValues = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var (jsonKey, psgName) in JsonScalarToPsg)
        {
            if (config.Scalars.TryGetValue(jsonKey, out float val))
                scalarValues[psgName] = val;
        }

        if (textureNames.Count == 0 && scalarValues.Count == 0)
            return null;

        return new RenderMaterialDataBuilder.BlenroseChannelConfig(textureNames, scalarValues);
    }
}
