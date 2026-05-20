using System.Numerics;

namespace ArenaBuilder.NavPower;

/// <summary>
/// Static KD build + on-disk write matching <c>KDBuildTree</c> / <c>WriteTree</c> in NavPower <c>bfxKDTree.cpp</c>
/// (preorder: branch node, left subtree, right subtree; leaf stores prim byte offset from NavGraph image base).
/// </summary>
internal sealed class NavPowerKdBuildTree
{
    private readonly Box _bbox;
    private readonly uint _treePayloadSize;
    private readonly KdBuildNode _root;

    private NavPowerKdBuildTree(Box bbox, uint treePayloadSize, KdBuildNode root)
    {
        _bbox = bbox;
        _treePayloadSize = treePayloadSize;
        _root = root;
    }

    internal int GetOutputSize() => NavPowerBinaryConstants.KdTreeDataPrefixBytes + (int)_treePayloadSize;

    internal static NavPowerKdBuildTree? Create(IReadOnlyList<NavPrim> prims)
    {
        if (prims.Count == 0)
            return null;
        var arr = prims.ToArray();
        var root = BuildTree(arr, 0, arr.Length);
        uint sz = (uint)CountBytes(root);
        var bbox = CalcBBox(arr, 0, arr.Length);
        var tree = new NavPowerKdBuildTree(bbox, sz, root);
        tree.AssignOffsets();
        return tree;
    }

    internal void Write(int graphImageBase, BigEndianWriter w)
    {
        WriteBox(_bbox, w);
        w.WriteUInt32(_treePayloadSize);
        WriteTree(_root, graphImageBase, w);
    }

    private static void WriteTree(KdBuildNode? n, int graphImageBase, BigEndianWriter w)
    {
        if (n == null)
            return;
        if (n.Leaf)
        {
            if (n.Prim == null)
                throw new InvalidOperationException("KD leaf without prim.");
            uint po = (uint)n.Prim.PrimOffset;
            uint data = NavPowerBinaryConstants.KdLeafMask | (po & NavPowerBinaryConstants.KdPrimOffsetMask);
            w.WriteUInt32(data);
            return;
        }

        uint nodeData = ((uint)n.Axis << NavPowerBinaryConstants.KdAxisShift) & NavPowerBinaryConstants.KdAxisMask;
        nodeData |= n.RightOffset & NavPowerBinaryConstants.KdRightOffsetMask;
        w.WriteUInt32(nodeData);
        w.WriteFloat32(n.DLeft);
        w.WriteFloat32(n.DRight);
        WriteTree(n.Left, graphImageBase, w);
        WriteTree(n.Right, graphImageBase, w);
    }

    private static void WriteBox(Box b, BigEndianWriter w)
    {
        w.WriteFloat32(b.Min.X);
        w.WriteFloat32(b.Min.Y);
        w.WriteFloat32(b.Min.Z);
        w.WriteFloat32(b.Max.X);
        w.WriteFloat32(b.Max.Y);
        w.WriteFloat32(b.Max.Z);
    }

    private static int CountBytes(KdBuildNode? n)
    {
        if (n == null)
            return 0;
        if (n.Leaf)
            return NavPowerBinaryConstants.KdLeafBytes;
        return NavPowerBinaryConstants.KdNodeBytes
            + CountBytes(n.Left)
            + CountBytes(n.Right);
    }

    private static void ComputeNodeOffsets(KdBuildNode n, ref int offset)
    {
        if (n.Leaf)
        {
            offset += NavPowerBinaryConstants.KdLeafBytes;
            return;
        }

        int thisNodeOffset = offset;
        offset += NavPowerBinaryConstants.KdNodeBytes;
        ComputeNodeOffsets(n.Left!, ref offset);
        n.RightOffset = (uint)(offset - thisNodeOffset);
        ComputeNodeOffsets(n.Right!, ref offset);
    }

