using ArenaBuilder.Collision.Rw;

namespace ArenaBuilder.Collision.Cluster;

/// <summary>
/// Walks the KdTree and builds unit clusters (RenderWare WalkBranch).
/// </summary>
public static class KdTreeClusterWalker
{
    public static int Execute(RwBuildNode buildNode, Dictionary<int, RwBuildNode> leafMap, List<RwUnitCluster> clusterStack,
        IReadOnlyList<(int V0, int V1, int V2)> tris, IReadOnlyList<int> sortedObjects)
    {
        int vcount0 = 0, vcount1 = 0;
        if (buildNode.Left == null)
        {
            uint start = buildNode.MFirstEntry;
            uint totalNumUnitsToAdd = buildNode.MNumEntries;
            if (totalNumUnitsToAdd == 0) return 0;
            // RenderWare: register only the first unit id in the leaf.
            leafMap[sortedObjects[(int)start]] = buildNode;
            var cluster = new RwUnitCluster();
            clusterStack.Add(cluster);
            // Skate 3: triangles only (no quads). RenderWare uses maxVerticesPerUnit=4 for quads; we use 3.
            int numUnitsAdded = ClusterUnitOps.AddOrderedUnitsToUnitCluster(cluster, sortedObjects, (int)start, (int)totalNumUnitsToAdd, tris, ClusterConstants.MaxVerticesPerUnit);
            if (numUnitsAdded < (int)totalNumUnitsToAdd)
            {
                throw new InvalidOperationException(
                    "KD-tree leaf has more triangles than fit in one unit cluster after vertex-dedup (RenderWare CLUSTER_GENERATION_FAILURE_MULTI_LEAF_CLUSTER). " +
                    "Reduce leaf density or increase MAX_VERTEX_COUNT handling.");
            }
            vcount0 = cluster.VertexIds.Count;
            if (vcount0 == 0) throw new InvalidOperationException("Cluster with no vertices.");
            return vcount0;
        }
        vcount0 = Execute(buildNode.Left!, leafMap, clusterStack, tris, sortedObjects);
        vcount1 = Execute(buildNode.Right!, leafMap, clusterStack, tris, sortedObjects);
        if (vcount0 > 0 && vcount0 <= ClusterConstants.MaxVertexCount && vcount1 > 0 && vcount1 <= ClusterConstants.MaxVertexCount && clusterStack.Count >= 2)
        {
            var last = clusterStack[clusterStack.Count - 1];
            var penultimate = clusterStack[clusterStack.Count - 2];
            if (last.VertexIds.Count == vcount1 && penultimate.VertexIds.Count == vcount0 && ClusterMerger.MergeLastTwo(clusterStack))
            {
                vcount0 = clusterStack[clusterStack.Count - 1].VertexIds.Count;
                vcount1 = 0;
            }
        }
        return vcount0 + vcount1;
    }
}
