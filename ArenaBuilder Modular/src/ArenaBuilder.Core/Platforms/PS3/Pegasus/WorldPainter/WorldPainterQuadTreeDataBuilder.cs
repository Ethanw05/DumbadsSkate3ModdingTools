using System.Buffers.Binary;

namespace ArenaBuilder.Core.Platforms.PS3.Pegasus.WorldPainter;

/// <summary>
/// Builds pegasus::tWorldPainterQuadTreeData (0x00EB0010).
/// Child index <c>i</c> must match <c>WorldPainter::DoQuadTreeLookup</c> traversal
/// (<c>documentation/WorldPainter/8238F988</c>) and the direction rows in <c>documentation/WorldPainter/8238FB80</c>.
/// Layout:
/// - 0x00..0x0F: <c>m_RootNodeTestSubtractor</c> — <c>rw::math::vpu::Vector2</c> / <c>__vector4</c> (16 bytes)
/// - 0x10..0x1F: <c>m_RootNodeHalfWidth</c> — same (see <see cref="WorldPainterVpuVector2OnDisk"/>)
/// - 0x20..0x23: m_uiNumNodes
/// - 0x24..0x27: m_pRootNode (blob-relative offset, typically 0x30)
/// - 0x30..     : tNode[] (0x0A each)
/// </summary>
public static class WorldPainterQuadTreeDataBuilder
{
    private const int HeaderSize = 0x30;
    private const int NodeSize = 0x0A;

    /// <summary>
    /// Vanilla pegasus::tWorldPainterQuadTreeData::tNode uses 0xFFFF on internal (non-leaf) nodes.
    /// Only leaves use a real WPDICT slot index; do not store fallback keys on internals.
    /// </summary>
    public const ushort InternalNodeDictionaryLookup = ushort.MaxValue;

    public readonly record struct WorldPainterQuadNode(short Child0, short Child1, short Child2, short Child3, ushort DictionaryLookup)
    {
        public static WorldPainterQuadNode Leaf(ushort dictionaryLookup) =>
            new(-1, -1, -1, -1, dictionaryLookup);

        /// <summary>True when this node is a leaf (<see cref="Leaf"/>): all child indices are -1.</summary>
        public bool IsLeaf => Child0 < 0;
    }

    /// <summary>
    /// Branch nodes have non-negative child indices; leaves use <c>-1</c> for all children (<see cref="WorldPainterQuadNode.Leaf"/>).
    /// </summary>
    public static void CountQuadNodeKinds(
        IReadOnlyList<WorldPainterQuadNode> nodes,
        out int internalNodeCount,
        out int leafCount)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        int leaves = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].IsLeaf)
                leaves++;
        }

        leafCount = leaves;
        internalNodeCount = nodes.Count - leaves;
    }

    public static byte[] BuildSingleLeaf(float rootCenterX, float rootCenterY, float rootHalfX, float rootHalfY, ushort dictionaryLookup)
    {
        return Build(
            rootCenterX,
            rootCenterY,
            rootHalfX,
            rootHalfY,
            new[] { WorldPainterQuadNode.Leaf(dictionaryLookup) });
    }

    public static byte[] Build(
        float rootCenterX,
        float rootCenterY,
        float rootHalfX,
        float rootHalfY,
        IReadOnlyList<WorldPainterQuadNode> nodes)
    {
        if (nodes == null || nodes.Count == 0)
            throw new ArgumentException("WorldPainter quadtree requires at least one node.", nameof(nodes));
        if (nodes.Count > WorldPainterQuadTreeValidator.MaxQuadNodeCount)
            throw new ArgumentOutOfRangeException(
                nameof(nodes),
                $"Quadtree node count exceeds engine limit {WorldPainterQuadTreeValidator.MaxQuadNodeCount} (signed int16 indices).");

        var blob = new byte[HeaderSize + nodes.Count * NodeSize];
        var s = blob.AsSpan();

        WorldPainterVpuVector2OnDisk.WriteRootPairXyZeroZw(s.Slice(0, 0x20), rootCenterX, rootCenterY, rootHalfX, rootHalfY);

        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x20, 4), (uint)nodes.Count);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x24, 4), HeaderSize);

        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            int off = HeaderSize + i * NodeSize;
            BinaryPrimitives.WriteInt16BigEndian(s.Slice(off + 0, 2), n.Child0);
            BinaryPrimitives.WriteInt16BigEndian(s.Slice(off + 2, 2), n.Child1);
            BinaryPrimitives.WriteInt16BigEndian(s.Slice(off + 4, 2), n.Child2);
            BinaryPrimitives.WriteInt16BigEndian(s.Slice(off + 6, 2), n.Child3);
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(off + 8, 2), n.DictionaryLookup);
        }

        return blob;
    }
}
