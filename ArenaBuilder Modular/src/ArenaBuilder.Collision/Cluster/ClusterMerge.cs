using ArenaBuilder.Collision.Rw;

namespace ArenaBuilder.Collision.Cluster;

/// <summary>
/// Merges last two clusters on the stack when combined vertex count fits. MergeLastTwoClusters (RenderWare).
/// </summary>
public static class ClusterMerger
{
    public static bool MergeLastTwo(List<RwUnitCluster> clusterStack)
    {
        if (clusterStack.Count < 2) return false;
        var last = clusterStack[clusterStack.Count - 1];
        var penultimate = clusterStack[clusterStack.Count - 2];
        var mergedVertices = new List<int>();
        int penultimateCounter = 0, lastCounter = 0;
        while (penultimateCounter < penultimate.VertexIds.Count && lastCounter < last.VertexIds.Count && mergedVertices.Count < ClusterConstants.MaxVertexCount)
        {
            if (penultimate.VertexIds[penultimateCounter] == last.VertexIds[lastCounter])
            {
                mergedVertices.Add(penultimate.VertexIds[penultimateCounter]);
                penultimateCounter++;
                lastCounter++;
            }
            else if (penultimate.VertexIds[penultimateCounter] < last.VertexIds[lastCounter])
            {
                mergedVertices.Add(penultimate.VertexIds[penultimateCounter]);
                penultimateCounter++;
            }
            else
            {
                mergedVertices.Add(last.VertexIds[lastCounter]);
                lastCounter++;
            }
        }
        while (penultimateCounter < penultimate.VertexIds.Count && mergedVertices.Count < ClusterConstants.MaxVertexCount)
        {
            mergedVertices.Add(penultimate.VertexIds[penultimateCounter]);
            penultimateCounter++;
        }
        while (lastCounter < last.VertexIds.Count && mergedVertices.Count < ClusterConstants.MaxVertexCount)
        {
            mergedVertices.Add(last.VertexIds[lastCounter]);
            lastCounter++;
        }
        if (penultimateCounter == penultimate.VertexIds.Count && lastCounter == last.VertexIds.Count && mergedVertices.Count <= ClusterConstants.MaxVertexCount)
        {
            penultimate.VertexIds.Clear();
            penultimate.VertexIds.AddRange(mergedVertices);
            penultimate.UnitIds.AddRange(last.UnitIds);
            clusterStack.RemoveAt(clusterStack.Count - 1);
            return true;
        }
        return false;
    }
}
