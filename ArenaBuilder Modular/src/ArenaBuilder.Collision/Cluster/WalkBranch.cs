using ArenaBuilder.Collision.Rw;

namespace ArenaBuilder.Collision.Cluster;

/// <summary>
/// Walks the KdTree and builds unit clusters (RenderWare WalkBranch).
/// </summary>
public static class KdTreeClusterWalker
{
    public static int Execute(RwBuildNode buildNode, Dictionary<int, RwBuildNode> leafMap, List<RwUnitCluster> clusterStack,
        IReadOnlyList<(int V0, int V1, int V2)> tris, IReadOnlyList<int> sortedObjects, int depth = 1)
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
                var leafUniqueVerts = new HashSet<int>();
                for (int i = 0; i < (int)totalNumUnitsToAdd; i++)
                {
                    var t = tris[sortedObjects[(int)start + i]];
                    leafUniqueVerts.Add(t.V0);
                    leafUniqueVerts.Add(t.V1);
                    leafUniqueVerts.Add(t.V2);
                }
                var bb = buildNode.Bbox;
                var ext = bb.Max - bb.Min;
                throw new InvalidOperationException(
                    "KD-tree leaf has more triangles than fit in one unit cluster after vertex-dedup " +
                    "(RenderWare CLUSTER_GENERATION_FAILURE_MULTI_LEAF_CLUSTER). " +
                    $"Leaf depth={depth}, tris={totalNumUnitsToAdd} (fit {numUnitsAdded}), " +
                    $"leaf unique verts={leafUniqueVerts.Count}, cluster verts at overflow={cluster.VertexIds.Count} (cap {ClusterConstants.MaxVertexCount}). " +
                    $"Bbox min=({bb.Min.X:0.###},{bb.Min.Y:0.###},{bb.Min.Z:0.###}) " +
                    $"max=({bb.Max.X:0.###},{bb.Max.Y:0.###},{bb.Max.Z:0.###}) " +
                    $"extent=({ext.X:0.###},{ext.Y:0.###},{ext.Z:0.###}). " +
                    "Either lower KdTreeConstants.KdtreeMaxEntriesPerNode further, or remove the dense/stacked geometry in that bbox.");
            }
            vcount0 = cluster.VertexIds.Count;
            if (vcount0 == 0) throw new InvalidOperationException("Cluster with no vertices.");
            return vcount0;
        }
        vcount0 = Execute(buildNode.Left!, leafMap, clusterStack, tris, sortedObjects, depth + 1);
        vcount1 = Execute(buildNode.Right!, leafMap, clusterStack, tris, sortedObjects, depth + 1);
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
