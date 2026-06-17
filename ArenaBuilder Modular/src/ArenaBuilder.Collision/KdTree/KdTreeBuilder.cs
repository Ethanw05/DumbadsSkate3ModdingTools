using ArenaBuilder.Collision.Math;
using ArenaBuilder.Collision.Rw;
using System.Numerics;

namespace ArenaBuilder.Collision.KdTree;

/// <summary>
/// Build KD-tree from triangles. RenderWare BuildNode::SplitRecurse, KDTreeBuilder::BuildTree (rwckdtreebuilder.cpp lines 1026-1303).
/// Ported from Collision_Export_Dumbad_Tuukkas_original.py lines 1331-1465.
/// </summary>
public static class KdTreeBuilder
{
    /// <summary>Recursively split node. Returns number of child nodes created (0 = leaf).</summary>
    public static int SplitRecurse(RwBuildNode node, IReadOnlyList<AABBox> entryBboxes, IList<RwEntry> entries, int depth)
    {
        if (node.MNumEntries <= KdTreeConstants.KdtreeSplitThreshold)
            return 0;
        if (depth > KdTreeConstants.RwcKdtreeMaxDepth)
            return 0;

        var nodeBbox = new AABBox(node.Bbox.Min, node.Bbox.Max);
        var split = KdTreeSah.FindBestSplit(
            nodeBbox,
            entryBboxes,
            entries,
            (int)node.MFirstEntry,
            (int)node.MNumEntries,
            KdTreeConstants.KdtreeDefaultLargeItemThreshold,
            KdTreeConstants.KdtreeMinChildEntriesThreshold,
            KdTreeConstants.KdtreeMaxEntriesPerNode,
            KdTreeConstants.KdtreeMinSimilarAreaThreshold);
        if (split == null)
            return 0;

        node.MSplitAxis = split.MAxis;
        int ax = (int)split.MAxis;
        Vector3 leftMax = node.Bbox.Max, rightMin = node.Bbox.Min;
        leftMax = Vector3Extensions.WithComponent(leftMax, ax, Vector3Extensions.GetComponent(split.MLeftBBox.Max, ax));
        rightMin = Vector3Extensions.WithComponent(rightMin, ax, Vector3Extensions.GetComponent(split.MRightBBox.Min, ax));
        var leftBbox = new AABBox(node.Bbox.Min, leftMax);
        var rightBbox = new AABBox(rightMin, node.Bbox.Max);

        node.Left = new RwBuildNode(node, leftBbox, node.MFirstEntry, split.MNumLeft);
        node.Right = new RwBuildNode(node, rightBbox, node.MFirstEntry + split.MNumLeft, split.MNumRight);

        depth++;
        node.Left.MIndex = node.MIndex + 1;
        int numLeft = SplitRecurse(node.Left, entryBboxes, entries, depth);
        node.Right.MIndex = node.Left.MIndex + numLeft + 1;
        int numRight = SplitRecurse(node.Right, entryBboxes, entries, depth);
        return numLeft + numRight + 2;
    }

    /// <summary>Build KD-tree. Returns (root node, sorted entry indices).</summary>
    public static (RwBuildNode? Root, IReadOnlyList<int> SortedEntryIndices) BuildKdTree(
        IReadOnlyList<Vector3> verts,
        IReadOnlyList<(int V0, int V1, int V2)> tris)
    {
        int numTris = tris.Count;
        if (numTris > (1 << 24))
            throw new InvalidOperationException($"Too many entries for KDTree: {numTris} > {1 << 24}");

        var entryBboxes = new List<AABBox>();
        AABBox? rootBbox = null;
        for (int i = 0; i < numTris; i++)
        {
            var t = tris[i];
            var v0 = verts[t.V0];
            var v1 = verts[t.V1];
            var v2 = verts[t.V2];
            var bbox = AABBox.TriBbox(v0, v1, v2);
            entryBboxes.Add(bbox);
            rootBbox = rootBbox == null ? bbox : rootBbox.Value.Expand(bbox);
        }

        var entries = new List<RwEntry>();
        for (int i = 0; i < numTris; i++)
        {
            float sa = entryBboxes[i].SurfaceArea();
            entries.Add(new RwEntry((uint)i, sa));
        }

        RwBuildNode? root = null;
        if (numTris > 0 && rootBbox != null)
        {
            root = new RwBuildNode(null, rootBbox.Value, 0, (uint)numTris);
            root.MIndex = 0;
            SplitRecurse(root, entryBboxes, entries, 1);
        }

        var sortedEntryIndices = new int[numTris];
        for (int i = 0; i < numTris; i++)
            sortedEntryIndices[i] = (int)entries[i].EntryIndex;
        return (root, sortedEntryIndices);
    }
}
