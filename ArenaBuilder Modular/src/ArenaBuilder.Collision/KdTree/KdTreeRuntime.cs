using ArenaBuilder.Collision.Math;
using ArenaBuilder.Collision.Rw;
using System.Numerics;

namespace ArenaBuilder.Collision.KdTree;

/// <summary>
/// Runtime KD-tree node. Matches RenderWare BranchNode / NodeRef. rwckdtreebuilder.cpp lines 1309-1387.
/// Ported from Collision_Export_Dumbad_Tuukkas_original.py KDNode (lines 686-695) and RW_InitializeRuntimeKDTree (2162-2316).
/// </summary>
public sealed class KdTreeNode
{
    public int Parent { get; set; }
    public uint Axis { get; set; }
    public float Ext0 { get; set; }
    public float Ext1 { get; set; }
    /// <summary>Child refs: (content, index). content=0xFFFFFFFF for branch, else numEntries; index=child node index or firstEntry.</summary>
    public (uint Content, uint Index)[] Entries { get; set; } = new (uint, uint)[2];

    public const uint BranchNode = 0xFFFFFFFF;
    public const uint InvalidIndex = 0xFFFFFFFF;
}

/// <summary>
/// Convert BuildNode tree to runtime KDNode array. RW_InitializeRuntimeKDTree (rwckdtreebuilder.cpp lines 1309-1387).
/// </summary>
public static class KdTreeRuntime
{
    private static int CountBranches(RwBuildNode? node)
    {
        if (node == null || node.Left == null) return 0;
        return 1 + CountBranches(node.Left) + CountBranches(node.Right);
    }

    private static int CountAllNodes(RwBuildNode? node)
    {
        if (node == null) return 0;
        if (node.Left == null) return 1;
        return 1 + CountAllNodes(node.Left) + CountAllNodes(node.Right);
    }

    public static IReadOnlyList<KdTreeNode> InitializeRuntimeKdTree(RwBuildNode? root)
    {
        if (root == null) return Array.Empty<KdTreeNode>();
        int numBranches = CountBranches(root);
        int totalNodes = CountAllNodes(root);
        if (1 + 2 * numBranches != totalNodes)
            throw new InvalidOperationException($"Invalid tree structure: 1 + 2*{numBranches} != {totalNodes}");
        if (numBranches == 0) return Array.Empty<KdTreeNode>();

        var rtNodes = new KdTreeNode[numBranches];
        for (int i = 0; i < rtNodes.Length; i++)
            rtNodes[i] = new KdTreeNode();
        var stack = new List<(int RtParent, int RtChild, RwBuildNode Node)>();
        stack.Add((0, 0, root));
        int top = 1;
        int rtIndex = 0;

        while (top > 0)
        {
            top--;
            var cur = stack[top];
            if (rtIndex != 0)
            {
                var parentNode = rtNodes[cur.RtParent];
                parentNode.Entries[cur.RtChild] = (KdTreeNode.BranchNode, (uint)rtIndex);
            }

            var rtNode = rtNodes[rtIndex];
            var childNodes = new[] { cur.Node.Left!, cur.Node.Right! };
            rtNode.Parent = cur.RtParent;
            rtNode.Axis = cur.Node.MSplitAxis;
            rtNode.Ext0 = Vector3Extensions.GetComponent(childNodes[0].Bbox.Max, (int)rtNode.Axis);
            rtNode.Ext1 = Vector3Extensions.GetComponent(childNodes[1].Bbox.Min, (int)rtNode.Axis);
            rtNode.Entries = new (uint, uint)[2];

            for (int i = 1; i >= 0; i--)
            {
                var child = childNodes[i];
                if (child.Left == null)
                    rtNode.Entries[i] = (child.MNumEntries, child.MFirstEntry);
                else
                {
                    while (stack.Count <= top) stack.Add(default);
                    stack[top] = (rtIndex, i, child);
                    top++;
                    rtNode.Entries[i] = (KdTreeNode.BranchNode, KdTreeNode.InvalidIndex);
                }
            }
            rtIndex++;
        }

        if (rtIndex != numBranches)
            throw new InvalidOperationException($"Invalid number of nodes: expected {numBranches}, got {rtIndex}");
        return rtNodes;
    }

