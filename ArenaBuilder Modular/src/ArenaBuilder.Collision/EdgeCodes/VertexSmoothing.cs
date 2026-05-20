using ArenaBuilder.Collision.Math;
using System.Numerics;

namespace ArenaBuilder.Collision.EdgeCodes;

/// <summary>
/// SmoothVertices: mark non-feature vertices with EDGE_VERTEX_DISABLE. rwcclusteredmeshbuilder.cpp lines 327-336.
/// Ported from Collision_Export_Dumbad_Tuukkas_original.py lines 2782-2902 (_all_coplanar_triangles, _vertex_is_non_feature, etc.).
/// </summary>
public static class VertexSmoothing
{
    private const float CoplanarTol = 0.01f;
    private const float CosineTol = 0.05f;
    private const float ConcaveTol = 0.15f;

    /// <summary>Apply vertex smoothing: set EDGE_VERTEX_DISABLE on edges incident to non-feature vertices. Modifies edgeCodes in place.</summary>
    public static void Apply(IReadOnlyList<Vector3> verts, IReadOnlyList<(int V0, int V1, int V2)> tris,
        Dictionary<int, List<int>> vertexTriMap, (int E0, int E1, int E2)[] edgeCodes)
    {
        foreach (var (vertexId, adjoiningTris) in vertexTriMap)
        {
            if (adjoiningTris.Count == 0) continue;
            var vertexPos = verts[vertexId];
            bool disable = AllCoplanarTriangles(adjoiningTris, tris, verts, CoplanarTol);
            if (!disable)
                disable = VertexIsNonFeature(vertexId, vertexPos, adjoiningTris, tris, verts);
            if (!disable) continue;
            foreach (int triId in adjoiningTris)
            {
                var t = tris[triId];
                int e0 = edgeCodes[triId].E0, e1 = edgeCodes[triId].E1, e2 = edgeCodes[triId].E2;
                if (t.V0 == vertexId) e0 |= EdgeCodeConstants.EdgeVertexDisable;
                else if (t.V1 == vertexId) e1 |= EdgeCodeConstants.EdgeVertexDisable;
                else if (t.V2 == vertexId) e2 |= EdgeCodeConstants.EdgeVertexDisable;
                edgeCodes[triId] = (e0, e1, e2);
            }
        }
    }

    private static bool AllCoplanarTriangles(List<int> triIds, IReadOnlyList<(int V0, int V1, int V2)> tris, IReadOnlyList<Vector3> verts, float tolerance)
    {
        if (triIds.Count == 0) return false;
        var t0 = tris[triIds[0]];
        var v0 = verts[t0.V0]; var v1 = verts[t0.V1]; var v2 = verts[t0.V2];
        var planeNormal = Vector3Extensions.Normalize(Vector3Extensions.Cross(v1 - v0, v2 - v0));
        for (int i = 1; i < triIds.Count; i++)
        {
            var ti = tris[triIds[i]];
            var n = Vector3Extensions.Normalize(Vector3Extensions.Cross(verts[ti.V1] - verts[ti.V0], verts[ti.V2] - verts[ti.V0]));
            if (!IsSimilar(Vector3.Dot(n, planeNormal), 1f, tolerance)) return false;
        }
        return true;
    }

    private static bool VertexIsNonFeature(int vertexId, Vector3 vertexPos, List<int> triIds, IReadOnlyList<(int V0, int V1, int V2)> tris, IReadOnlyList<Vector3> verts)
    {
        if (triIds.Count == 0) return false;
        var t0 = tris[triIds[0]];
        var v0 = verts[t0.V0]; var v1 = verts[t0.V1]; var v2 = verts[t0.V2];
        var planeNormal = Vector3Extensions.Normalize(Vector3Extensions.Cross(v1 - v0, v2 - v0));
        (Vector3 a, Vector3 b) = GetOppositeVertices(vertexId, t0, verts);
        var edgeA = Vector3Extensions.Normalize(vertexPos - a);
        var edgeB = Vector3Extensions.Normalize(vertexPos - b);
        for (int i = 1; i < triIds.Count; i++)
        {
            (Vector3 va, Vector3 vb) = GetOppositeVertices(vertexId, tris[triIds[i]], verts);
            var edgeC = Vector3Extensions.Normalize(vertexPos - va);
            if (EdgeDisablesVertex(ref edgeA, ref edgeB, edgeC, planeNormal)) return true;
            edgeC = Vector3Extensions.Normalize(vertexPos - vb);
            if (EdgeDisablesVertex(ref edgeA, ref edgeB, edgeC, planeNormal)) return true;
        }
        return false;
    }

    private static (Vector3, Vector3) GetOppositeVertices(int vertexId, (int V0, int V1, int V2) tri, IReadOnlyList<Vector3> verts)
    {
        if (tri.V0 == vertexId) return (verts[tri.V1], verts[tri.V2]);
        if (tri.V1 == vertexId) return (verts[tri.V2], verts[tri.V0]);
        return (verts[tri.V0], verts[tri.V1]);
    }

    private static bool EdgeDisablesVertex(ref Vector3 edgeA, ref Vector3 edgeB, Vector3 edgeC, Vector3 planeNormal)
    {
        float planeEdgeCDot = Vector3.Dot(edgeC, planeNormal);
        if (IsSimilar(planeEdgeCDot, 0f, CoplanarTol))
            return EdgeProducesFeaturelessPlane(ref edgeA, ref edgeB, edgeC, CosineTol);
        if (planeEdgeCDot < ConcaveTol)
            return true;
        return false;
    }

    private static bool EdgeProducesFeaturelessPlane(ref Vector3 edgeA, ref Vector3 edgeB, Vector3 edgeC, float cosineTolerance)
    {
        float aDotB = Vector3.Dot(edgeA, edgeB);
        var halfSpace = edgeA + edgeB;
        if (Vector3.Dot(halfSpace, -edgeC) >= 0f)
        {
            var negC = -edgeC;
            if (Vector3.Dot(negC, edgeA) >= aDotB - cosineTolerance && Vector3.Dot(negC, edgeB) >= aDotB - cosineTolerance)
                return true;
        }

        float aDotC = Vector3.Dot(edgeA, edgeC);
        float bDotC = Vector3.Dot(edgeB, edgeC);

        if (aDotC < bDotC && aDotC < aDotB)
            edgeB = edgeC;
        else if (bDotC < aDotB)
            edgeA = edgeC;

        return false;
    }

    private static bool IsSimilar(float a, float b, float epsilon) => MathF.Abs(a - b) <= epsilon;
}
