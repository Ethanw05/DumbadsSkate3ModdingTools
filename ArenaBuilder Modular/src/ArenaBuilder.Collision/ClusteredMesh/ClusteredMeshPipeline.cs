using ArenaBuilder.Collision.Cluster;
using ArenaBuilder.Collision.EdgeCodes;
using ArenaBuilder.Collision.KdTree;
using ArenaBuilder.Collision.Rw;
using ArenaBuilder.Collision.Serialization;
using ArenaBuilder.Collision.Validation;
using System.Numerics;

namespace ArenaBuilder.Collision.ClusteredMesh;

/// <summary>
/// Clustered mesh <em>geometry</em> pipeline: validation, adjacency, edge codes, optional vertex smoothing,
/// KD-tree, cluster walk, and runtime KD-tree. Aligns with the triangle path in
/// <c>ClusteredMeshBuilder::BuildClusteredMesh</c> after <c>ValidateTriangles</c> through cluster generation,
/// not the full EA builder (vertex merge, internal-triangle removal, merge-with-planes, quads, <c>BuildUnitList</c>, etc.).
/// Cluster binary layout and compression use <see cref="Serialization.ClusteredMeshBinarySerializer"/> with a caller-supplied granularity.
/// </summary>
public static class ClusteredMeshPipeline
{
    /// <summary>
    /// Build clustered mesh: validate → neighbors → edge codes → (optional) smooth → KD-tree → walk → fill clusters → adjust → runtime KD-tree.
    /// </summary>
    /// <param name="verts">Vertex list.</param>
    /// <param name="tris">Triangle list (will be validated; degenerate removed).</param>
    /// <param name="enableVertexSmoothing">If true, run SmoothVertices (EDGE_VERTEX_DISABLE on non-feature vertices).</param>
    public static ClusteredMeshPipelineResult BuildComplete(
        IReadOnlyList<Vector3> verts,
        IReadOnlyList<(int V0, int V1, int V2)> tris,
        bool enableVertexSmoothing = false)
    {
        // RW stage parity scaffold (triangle-only): MergeVertexGroups.
        ClusteredMeshBuilderMethodsTriangleOnly.MergeVertexGroups(vertexMergeEnabled: false);

        // STEP 0: Validate and filter degenerate triangles (RenderWare lines 235-245)
        var validatedWithOrig = TriangleValidation.ValidateTrianglesWithOriginalIndices(verts, tris);
        var validatedTris = validatedWithOrig.Select(t => (t.V0, t.V1, t.V2)).ToList();
        var validatedOrigIndices = validatedWithOrig.Select(t => t.OriginalIndex).ToList();
        int numTris = validatedTris.Count;

        if (numTris == 0)
        {
            return new ClusteredMeshPipelineResult
            {
                Clusters = Array.Empty<RwUnitCluster>(),
                KdTreeNodes = Array.Empty<KdTreeNode>(),
                BboxMin = Vector3.Zero,
                BboxMax = Vector3.Zero,
                ValidatedTriangles = validatedTris,
                ValidatedTriangleOriginalIndices = validatedOrigIndices
            };
        }

        // RW storage parity: TriangleFlagsList.
        var triangleEnabled = ClusteredMeshBuilderMethodsTriangleOnly.InitializeTriangleFlags(numTris);

        // STEP 1: Find triangle neighbors (RenderWare lines 282-288)
        var triangleNeighbors = new int?[numTris][];
        var triangleEdgeCosines = new float[numTris][];
        // RenderWare: TriangleNeighborFinder::InitializeTriangleEdgeCosines — CLUSTEREDMESHBUILDER_EDGECOS_OF_UNMATCHED_EDGE (-1f).
        // (Rematch demotion in MateEdge resets discarded edges to 1f; initial state is -1f.)
        const float unmatchedEdgeCos = -1f;
        for (int i = 0; i < numTris; i++)
        {
            triangleNeighbors[i] = new int?[3];
            triangleEdgeCosines[i] = new[] { unmatchedEdgeCos, unmatchedEdgeCos, unmatchedEdgeCos };
        }
        var vertexTriMap = TriangleNeighborFinder.BuildVertexTriangleMap(validatedTris);
        ClusteredMeshBuilderMethodsTriangleOnly.DisableInternalTriangles(
            triangleEnabled,
            validatedTris,
            verts,
            vertexTriMap);

        TriangleNeighborFinder.FindTriangleNeighbors(verts, validatedTris, triangleNeighbors, triangleEdgeCosines, triangleEnabled);
        ClusteredMeshBuilderMethodsTriangleOnly.MergeWithPlanes(
            triangleEdgeCosines,
            triangleNeighbors,
            validatedTris,
            triangleEnabled,
            verts);
        ClusteredMeshBuilderMethodsTriangleOnly.FixUnmatchedEdges(
            triangleEdgeCosines,
            triangleNeighbors,
            validatedTris,
            triangleEnabled,
            verts);

        // Compact to enabled triangles so downstream unit/KD indices match RW flag-driven filtering stages.
        var oldToNew = new int[numTris];
        Array.Fill(oldToNew, -1);
        var filteredTris = new List<(int V0, int V1, int V2)>(numTris);
        var filteredOrigIndices = new List<int>(numTris);
        for (int i = 0; i < numTris; i++)
        {
            if (!triangleEnabled[i])
                continue;
            oldToNew[i] = filteredTris.Count;
            filteredTris.Add(validatedTris[i]);
            filteredOrigIndices.Add(validatedOrigIndices[i]);
        }

        if (filteredTris.Count == 0)
        {
            return new ClusteredMeshPipelineResult
            {
                Clusters = Array.Empty<RwUnitCluster>(),
                KdTreeNodes = Array.Empty<KdTreeNode>(),
                BboxMin = Vector3.Zero,
                BboxMax = Vector3.Zero,
                ValidatedTriangles = filteredTris,
                ValidatedTriangleOriginalIndices = filteredOrigIndices
            };
        }

        var filteredNeighbors = new int?[filteredTris.Count][];
        var filteredEdgeCosines = new float[filteredTris.Count][];
        for (int oldTri = 0; oldTri < numTris; oldTri++)
        {
            int newTri = oldToNew[oldTri];
            if (newTri < 0)
                continue;

            filteredNeighbors[newTri] = new int?[3];
            filteredEdgeCosines[newTri] = new float[3];
            for (int edge = 0; edge < 3; edge++)
            {
                filteredEdgeCosines[newTri][edge] = triangleEdgeCosines[oldTri][edge];
                int? oldN = triangleNeighbors[oldTri][edge];
                filteredNeighbors[newTri][edge] = oldN.HasValue ? (oldToNew[oldN.Value] >= 0 ? oldToNew[oldN.Value] : null) : null;
            }
        }

        validatedTris = filteredTris;
        validatedOrigIndices = filteredOrigIndices;
        triangleNeighbors = filteredNeighbors;
        triangleEdgeCosines = filteredEdgeCosines;
        numTris = validatedTris.Count;
        vertexTriMap = TriangleNeighborFinder.BuildVertexTriangleMap(validatedTris);

        // STEP 2: Generate triangle edge codes (RenderWare lines 319-323)
        var edgeCodesGlobal = EdgeCodeGenerator.GenerateEdgeCodes(numTris, triangleNeighbors, triangleEdgeCosines);
        // Python bounds check per tri: if vert indices out of range use fallback. We use validated tris so indices are valid.

        // STEP 3: Smooth non-feature vertices (RenderWare lines 327-336) — modifies edgeCodesGlobal in place
        if (enableVertexSmoothing)
            VertexSmoothing.Apply(verts, validatedTris, vertexTriMap, edgeCodesGlobal);

        // RW stage parity: BuildUnitList(quads disabled in this pipeline).
        int numUnits = ClusteredMeshBuilderMethodsTriangleOnly.BuildUnitList(triangleEnabled);
        if (numUnits == 0)
            throw new InvalidOperationException("No enabled triangle units remain after triangle-only RW stages.");

        // STEP 4: Build KD-tree (RenderWare lines 369-376) — produces sortedEntryIndices (MASTER ORDERING)
        var (rootBuildNode, sortedEntryIndices) = KdTreeBuilder.BuildKdTree(verts, validatedTris);
        if (rootBuildNode == null)
        {
            return new ClusteredMeshPipelineResult
            {
                Clusters = Array.Empty<RwUnitCluster>(),
                KdTreeNodes = Array.Empty<KdTreeNode>(),
                BboxMin = Vector3.Zero,
                BboxMax = Vector3.Zero,
                ValidatedTriangles = validatedTris,
                ValidatedTriangleOriginalIndices = validatedOrigIndices
            };
        }

        // STEP 5: WalkBranch — create clusters (RenderWare lines 944-952). Use walk+merge only for game-compatible output.
        var leafMap = new Dictionary<int, RwBuildNode>();
        var clusterStack = new List<RwUnitCluster>();
        KdTreeClusterWalker.Execute(rootBuildNode, leafMap, clusterStack, validatedTris, sortedEntryIndices);

        // Fill vertex positions into each cluster (Python lines 2554-2558)
        foreach (var cluster in clusterStack)
        {
            cluster.Vertices.Clear();
            foreach (int vertId in cluster.VertexIds)
                cluster.Vertices.Add(verts[vertId]);
        }

        // Copy edge codes from global to each cluster (Python lines 2560-2568)
        // Missing edge codes indicate a broken pipeline; fail fast instead of emitting fallback bytes.
        foreach (var cluster in clusterStack)
        {
            cluster.EdgeCodes.Clear();
            foreach (int unitId in cluster.UnitIds)
            {
                if (unitId < 0 || unitId >= edgeCodesGlobal.Length)
                {
                    throw new InvalidOperationException(
                        $"Unit {unitId} is outside edge-code range [0, {edgeCodesGlobal.Length - 1}].");
                }

                cluster.EdgeCodes[unitId] = edgeCodesGlobal[unitId];
            }
        }

        // STEP 6: Adjust KD-tree leaf offsets (RenderWare lines 968-976)
        int unitClusterIdShift = ClusteredMeshBinarySerializer.ComputeUnitClusterIdShiftFromMaxClusters(clusterStack.Count);
        for (int clusterId = 0; clusterId < clusterStack.Count; clusterId++)
        {
            clusterStack[clusterId].ClusterId = (uint)clusterId;
            KdTreeAdjustForCluster.Execute(clusterStack[clusterId], clusterId, unitClusterIdShift, leafMap, null);
        }
        ValidateLeafEntryUnitRanges(rootBuildNode, clusterStack, unitClusterIdShift, leafMap);

        // STEP 7: Initialize runtime KD-tree (RenderWare line 486)
        var rtKdTreeNodes = KdTreeRuntime.InitializeRuntimeKdTree(rootBuildNode);
        // Match RW: require leaf traversal order monotonicity.
        KdTreeRuntime.Validate(rtKdTreeNodes, (uint)numUnits, rootBuildNode.Bbox, skipLeafMonotonicityCheck: false);

        var bboxMin = rootBuildNode.Bbox.Min;
        var bboxMax = rootBuildNode.Bbox.Max;

        return new ClusteredMeshPipelineResult
        {
            Clusters = clusterStack,
            KdTreeNodes = rtKdTreeNodes,
            BboxMin = bboxMin,
            BboxMax = bboxMax,
            ValidatedTriangles = validatedTris,
            ValidatedTriangleOriginalIndices = validatedOrigIndices
        };
    }