    /// <summary>
    /// Validates and returns (true, null) or (false, reason). Use for detailed failure messages.
    /// </summary>
    /// <param name="traversalLog">Optional. When provided, collects (first, count) for each leaf in validation traversal order. On monotonicity failure the reason includes the last entries and failing leaf for debugging.</param>
    /// <param name="skipLeafMonotonicityCheck">If true, do not require leaf first >= lastLeafEntryIndex (for ClusteredMesh with original clusters).</param>
    public static (bool Ok, string? FailureReason) IsValidWithReason(
        IReadOnlyList<KdTreeNode> branchNodes,
        uint numEntries,
        AABBox rootBbox = default,
        List<(uint First, uint Count)>? traversalLog = null,
        bool skipLeafMonotonicityCheck = false)
    {
        if (branchNodes == null)
            return (false, "branchNodes is null");
        if (branchNodes.Count == 0)
            return (true, null);

        if (branchNodes[0].Parent != 0)
            return (false, "root branch parent is not 0");

        uint leafEntryCountCheck = 0;
        uint lastLeafEntryIndex = 0;
        int branchIndexCheck = 0;
        // Stack: branch (branchIndex >= 0) or leaf (branchIndex == -1, leafFirst/leafCount set). Push right then left so pop = depth-first left-first (RW Traversal order).
        var stack = new List<(int BranchIndex, uint ParentOrFirst, Vector3 BboxMin, Vector3 BboxMax, uint LeafCount)>();
        stack.Add((0, 0, rootBbox.Min, rootBbox.Max, 0));

        while (stack.Count > 0)
        {
            var (branchIndex, parentOrFirst, bboxMin, bboxMax, leafCount) = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);

            if (branchIndex == -1)
            {
                // Popped a leaf: visit it (RW: GetLeafNodeEntries then check)
                uint first = parentOrFirst;
                uint count = leafCount;
                if (count > 0)
                {
                    traversalLog?.Add((first, count));
                    if (!skipLeafMonotonicityCheck && first < lastLeafEntryIndex)
                    {
                        string tail = FormatTraversalTail(traversalLog, first, count, lastLeafEntryIndex);
                        return (false, $"leaf first {first} < lastLeafEntryIndex {lastLeafEntryIndex}.{tail}");
                    }
                    lastLeafEntryIndex = first + count;
                    leafEntryCountCheck += count;
                }
                continue;
            }

            if (branchIndex < 0 || branchIndex >= branchNodes.Count)
                return (false, $"branch index {branchIndex} out of range [0, {branchNodes.Count})");

            var branch = branchNodes[branchIndex];
            uint parentIndex = parentOrFirst;
            if (branch.Parent != parentIndex)
                return (false, $"branch {branchIndex} parent {branch.Parent} != expected {parentIndex}");
            if (branchIndex != branchIndexCheck)
                return (false, $"branch index order: got {branchIndex}, expected {branchIndexCheck}");
            branchIndexCheck++;

            if (branch.Axis > 2)
                return (false, $"branch {branchIndex} axis {branch.Axis} > 2");

            int axis = (int)branch.Axis;
            float ext0 = branch.Ext0;
            float ext1 = branch.Ext1;
            float bboxMinAxis = Vector3Extensions.GetComponent(bboxMin, axis);
            float bboxMaxAxis = Vector3Extensions.GetComponent(bboxMax, axis);
            uint leftContent = branch.Entries[0].Content;
            uint rightContent = branch.Entries[1].Content;
            // Child extent containment: matches KDTree::IsValid (rwckdtree.cpp lines 117-134).
            // Uses Min/Max of both branch planes vs current region bbox on split axis.
            float minPlane = System.Math.Min(ext0, ext1);
            float maxPlane = System.Math.Max(ext0, ext1);
            if (bboxMinAxis > minPlane)
                return (false, $"branch {branchIndex} does not enclose left child extent (bbox.min[{axis}]={bboxMinAxis} > min(ext0,ext1)={minPlane})");
            if (bboxMaxAxis < maxPlane)
                return (false, $"branch {branchIndex} does not enclose right child extent (bbox.max[{axis}]={bboxMaxAxis} < max(ext0,ext1)={maxPlane})");

            var leftBboxMax = Vector3Extensions.WithComponent(bboxMax, axis, ext0);
            var rightBboxMin = Vector3Extensions.WithComponent(bboxMin, axis, ext1);
            bool leftIsBranch = branch.Entries[0].Content == KdTreeNode.BranchNode;
            bool rightIsBranch = branch.Entries[1].Content == KdTreeNode.BranchNode;
            uint branchIndexU = (uint)branchIndex;

            // Push right then left so we pop left first (depth-first left-first, matching RW Traversal and CollectLeavesInOrder).
            if (rightIsBranch)
                stack.Add(((int)branch.Entries[1].Index, branchIndexU, rightBboxMin, bboxMax, 0));
            else if (rightContent > 0)
                stack.Add((-1, branch.Entries[1].Index, default, default, rightContent));
            if (leftIsBranch)
                stack.Add(((int)branch.Entries[0].Index, branchIndexU, bboxMin, leftBboxMax, 0));
            else if (leftContent > 0)
                stack.Add((-1, branch.Entries[0].Index, default, default, leftContent));
        }

        if (leafEntryCountCheck != numEntries)
            return (false, $"sum of leaf entry counts ({leafEntryCountCheck}) != numEntries ({numEntries})");
        return (true, null);
    }

    private static string FormatTraversalTail(List<(uint First, uint Count)>? log, uint failFirst, uint failCount, uint lastLeafEntryIndex)
    {
        if (log == null || log.Count == 0)
            return " Traversal log not collected.";
        const int maxShow = 24;
        int start = System.Math.Max(0, log.Count - maxShow);
        var segment = log.Skip(start).Take(maxShow).Select(x => $"({x.First},{x.Count})");
        return " Validation traversal order (first,count) last " + (log.Count - start) + " leaves: [" + string.Join(", ", segment) + "]. Failing leaf: first=" + failFirst + " count=" + failCount + " (lastLeafEntryIndex was " + lastLeafEntryIndex + ").";
    }

    /// <summary>
    /// Throws if the runtime KD-tree is invalid. Use after InitializeRuntimeKdTree when numEntries and root bbox are available.
    /// </summary>
    /// <param name="skipLeafMonotonicityCheck">If true, do not require leaf first >= lastLeafEntryIndex. Use for ClusteredMesh when using original walk+merge clusters (leaf Index is clusterId+offset, not globally monotonic in traversal order).</param>
    public static void Validate(IReadOnlyList<KdTreeNode> branchNodes, uint numEntries, AABBox rootBbox = default, bool skipLeafMonotonicityCheck = false)
    {
        var (ok, reason) = IsValidWithReason(branchNodes, numEntries, rootBbox, null, skipLeafMonotonicityCheck);
        if (!ok)
            throw new InvalidOperationException("KD-tree validation failed: " + (reason ?? "invalid structure."));
    }
}
