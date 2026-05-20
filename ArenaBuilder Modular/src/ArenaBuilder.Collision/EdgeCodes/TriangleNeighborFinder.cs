using ArenaBuilder.Collision.Math;
using System.Numerics;

namespace ArenaBuilder.Collision.EdgeCodes;

/// <summary>
/// Build vertex→triangle map and find triangle neighbors (mate edges). triangleneighborfinder.cpp, rwcclusteredmeshbuildermethods.cpp.
/// Ported from Collision_Export_Dumbad_Tuukkas_original.py lines 2770-2762, 2436-2458, 2588-2762.
/// </summary>
public static class TriangleNeighborFinder
{
    /// <summary><c>MateEdge</c> resets a displaced neighbor edge cosine to <c>1.0f</c> (see <c>triangleneighborfinder.cpp</c>).</summary>
    private const float DemotedPartnerEdgeCosine = 1f;

    /// <summary>Build mapping vertex index → list of triangle indices.</summary>
    public static Dictionary<int, List<int>> BuildVertexTriangleMap(IReadOnlyList<(int V0, int V1, int V2)> tris)
    {
        var map = new Dictionary<int, List<int>>();
        for (int triId = 0; triId < tris.Count; triId++)
        {
            var t = tris[triId];
            foreach (int v in new[] { t.V0, t.V1, t.V2 })
            {
                if (!map.TryGetValue(v, out var list)) { list = new List<int>(); map[v] = list; }
                list.Add(triId);
            }
        }
        return map;
    }

    /// <summary>Find neighbors and edge cosines. Fills triangleNeighbors[tri][edge] and triangleEdgeCosines[tri][edge].</summary>
    public static void FindTriangleNeighbors(
        IReadOnlyList<Vector3> verts,
        IReadOnlyList<(int V0, int V1, int V2)> tris,
        int?[][] triangleNeighbors,
        float[][] triangleEdgeCosines,
        bool[]? triangleEnabled = null)
    {
        var vertexTriMap = BuildVertexTriangleMap(tris);
        for (int tri1Id = 0; tri1Id < tris.Count; tri1Id++)
        {
            if (triangleEnabled is not null && !triangleEnabled[tri1Id])
                continue;

            var tri1 = tris[tri1Id];
            for (int edge1Idx = 0; edge1Idx < 3; edge1Idx++)
            {
                int edge1NextIdx = edge1Idx < 2 ? edge1Idx + 1 : 0;
                int edge1V0 = GetTriVertex(tri1, edge1Idx);
                if (!vertexTriMap.TryGetValue(edge1V0, out var adjoining)) continue;
                foreach (int tri2Id in adjoining)
                {
                    if ((triangleEnabled is null || triangleEnabled[tri2Id]) && tri1Id > tri2Id)
                        MateEdge(tri1Id, edge1Idx, tri2Id, verts, tris, triangleNeighbors, triangleEdgeCosines);
                }
            }
        }
    }

    private static int GetTriVertex((int V0, int V1, int V2) t, int edgeIdx) => edgeIdx switch { 0 => t.V0, 1 => t.V1, 2 => t.V2, _ => t.V0 };

    private static int FindEdgeByNeighbor(int?[] neighborList, int target)
    {
        for (int i = 0; i < 3; i++)
            if (neighborList[i] == target) return i;
        return 0;
    }

