using ArenaBuilder.Collision.Rw;

namespace ArenaBuilder.Collision.Cluster;

/// <summary>
/// Sort and compress vertex set (remove duplicates). UnitCluster::SortAndCompressVertexSet (unitcluster.h lines 86-111).
/// Ported from Collision_Export_Dumbad_Tuukkas_original.py lines 1570-1612.
/// </summary>
public static class ClusterVertexSet
{
    public static void SortAndCompress(RwUnitCluster cluster)
    {
        if (cluster.VertexIds.Count == 0) return;
        cluster.VertexIds.Sort();
        int currentIndex = 0;
        int headIndex = 1;
        while (headIndex < cluster.VertexIds.Count)
        {
            if (cluster.VertexIds[headIndex] == cluster.VertexIds[currentIndex])
                headIndex++;
            else
            {
                currentIndex++;
                cluster.VertexIds[currentIndex] = cluster.VertexIds[headIndex];
                headIndex++;
            }
        }
        int newCount = currentIndex + 1;
        if (cluster.VertexIds.Count > newCount)
            cluster.VertexIds.RemoveRange(newCount, cluster.VertexIds.Count - newCount);
    }
}
