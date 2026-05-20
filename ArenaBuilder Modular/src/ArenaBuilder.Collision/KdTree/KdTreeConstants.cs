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

    /// <summary><c>rwcKDTREEBUILER_DEFAULTMAXENTRIESPERNODE</c> (<c>kdtreebuilder.h</c> line 66; EA macro spelling).</summary>
    public const int KdtreeMaxEntriesPerNode = 63;
    /// <summary>rwckdtreebuilder.cpp line 28.</summary>
    public const float RwcKdtreebuildSplitCostThreshold = 0.95f;

    /// <summary>rwckdtreebuilder.cpp line 33; rwcKDTREEBUILD_EMPTY_LEAF_THRESHOLD.</summary>
    public const float RwcKdtreebuildEmptyLeafThreshold = 0.6f;

    /// <summary>RenderWare default: kdtreebuilder.h line 42; rwcKDTREEBUILDER_DEFAULTLARGEITEMTHRESHOLD.</summary>
    public const float KdtreeDefaultLargeItemThreshold = 0.8f;

    /// <summary>RenderWare default: kdtreebuilder.h line 58; rwcKDTREEBUILDER_DEFAULTMINPROPORTIONNODEENTRIES.</summary>
    public const float KdtreeMinChildEntriesThreshold = 0.3f;

    /// <summary>RenderWare default: kdtreebuilder.h line 50; rwcKDTREEBUILDER_DEFAULTMINSIMILARSIZETHRESHOLD.</summary>
    public const float KdtreeMinSimilarAreaThreshold = 0.8f;
    /// <summary>RenderWare 6.14.00: kdtreebase.h line 40.</summary>
    public const int RwcKdtreeMaxDepth = 32;
}
