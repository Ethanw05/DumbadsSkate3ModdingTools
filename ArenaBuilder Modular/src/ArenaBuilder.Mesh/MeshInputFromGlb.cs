using ArenaBuilder.Core.Platforms.PS3.Pegasus.Mesh;
using ArenaBuilder.Glb;

namespace ArenaBuilder.Mesh;

/// <summary>
/// Builds IMeshPsgInput from GLB via MeshVertexFlattener.
/// Uses first mesh, first primitive only (backup-compatible).
/// </summary>
public sealed class MeshInputFromGlb : IMeshPsgInput
{
    public (float X, float Y, float Z) BoundsMin { get; }
    public (float X, float Y, float Z) BoundsMax { get; }
    public IReadOnlyList<MeshPart> Parts { get; }
    public string MaterialName { get; }
    public RenderMaterialDataBuilder.MaterialTextureOverrides? TextureChannelOverrides { get; set; }
    public string? AttributorMaterialPath { get; set; }
    /// <inheritdoc />
    public string? InstanceDisplayName { get; set; }

    public MeshInputFromGlb(string glbPath, float scale = 1f, bool reverseWinding = false)
    {
        var result = MeshVertexFlattener.Flatten(glbPath);
        InstanceDisplayName = Path.GetFileNameWithoutExtension(glbPath);
        MaterialName = result.MaterialName;
        BoundsMin = (result.Bounds.Min.X * scale, result.Bounds.Min.Y * scale, result.Bounds.Min.Z * scale);
        BoundsMax = (result.Bounds.Max.X * scale, result.Bounds.Max.Y * scale, result.Bounds.Max.Z * scale);

        var vertexData = MeshVertexPacker.PackVertices(
            result.Positions,
            result.Normals,
            result.Uvs,
            result.Indices,
            scale,
            result.Uvs1);
        var indexData = MeshIndexPacker.PackIndices(result.Indices, reverseWinding);

        Parts = new[] { new MeshPart(vertexData, indexData, 0) };
    }
}
