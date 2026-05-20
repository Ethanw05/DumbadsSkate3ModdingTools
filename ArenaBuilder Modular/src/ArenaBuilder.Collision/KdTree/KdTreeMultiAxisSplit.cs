using ArenaBuilder.Collision.Math;

namespace ArenaBuilder.Collision.KdTree;

/// <summary>
/// Multi-axis split stats. RenderWare KDTreeMultiAxisSplit (rwckdtreebuilder.cpp).
/// Ported from Collision_Export_Dumbad_Tuukkas_original.py lines 813-821.
/// </summary>
public sealed class KdTreeMultiAxisSplit
{
    /// <summary>Split values for all 3 axes (matches rwckdtreebuilder.cpp KDTreeMultiAxisSplit::m_value).</summary>
    public float[] MValue { get; } = new float[3];
    public int[] MNumLeft { get; } = new int[3];
    public int[] MNumRight { get; } = new int[3];
    public AABBox[] MLeftBBox { get; } = new AABBox[3];
    public AABBox[] MRightBBox { get; } = new AABBox[3];
}