    private static void ValidateLeafEntryUnitRanges(
        RwBuildNode rootBuildNode,
        IReadOnlyList<RwUnitCluster> clusters,
        int unitClusterIdShift,
        IReadOnlyDictionary<int, RwBuildNode> leafMap)
    {
        if (clusters.Count == 0)
            return;

        uint offsetMask = unitClusterIdShift == 20 ? 0xFFFFFu : 0xFFFFu;
        var stack = new Stack<RwBuildNode>();
        stack.Push(rootBuildNode);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Left != null)
            {
                stack.Push(node.Left);
                stack.Push(node.Right!);
                continue;
            }

            if (node.MNumEntries == 0)
                continue;

            int clusterId = (int)(node.MFirstEntry >> unitClusterIdShift);
            int unitByteOffset = (int)(node.MFirstEntry & offsetMask);
            if ((uint)clusterId >= (uint)clusters.Count)
            {
                throw new InvalidOperationException(
                    $"KD leaf cluster id out of range: {clusterId} (clusters={clusters.Count}, first={node.MFirstEntry}).");
            }

            if ((unitByteOffset % ClusterBinarySerializer.BytesPerTriangleUnit) != 0)
            {
                throw new InvalidOperationException(
                    $"KD leaf unit offset is not aligned to unit size {ClusterBinarySerializer.BytesPerTriangleUnit}: offset={unitByteOffset}, first={node.MFirstEntry}.");
            }

