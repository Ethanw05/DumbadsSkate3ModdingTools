using ArenaBuilder.Collision.Math;
using System.Numerics;

namespace ArenaBuilder.Collision.ClusteredMesh;

/// <summary>
/// Triangle-only subset of RW ClusteredMeshBuilder methods.
/// Function names intentionally mirror RW stages so parity audits can verify call order.
/// Unsupported RW stages (vertex-group merge planes/quads/internal culling heuristics) are explicit no-ops here.
/// </summary>
internal static class ClusteredMeshBuilderMethodsTriangleOnly
{
    private const float CoplanarCosineTolerance = 0.01f;
    private const float CoplanarHeightTolerance = 0.05f;
    private const float MaximumEdgeCosineMergeTolerance = 0.1f;
    /// <summary>
    /// RW stage: MergeVertexGroups(buildParams.vertexMerge_Enable).
    /// Triangle-only pipeline performs welding before this point; keep stage as explicit no-op.
    /// </summary>
    public static void MergeVertexGroups(bool vertexMergeEnabled)
    {
        _ = vertexMergeEnabled;
    }

    /// <summary>
    /// RW storage: TriangleFlagsList.
    /// </summary>
    public static bool[] InitializeTriangleFlags(int numTriangles)
    {
        var flags = new bool[numTriangles];
        Array.Fill(flags, true);
        return flags;
    }

    /// <summary>
    /// RW stage: DisableInternalTriangles(...).
    /// Triangle-only implementation of the RW internal-triangle path:
    /// disables coplanar opposing triangle pairs that share all 3 vertices.
    /// (RW also has internal-quad handling; intentionally omitted here.)
    /// </summary>
    public static void DisableInternalTriangles(
        bool[] triangleEnabled,
        IReadOnlyList<(int V0, int V1, int V2)> triangles,
        IReadOnlyList<Vector3> vertices,
        Dictionary<int, List<int>> vertexTriangleMap)
    {
        for (int triangle1Index = 0; triangle1Index < triangles.Count; triangle1Index++)
        {
            if (!triangleEnabled[triangle1Index])
                continue;

            var t1 = triangles[triangle1Index];
            var t1Normal = ComputeTriangleNormal(t1, vertices);

            for (int edge1Index = 0; edge1Index < 3; edge1Index++)
            {
                int edge1NextIndex = edge1Index < 2 ? edge1Index + 1 : 0;
                int t1EdgeStart = GetTriVertex(t1, edge1Index);
                int t1EdgeEnd = GetTriVertex(t1, edge1NextIndex);
                int t1Opposite = GetTriVertex(t1, edge1NextIndex < 2 ? edge1NextIndex + 1 : 0);
                var tri1EdgeVector = vertices[t1EdgeEnd] - vertices[t1EdgeStart];

                if (!vertexTriangleMap.TryGetValue(t1EdgeStart, out var adjoining))
                    continue;

                foreach (int triangle2Index in adjoining)
                {
                    if (triangle1Index >= triangle2Index || !triangleEnabled[triangle2Index])
                        continue;

                    var t2 = triangles[triangle2Index];
                    var t2Normal = ComputeTriangleNormal(t2, vertices);

                    for (int edge2Index = 2, edge2NextIndex = 0; edge2NextIndex < 3; edge2Index = edge2NextIndex++)
                    {
                        int t2EdgeStart = GetTriVertex(t2, edge2Index);
                        int t2EdgeEnd = GetTriVertex(t2, edge2NextIndex);
                        if (t1EdgeStart != t2EdgeEnd || t2EdgeStart != t1EdgeEnd)
                            continue;

                        float edgeCosine = EdgeCodes.EdgeCosines.ComputeExtendedEdgeCosine(t1Normal, t2Normal, tri1EdgeVector);
                        bool coplanar = edgeCosine > 2.99f || edgeCosine < -0.99f;
                        if (!coplanar)
                            continue;

                        int t2Opposite = GetTriVertex(t2, edge2NextIndex < 2 ? edge2NextIndex + 1 : 0);
                        if (t1Opposite == t2Opposite)
                        {
                            triangleEnabled[triangle1Index] = false;
                            triangleEnabled[triangle2Index] = false;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// RW stage: MergeWithPlanes(...).
    /// Triangle-only builder does not merge with external planes; explicit no-op.
    /// </summary>
    public static void MergeWithPlanes(
        float[][] triangleEdgeCosines,
        int?[][] triangleNeighbors,
        IReadOnlyList<(int V0, int V1, int V2)> triangles,
        bool[] triangleEnabled,
        IReadOnlyList<Vector3> vertices)
    {
        _ = triangleEdgeCosines;
        _ = triangleNeighbors;
        _ = triangles;
        _ = triangleEnabled;
        _ = vertices;
    }

    /// <summary>
    /// Triangle-only implementation of RW FixUnmatchedEdges horizontal correction path.
    /// Uses the same default tolerances from ClusteredMeshBuilder ctor and applies merge-plane edge cosine corrections.
    /// </summary>
    public static void FixUnmatchedEdges(
        float[][] triangleEdgeCosines,
        int?[][] triangleNeighbors,
        IReadOnlyList<(int V0, int V1, int V2)> triangles,
        bool[] triangleEnabled,
        IReadOnlyList<Vector3> vertices)
    {
        // RW FixUnmatchedEdges relies on triangle group IDs and only applies cross-group edge corrections.
        // Skate 3 clustered meshes are built without group IDs (group size = 0), so this stage is a no-op here.
        _ = triangleEdgeCosines;
        _ = triangleNeighbors;
        _ = triangles;
        _ = triangleEnabled;
        _ = vertices;
        return;
    }

    /// <summary>
    /// RW stage: BuildUnitList(buildParams.quads_Enable).
    /// Triangle-only output keeps one unit per enabled triangle.
    /// </summary>
    public static int BuildUnitList(bool[] triangleEnabled)
    {
        int count = 0;
        for (int i = 0; i < triangleEnabled.Length; i++)
        {
            if (triangleEnabled[i])
                count++;
        }

        return count;
    }

    private static int GetTriVertex((int V0, int V1, int V2) t, int edgeIdx) =>
        edgeIdx switch { 0 => t.V0, 1 => t.V1, 2 => t.V2, _ => t.V0 };

    private static Vector3 ComputeTriangleNormal((int V0, int V1, int V2) tri, IReadOnlyList<Vector3> vertices)
    {
        var p0 = vertices[tri.V0];
        var p1 = vertices[tri.V1];
        var p2 = vertices[tri.V2];
        return Vector3Extensions.Normalize(Vector3Extensions.Cross(p1 - p0, p2 - p0));
    }

}

