using ArenaBuilder.Collision.Rw;
using ArenaBuilder.Collision.Serialization;

namespace ArenaBuilder.Collision.Cluster;

/// <summary>
/// Update KD-tree leaf nodes with final byte offsets (cluster ID + unit offset). AdjustKDTreeNodeEntriesForCluster (rwcclusteredmeshbuildermethods.cpp lines 1877-1946).
/// Ported from Collision_Export_Dumbad_Tuukkas_original.py lines 2058-2160.
/// </summary>
public static class KdTreeAdjustForCluster
{
    /// <summary>Same stride as <see cref="ClusterBinarySerializer.BytesPerTriangleUnit"/> (Skate triangle unit stream).</summary>
    private const int UnitSizeBytes = ClusterBinarySerializer.BytesPerTriangleUnit;

    /// <summary>
    /// Update KD-tree leaf BuildNodes with (clusterId &lt;&lt; unitClusterIdShift) + unitByteOffset.
    /// RenderWare computes unitClusterIdShift as: (unitClusterCount &gt; 65536) ? 20 : 16.
    /// </summary>
    /// <param name="assignmentLog">Optional. When provided, appends (unitId, first) for each leaf updated (order = cluster UnitIds order). Use for debugging traversal vs assignment order.</param>
    public static int Execute(RwUnitCluster cluster, int clusterId, int unitClusterIdShift, Dictionary<int, RwBuildNode> leafMap, List<(int UnitId, uint First)>? assignmentLog = null)
    {
        int numUnits = cluster.UnitIds.Count;
        int clusterUnitBytes = numUnits * UnitSizeBytes;
        // With shift 16, offset is 16 bits (max 65535). RenderWare uses same encoding; overflow would corrupt game lookup.
        if (unitClusterIdShift == 16 && clusterUnitBytes > 65535)
            throw new InvalidOperationException($"Cluster {clusterId} unit stream length {clusterUnitBytes} exceeds 16-bit offset max (65535). Reduce cluster size or use more clusters.");
        if (unitClusterIdShift == 20 && clusterUnitBytes > 0xFFFFF)
            throw new InvalidOperationException($"Cluster {clusterId} unit stream length {clusterUnitBytes} exceeds 20-bit offset max (1048575). Reduce cluster size.");
        uint shiftedClusterId = ((uint)clusterId << unitClusterIdShift);

        // RenderWare exact method (rwcclusteredmeshbuildermethods.cpp AdjustKDTreeNodeEntriesForCluster):
        // keep a running sizeofUnitData and rewrite leaf first-entry when the mapped unit id is encountered.
        uint sizeofUnitData = 0;
        for (int unitIndex = 0; unitIndex < numUnits; unitIndex++)
        {
            int unitId = cluster.UnitIds[unitIndex];
            if (leafMap.TryGetValue(unitId, out var buildNode))
            {
                if (buildNode.Left != null)
                    throw new InvalidOperationException("BuildNode should be a leaf.");

                uint reformattedStartIndex = shiftedClusterId + sizeofUnitData;
                buildNode.MFirstEntry = reformattedStartIndex;

                if (buildNode.Parent != null)
                {
                    var parent = buildNode.Parent;
                    if (parent.Right == buildNode)
                    {
                        if (parent.Left!.MNumEntries == 0)
                            parent.Left.MFirstEntry = reformattedStartIndex;
                    }
                    else
                    {
                        if (parent.Right!.MNumEntries == 0)
                            parent.Right.MFirstEntry = reformattedStartIndex;
                    }
                }

                assignmentLog?.Add((unitId, reformattedStartIndex));
            }

            // Triangles-only stream: fixed 9-byte unit size.
            sizeofUnitData += UnitSizeBytes;
        }

        return clusterUnitBytes;
    }
}