            int clusterUnitDataBytes = clusters[clusterId].UnitIds.Count * ClusterBinarySerializer.BytesPerTriangleUnit;
            int leafUnitBytes = checked((int)node.MNumEntries * ClusterBinarySerializer.BytesPerTriangleUnit);
            if (unitByteOffset + leafUnitBytes > clusterUnitDataBytes)
            {
                throw new InvalidOperationException(
                    $"KD leaf unit range exceeds cluster stream: cluster={clusterId}, offset={unitByteOffset}, leafUnits={node.MNumEntries}, clusterBytes={clusterUnitDataBytes}.");
            }
        }

        // Strong identity check: each mapped leaf "first unit id" must decode back to that same unit id.
        foreach (var kvp in leafMap)
        {
            int expectedUnitId = kvp.Key;
            var leaf = kvp.Value;
            if (leaf.MNumEntries == 0)
                continue;

            int clusterId = (int)(leaf.MFirstEntry >> unitClusterIdShift);
            int unitByteOffset = (int)(leaf.MFirstEntry & offsetMask);
            int unitIndexInCluster = unitByteOffset / ClusterBinarySerializer.BytesPerTriangleUnit;
            if ((uint)clusterId >= (uint)clusters.Count ||
                (uint)unitIndexInCluster >= (uint)clusters[clusterId].UnitIds.Count)
            {
                throw new InvalidOperationException(
                    $"KD leaf first-entry decode out of bounds during identity check: cluster={clusterId}, unitIndex={unitIndexInCluster}, first={leaf.MFirstEntry}.");
            }

            int actualUnitId = clusters[clusterId].UnitIds[unitIndexInCluster];
            if (actualUnitId != expectedUnitId)
            {
                throw new InvalidOperationException(
                    $"KD leaf first-entry identity mismatch: expected unitId={expectedUnitId}, got {actualUnitId} (cluster={clusterId}, unitIndex={unitIndexInCluster}, first={leaf.MFirstEntry}).");
            }
        }
    }
}