    private static KdBuildNode BuildTree(NavPrim[] prims, int start, int count)
    {
        if (count == 1)
            return new KdBuildNode { Leaf = true, Prim = prims[start] };

        Box bbox = CalcBBox(prims, start, count);
        Vector3 w = bbox.Max - bbox.Min;
        int axis = 2;
        if (w.X >= w.Y && w.X >= w.Z)
            axis = 0;
        else if (w.Y >= w.Z)
            axis = 1;

        // Pick a static comparer per axis instead of allocating a fresh
        // delegate-wrapping Comparer<NavPrim> via Comparer<>.Create per
        // recursion. The tree depth on a typical tile is ~log₂(5k) ≈ 12,
        // and recursion produces a sort call on each internal node — without
        // the static comparers we'd allocate 2 × internal-node-count
        // closures+comparers per tile, plus pay delegate-invoke cost on
        // every comparison.
        IComparer<NavPrim> cmp = axis switch
        {
            0 => PrimCenterXComparer.Instance,
            1 => PrimCenterYComparer.Instance,
            _ => PrimCenterZComparer.Instance,
        };
        Array.Sort(prims, start, count, cmp);

        int split = count / 2;
        AxisSpan leftSpan = CalcSpan(prims, start, split, axis);
        AxisSpan rightSpan = CalcSpan(prims, start + split, count - split, axis);
        var left = BuildTree(prims, start, split);
        var right = BuildTree(prims, start + split, count - split);
        return new KdBuildNode
        {
            Leaf = false,
            Axis = axis,
            DLeft = leftSpan.Max,
            DRight = rightSpan.Min,
            Left = left,
            Right = right,
        };
    }

    private static float GetCenter(NavPrim p, int axis) => axis switch
    {
        0 => (p.Min.X + p.Max.X) * 0.5f,
        1 => (p.Min.Y + p.Max.Y) * 0.5f,
        _ => (p.Min.Z + p.Max.Z) * 0.5f,
    };

    private readonly struct AxisSpan
    {
        internal readonly float Min;
        internal readonly float Max;
        internal AxisSpan(float min, float max)
        {
            Min = min;
            Max = max;
        }
    }

    private static AxisSpan CalcSpan(NavPrim[] prims, int start, int count, int axis)
    {
        float mn = float.MaxValue, mx = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            var p = prims[start + i];
            float a = axis switch
            {
                0 => p.Min.X,
                1 => p.Min.Y,
                _ => p.Min.Z,
            };
            float b = axis switch
            {
                0 => p.Max.X,
                1 => p.Max.Y,
                _ => p.Max.Z,
            };
            if (a < mn) mn = a;
            if (b > mx) mx = b;
        }

        return new AxisSpan(mn, mx);
    }

    private static Box CalcBBox(NavPrim[] prims, int start, int count)
    {
        var mn = new Vector3(float.MaxValue);
        var mx = new Vector3(float.MinValue);
        for (int i = 0; i < count; i++)
        {
            var p = prims[start + i];
            mn = Vector3.Min(mn, p.Min);
            mx = Vector3.Max(mx, p.Max);
        }

        return new Box(mn, mx);
    }

    private void AssignOffsets()
    {
        int off = 0;
        ComputeNodeOffsets(_root, ref off);
        if (off != _treePayloadSize)
            throw new InvalidOperationException($"KD offset walk {off} != m_size {_treePayloadSize}");
    }

    private sealed class KdBuildNode
    {
        internal bool Leaf;
        internal NavPrim? Prim;
        internal int Axis;
        internal float DLeft;
        internal float DRight;
        internal uint RightOffset;
        internal KdBuildNode? Left;
        internal KdBuildNode? Right;
    }

    // Per-axis static comparers — singletons reused across every recursive
    // sort. Replaces `Comparer<NavPrim>.Create(lambda)` allocations.
    private sealed class PrimCenterXComparer : IComparer<NavPrim>
    {
        internal static readonly PrimCenterXComparer Instance = new();
        public int Compare(NavPrim? a, NavPrim? b) =>
            ((a!.Min.X + a.Max.X) * 0.5f).CompareTo((b!.Min.X + b.Max.X) * 0.5f);
    }
    private sealed class PrimCenterYComparer : IComparer<NavPrim>
    {
        internal static readonly PrimCenterYComparer Instance = new();
        public int Compare(NavPrim? a, NavPrim? b) =>
            ((a!.Min.Y + a.Max.Y) * 0.5f).CompareTo((b!.Min.Y + b.Max.Y) * 0.5f);
    }
    private sealed class PrimCenterZComparer : IComparer<NavPrim>
    {
        internal static readonly PrimCenterZComparer Instance = new();
        public int Compare(NavPrim? a, NavPrim? b) =>
            ((a!.Min.Z + a.Max.Z) * 0.5f).CompareTo((b!.Min.Z + b.Max.Z) * 0.5f);
    }
}

internal readonly struct Box
{
    internal readonly Vector3 Min;
    internal readonly Vector3 Max;
    internal Box(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
    }
}

/// <summary>One navigable primitive for KD build (area bbox + byte offset into NavGraph image).</summary>
internal sealed class NavPrim
{
    internal int PrimOffset { get; set; }
    internal Vector3 Min;
    internal Vector3 Max;
}
