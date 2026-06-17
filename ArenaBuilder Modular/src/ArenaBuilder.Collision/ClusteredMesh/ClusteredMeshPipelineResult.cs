using ArenaBuilder.Collision.KdTree;
using ArenaBuilder.Collision.Rw;
using System.Numerics;

namespace ArenaBuilder.Collision.ClusteredMesh;

/// <summary>
/// Result of <see cref="ClusteredMeshPipeline.BuildComplete"/>: clusters, runtime KD-tree, world AABB from the KD build root,
/// and validated triangle lists (with original indices). Binary serialization is separate (<c>ClusteredMeshBinarySerializer</c>).
/// </summary>
public sealed class ClusteredMeshPipelineResult
{
    public IReadOnlyList<RwUnitCluster> Clusters { get; init; } = null!;
    public IReadOnlyList<KdTreeNode> KdTreeNodes { get; init; } = null!;
    public Vector3 BboxMin { get; init; }
    public Vector3 BboxMax { get; init; }
    /// <summary>Validated triangle list (degenerate triangles removed).</summary>
    public IReadOnlyList<(int V0, int V1, int V2)> ValidatedTriangles { get; init; } = null!;
    /// <summary>
    /// For each entry in <see cref="ValidatedTriangles"/>, stores the original triangle index from the input list.
    /// Required to keep per-face metadata (e.g. surface IDs) aligned after degenerate filtering.
    /// </summary>
    public IReadOnlyList<int> ValidatedTriangleOriginalIndices { get; init; } = null!;
}
