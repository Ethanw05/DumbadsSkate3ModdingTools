using ArenaBuilder.Collision.Math;

namespace ArenaBuilder.Collision.Rw;

/// <summary>
/// Axis-aligned KD split plane. Matches <c>struct KDTreeSplit</c> in rwckdtreebuilder.cpp
/// (<c>m_axis</c>, <c>m_value</c>, <c>m_numLeft</c>, <c>m_numRight</c>, child bboxes).
/// </summary>
public sealed class RwKdTreeSplit
{
    /// <summary>Split axis 0=X, 1=Y, 2=Z; matches <c>uint32_t m_axis</c>.</summary>
    public uint MAxis { get; set; }

    /// <summary>Split plane position on axis; matches <c>rwpmath::VecFloat m_value</c>.</summary>
    public float MValue { get; set; }

    public uint MNumLeft { get; set; }
    public uint MNumRight { get; set; }
    public AABBox MLeftBBox { get; set; }
    public AABBox MRightBBox { get; set; }

    public RwKdTreeSplit()
    {
        MAxis = 0;
        MValue = 0f;
        MNumLeft = 0;
        MNumRight = 0;
        MLeftBBox = new AABBox(System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero);
        MRightBBox = new AABBox(System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero);
    }
}
