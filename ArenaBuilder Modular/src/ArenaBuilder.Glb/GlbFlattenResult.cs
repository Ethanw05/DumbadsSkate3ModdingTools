using System.Numerics;

namespace ArenaBuilder.Glb;

/// <summary>
/// Result of flattening a GLB mesh to unique world-space vertices and triangle indices.
/// </summary>
public sealed record GlbFlattenResult(
    IReadOnlyList<Vector3> Vertices,
    IReadOnlyList<(int V0, int V1, int V2)> Faces,
    string MaterialName);

