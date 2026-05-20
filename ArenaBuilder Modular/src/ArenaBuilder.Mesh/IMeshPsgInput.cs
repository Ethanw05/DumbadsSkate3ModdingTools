using ArenaBuilder.Core.Platforms.PS3.Pegasus.Mesh;

namespace ArenaBuilder.Mesh;

/// <summary>
/// Per-part material info for multi-material mesh PSG. When set, part i uses entry i.
/// ChannelConfig: when present, RenderMaterialData is built with only the channels in the JSON (textures + scalars).
/// </summary>
public sealed record PerPartMaterial(
    string MaterialName,
    RenderMaterialDataBuilder.MaterialTextureOverrides? TextureOverrides,
    string? AttributorMaterialPath,
    RenderMaterialDataBuilder.BlenroseChannelConfig? ChannelConfig = null);

/// <summary>
/// Input for mesh PSG composition. Provides vertices, indices, bounds, and material info.
/// </summary>
public interface IMeshPsgInput
{
    (float X, float Y, float Z) BoundsMin { get; }
    (float X, float Y, float Z) BoundsMax { get; }
    IReadOnlyList<MeshPart> Parts { get; }
    /// <summary>Material name for single-material mode (AttribulatorMaterialName channel).</summary>
    string MaterialName { get; }
    /// <summary>Optional. When set, material channels use these GUIDs (single-material mode).</summary>
    RenderMaterialDataBuilder.MaterialTextureOverrides? TextureChannelOverrides { get; }
    /// <summary>AttributorMaterialName stream path (single-material mode). Null = "environmentsimple.default".</summary>
    string? AttributorMaterialPath => null;

    /// <summary>When set (single-material mode), only these channels are built (from BlenRose JSON).</summary>
    RenderMaterialDataBuilder.BlenroseChannelConfig? ChannelConfig => null;

    /// <summary>
    /// Optional display name for the instance (e.g. GLB filename stem).
    /// </summary>
    string? InstanceDisplayName => null;

    /// <summary>
    /// Optional namespace for instance GUID computation (e.g. "mesh_proxy" for proxy builds).
    /// When set, used in ComputeInstanceGuid so proxy and main-world meshes get different GUIDs.
    /// </summary>
    string? InstanceGuidNamespace => null;

    /// <summary>
    /// When non-null, one material per part (multi-material from multiple GLBs). Count must equal Parts.Count.
    /// Part i uses PerPartMaterials[i]. When null, single material for all (MaterialName + TextureChannelOverrides).
    /// </summary>
    IReadOnlyList<PerPartMaterial>? PerPartMaterials => null;
}

/// <summary>
/// One mesh part: vertex bytes, index bytes, material index.
/// </summary>
public sealed record MeshPart(
    byte[] VertexData,
    byte[] IndexData,
    int MaterialIndex);
