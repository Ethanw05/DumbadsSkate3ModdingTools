namespace ArenaBuilder.Collision.KdTree;

/// <summary>
/// KD-tree build constants. RenderWare: rwckdtreebuilder.cpp lines 24-33; clusteredmeshbuilder.h Parameters (e.g. split threshold line 180);
/// kdtreebuilder.h (large-item line 42, min similar line 50, min child proportion line 58, max entries line 66 as rwcKDTREEBUILER_DEFAULTMAXENTRIESPERNODE); kdtreebase.h rwcKDTREE_MAX_DEPTH line 40.
/// Ported from Collision_Export_Dumbad_Tuukkas_original.py lines 646-652, 705-707.
/// </summary>
public static class KdTreeConstants
{
    /// <summary>Do not split if node has this many or fewer entries. RenderWare default: clusteredmeshbuilder.h line 180.</summary>
    public const int KdtreeSplitThreshold = 8;

    /// <summary>
    /// Maximum entries per node before the non-spatial safety-net split fires.
    /// RW default (<c>rwcKDTREEBUILER_DEFAULTMAXENTRIESPERNODE</c>, <c>kdtreebuilder.h</c> line 66) is 63.
    /// Slightly lowered to 48: keeps leaf size close to stock (so runtime KD-tree queries stay near stock cost),
    /// but trips the safety net a touch sooner — enough headroom to keep the densest DLC leaves under the
    /// 255-vert unit-cluster cap at depth <see cref="RwcKdtreeMaxDepth"/>.
    /// </summary>
    public const int KdtreeMaxEntriesPerNode = 48;
    /// <summary>
    /// SAH split cost cutoff. RW default (<c>rwcKDTREEBUILD_SPLIT_COST_THRESHOLD</c>, <c>rwckdtreebuilder.cpp</c>
    /// line 28) is 0.95 — very lax, accepts near-no-gain splits (e.g. 1/N) that consume a depth level for almost
    /// no entry reduction. This single knob is structurally the most important: SAH gets first dibs in
    /// <c>KdTreeSah</c>, so if it accepts a bad split the safety net never fires on that subtree. Tightened to
    /// 0.5: still permissive enough to keep most stock-shaped splits, but rejects clearly bad ones so the safety
    /// net (with <see cref="KdtreeMinChildEntriesThreshold"/> = 0.35) has the depth budget it needs to fit the
    /// densest DLC leaves under the 255-vert unit-cluster cap. Note: 0.8 was tested and still hit depth 33 on
    /// DIST_Fireside Dome dense leaves — that's the empirical upper bound on this knob for our content.
    /// </summary>
    public const float RwcKdtreebuildSplitCostThreshold = 0.625f;

    /// <summary>rwckdtreebuilder.cpp line 33; rwcKDTREEBUILD_EMPTY_LEAF_THRESHOLD.</summary>
    public const float RwcKdtreebuildEmptyLeafThreshold = 0.6f;

    /// <summary>RenderWare default: kdtreebuilder.h line 42; rwcKDTREEBUILDER_DEFAULTLARGEITEMTHRESHOLD.</summary>
    public const float KdtreeDefaultLargeItemThreshold = 0.8f;

    /// <summary>
    /// Minimum fraction of entries each side of a non-spatial safety-net split must hold.
    /// RW default (<c>rwcKDTREEBUILDER_DEFAULTMINPROPORTIONNODEENTRIES</c>, <c>kdtreebuilder.h</c> line 58) is 0.3
    /// (permits 30/70 safety-net splits). Slightly raised to 0.35: barely tighter than stock so the safety net
    /// shrinks the heavy child a little faster (×0.65 vs ×0.70 per level) on degenerate dense leaves.
    /// </summary>
    public const float KdtreeMinChildEntriesThreshold = 0.35f;

    /// <summary>RenderWare default: kdtreebuilder.h line 50; rwcKDTREEBUILDER_DEFAULTMINSIMILARSIZETHRESHOLD.</summary>
    public const float KdtreeMinSimilarAreaThreshold = 0.8f;
    /// <summary>RenderWare 6.14.00: kdtreebase.h line 40.</summary>
    public const int RwcKdtreeMaxDepth = 32;
}
