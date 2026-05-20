using ArenaBuilder.Collision.KdTree;
using ArenaBuilder.Collision.Math;

namespace ArenaBuilder.Collision.Rw;

/// <summary>
/// KD-tree build graph node. Matches <c>KDTreeBuilder::BuildNode</c> in <c>kdtreebuilder.h</c>
/// (<c>m_parent</c>, <c>m_index</c>, <c>m_bbox</c>, <c>m_firstEntry</c>, <c>m_numEntries</c>, <c>m_splitAxis</c>, <c>m_left</c>, <c>m_right</c>);
/// not serialized; converted to runtime branch nodes by <see cref="KdTreeRuntime.InitializeRuntimeKdTree"/>.
/// </summary>
public sealed class RwBuildNode
{
    public RwBuildNode? Parent { get; set; }

    /// <summary>Depth-first flattened index; matches <c>int32_t m_index</c>.</summary>
    public int MIndex { get; set; }

    public AABBox Bbox { get; set; }

    /// <summary>First entry in sorted entry slice; matches <c>uint32_t m_firstEntry</c>.</summary>
    public uint MFirstEntry { get; set; }

    /// <summary>
    /// Initial <see cref="MFirstEntry"/> when the node was created. Not in RW; used by the pipeline
    /// to detect whether a leaf’s first-entry was rewritten (e.g. clustered mesh).
    /// </summary>
    public uint MFirstEntryInitial { get; set; }

    /// <summary>Entry count in this node; matches <c>uint32_t m_numEntries</c>.</summary>
    public uint MNumEntries { get; set; }

    /// <summary>Split axis when branched; matches <c>uint32_t m_splitAxis</c>.</summary>
    public uint MSplitAxis { get; set; }

    public RwBuildNode? Left { get; set; }
    public RwBuildNode? Right { get; set; }

    public RwBuildNode(RwBuildNode? parent, AABBox bbox, uint firstEntry, uint numEntries)
    {
        Parent = parent;
        MIndex = 0;
        Bbox = bbox;
        MFirstEntry = firstEntry;
        MFirstEntryInitial = firstEntry;
        MNumEntries = numEntries;
        MSplitAxis = 0;
        Left = null;
        Right = null;
    }
}
