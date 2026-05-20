using ArenaBuilder.Collision.Rw;

namespace ArenaBuilder.Collision.Cluster;

/// <summary>
/// GetVertexCode and AddUnitToCluster. UnitCluster::GetVertexCode, UnitClusterBuilder::AddUnitToCluster.
/// Ported from Collision_Export_Dumbad_Tuukkas_original.py lines 1614-1720.
/// </summary>
public static class ClusterUnitOps
{
    /// <summary>Binary search for local vertex index. Returns 0xFF (255) if not found.</summary>
    public static int GetVertexCode(RwUnitCluster cluster, int vertexIndex)
    {
        int start = 0;
        int end = cluster.VertexIds.Count - 1;
        if (end < 0) return 0xFF;
        int ret = (end - start) / 2;
        while (start <= end)
        {
            if (vertexIndex == cluster.VertexIds[ret]) return ret;
            if (vertexIndex > cluster.VertexIds[ret])
                start = ret + 1;
            else
                end = ret - 1;
            ret = start + (end - start) / 2;
        }
        return 0xFF;
    }

    /// <summary>Add a triangle (unit) to cluster. Returns false if cluster would be full. Skate 3: triangles only, use MaxVerticesPerUnit=3.</summary>
    public static bool AddUnitToCluster(RwUnitCluster cluster, int unitId, IReadOnlyList<(int V0, int V1, int V2)> tris, int maxVerticesPerUnit = ClusterConstants.MaxVerticesPerUnit)
    {
        if (cluster.VertexIds.Count > ClusterConstants.MaxVertexCount - maxVerticesPerUnit)
        {
            ClusterVertexSet.SortAndCompress(cluster);
            if (cluster.VertexIds.Count > ClusterConstants.MaxVertexCount - maxVerticesPerUnit)
                return false;
        }
        var tri = tris[unitId];
        cluster.VertexIds.Add(tri.V0);
        cluster.VertexIds.Add(tri.V1);
        cluster.VertexIds.Add(tri.V2);
        cluster.UnitIds.Add(unitId);
        return true;
    }

    /// <summary>Add ordered units to cluster. Returns number of units added. Skate 3: triangles only, use MaxVerticesPerUnit=3.</summary>
    public static int AddOrderedUnitsToUnitCluster(RwUnitCluster cluster, IReadOnlyList<int> sortedObjects, int startIndex, int numUnitsToAdd,
        IReadOnlyList<(int V0, int V1, int V2)> tris, int maxVerticesPerUnit = ClusterConstants.MaxVerticesPerUnit)
    {
        int unitIndex = 0;
        while (unitIndex < numUnitsToAdd)
        {
            int unitId = sortedObjects[startIndex + unitIndex];
            if (!AddUnitToCluster(cluster, unitId, tris, maxVerticesPerUnit))
                return unitIndex;
            unitIndex++;
        }
        ClusterVertexSet.SortAndCompress(cluster);
        return unitIndex;
    }
}