    private static void MateEdge(int tri1Id, int edge1Idx, int tri2Id, IReadOnlyList<Vector3> verts, IReadOnlyList<(int V0, int V1, int V2)> tris,
        int?[][] triangleNeighbors, float[][] triangleEdgeCosines)
    {
        int edge1NextIdx = edge1Idx < 2 ? edge1Idx + 1 : 0;
        var tri1V = tris[tri1Id];
        var tri2V = tris[tri2Id];
        var v1_p0 = verts[tri1V.V0]; var v1_p1 = verts[tri1V.V1]; var v1_p2 = verts[tri1V.V2];
        var v2_p0 = verts[tri2V.V0]; var v2_p1 = verts[tri2V.V1]; var v2_p2 = verts[tri2V.V2];
        var t1Normal = Vector3Extensions.Normalize(Vector3Extensions.Cross(v1_p1 - v1_p0, v1_p2 - v1_p0));
        var t2Normal = Vector3Extensions.Normalize(Vector3Extensions.Cross(v2_p1 - v2_p0, v2_p2 - v2_p0));
        // RW: MateEdge passes unnormalized (vert[next] - vert[edge]) — edgecosines.h text says "normalized" but
        // triangleneighborfinder.cpp uses the raw difference (same half-space test after positive scaling).
        var edgeVec = verts[GetTriVertex(tri1V, edge1NextIdx)] - verts[GetTriVertex(tri1V, edge1Idx)];
        float edgeCosine = EdgeCosines.ComputeExtendedEdgeCosine(t1Normal, t2Normal, edgeVec);

        int edge2Idx = 2, edge2NextIdx = 0;
        while (edge2NextIdx < 3)
        {
            int e1v0 = GetTriVertex(tri1V, edge1Idx);
            int e1v1 = GetTriVertex(tri1V, edge1NextIdx);
            int e2v0 = GetTriVertex(tri2V, edge2Idx);
            int e2v1 = GetTriVertex(tri2V, edge2NextIdx);
            if (e1v0 == e2v1 && e2v0 == e1v1)
            {
                bool tri1Matched = triangleNeighbors[tri1Id][edge1Idx].HasValue;
                bool tri2Matched = triangleNeighbors[tri2Id][edge2Idx].HasValue;
                if (!tri1Matched && !tri2Matched)
                {
                    triangleNeighbors[tri1Id][edge1Idx] = tri2Id;
                    triangleNeighbors[tri2Id][edge2Idx] = tri1Id;
                    triangleEdgeCosines[tri1Id][edge1Idx] = edgeCosine;
                    triangleEdgeCosines[tri2Id][edge2Idx] = edgeCosine;
                }
                else if (!tri1Matched && tri2Matched)
                {
                    float oldCos = triangleEdgeCosines[tri2Id][edge2Idx];
                    if (edgeCosine > oldCos)
                    {
                        int tri3Id = triangleNeighbors[tri2Id][edge2Idx]!.Value;
                        triangleNeighbors[tri1Id][edge1Idx] = tri2Id;
                        triangleNeighbors[tri2Id][edge2Idx] = tri1Id;
                        triangleEdgeCosines[tri1Id][edge1Idx] = edgeCosine;
                        triangleEdgeCosines[tri2Id][edge2Idx] = edgeCosine;
                        int edge3Idx = FindEdgeByNeighbor(triangleNeighbors[tri3Id], tri2Id);
                        triangleNeighbors[tri3Id][edge3Idx] = null;
                        triangleEdgeCosines[tri3Id][edge3Idx] = DemotedPartnerEdgeCosine;
                    }
                }
                else if (tri1Matched && !tri2Matched)
                {
                    float oldCos = triangleEdgeCosines[tri1Id][edge1Idx];
                    if (edgeCosine > oldCos)
                    {
                        int tri3Id = triangleNeighbors[tri1Id][edge1Idx]!.Value;
                        triangleNeighbors[tri1Id][edge1Idx] = tri2Id;
                        triangleNeighbors[tri2Id][edge2Idx] = tri1Id;
                        triangleEdgeCosines[tri1Id][edge1Idx] = edgeCosine;
                        triangleEdgeCosines[tri2Id][edge2Idx] = edgeCosine;
                        int edge3Idx = FindEdgeByNeighbor(triangleNeighbors[tri3Id], tri1Id);
                        triangleNeighbors[tri3Id][edge3Idx] = null;
                        triangleEdgeCosines[tri3Id][edge3Idx] = DemotedPartnerEdgeCosine;
                    }
                }
                else
                {
                    int tri3Id = triangleNeighbors[tri1Id][edge1Idx]!.Value;
                    int tri4Id = triangleNeighbors[tri2Id][edge2Idx]!.Value;
                    if (tri1Id != tri4Id && tri2Id != tri3Id)
                    {
                        float old1 = triangleEdgeCosines[tri1Id][edge1Idx];
                        float old2 = triangleEdgeCosines[tri2Id][edge2Idx];
                        if (edgeCosine > old1 && edgeCosine > old2)
                        {
                            triangleNeighbors[tri1Id][edge1Idx] = tri2Id;
                            triangleNeighbors[tri2Id][edge2Idx] = tri1Id;
                            triangleEdgeCosines[tri1Id][edge1Idx] = edgeCosine;
                            triangleEdgeCosines[tri2Id][edge2Idx] = edgeCosine;
                            int edge3Idx = FindEdgeByNeighbor(triangleNeighbors[tri3Id], tri1Id);
                            int edge4Idx = FindEdgeByNeighbor(triangleNeighbors[tri4Id], tri2Id);
                            triangleNeighbors[tri3Id][edge3Idx] = null;
                            triangleEdgeCosines[tri3Id][edge3Idx] = DemotedPartnerEdgeCosine;
                            triangleNeighbors[tri4Id][edge4Idx] = null;
                            triangleEdgeCosines[tri4Id][edge4Idx] = DemotedPartnerEdgeCosine;
                        }
                    }
                }
                return;
            }
            edge2Idx = edge2NextIdx;
            edge2NextIdx++;
        }
    }
}
