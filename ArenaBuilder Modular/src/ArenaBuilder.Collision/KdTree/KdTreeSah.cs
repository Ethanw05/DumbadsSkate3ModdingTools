using ArenaBuilder.Collision.Math;
using ArenaBuilder.Collision.Rw;
using System.Numerics;

namespace ArenaBuilder.Collision.KdTree;

/// <summary>
/// SAH (Surface Area Heuristic) and split helpers. RenderWare rwckdtreebuilder.cpp.
/// Ported from Collision_Export_Dumbad_Tuukkas_original.py lines 709-1330.
/// </summary>
public static class KdTreeSah
{
    /// <summary>Surface area of bbox. rwc_BBoxSurfaceArea (rwckdtreebuilder.cpp lines 136-140).</summary>
    public static float BBoxSurfaceArea(AABBox bbox) => bbox.SurfaceArea();

    /// <summary>Sort entries into left/right by split plane. Modifies entries in-place. rwc_SortSplitEntries (lines 266-306).</summary>
    public static void SortSplitEntries(RwKdTreeSplit split, IReadOnlyList<AABBox> entryBboxes, IList<RwEntry> entries, int startIndex, int numEntries)
    {
        int iLeft = startIndex;
        int iRight = startIndex + numEntries - 1;
        int axis = (int)split.MAxis;
        float splitValue = split.MValue;

        while (iLeft <= iRight)
        {
            var entry = entries[iLeft];
            var bb = entryBboxes[(int)entry.EntryIndex];
            float centerAxis = axis switch
            {
                0 => (bb.Min.X + bb.Max.X) * 0.5f,
                1 => (bb.Min.Y + bb.Max.Y) * 0.5f,
                _ => (bb.Min.Z + bb.Max.Z) * 0.5f
            };
            bool swap = centerAxis > splitValue; // STRICTLY >
            if (swap)
            {
                (entries[iLeft], entries[iRight]) = (entries[iRight], entries[iLeft]);
                iRight--;
            }
            else
                iLeft++;
        }

        int actualLeft = iLeft - startIndex;
        int actualRight = numEntries - actualLeft;
        if (split.MNumLeft != (uint)actualLeft || split.MNumRight != (uint)actualRight)
            throw new InvalidOperationException($"Split count mismatch: expected left={split.MNumLeft} right={split.MNumRight}, got left={actualLeft} right={actualRight}");
    }

    /// <summary>Update split stats for one entry. rwc_UpdateSplitStats (lines 177-245).</summary>
    private static void UpdateSplitStats(
        bool[] axisComparison,
        Vector3 minExtent, Vector3 maxExtent,
        int[] rightCount, int[] leftCount,
        ref Vector3 leftMinX, ref Vector3 leftMaxX, ref Vector3 rightMinX, ref Vector3 rightMaxX,
        ref Vector3 leftMinY, ref Vector3 leftMaxY, ref Vector3 rightMinY, ref Vector3 rightMaxY,
        ref Vector3 leftMinZ, ref Vector3 leftMaxZ, ref Vector3 rightMinZ, ref Vector3 rightMaxZ)
    {
        for (int a = 0; a < 3; a++)
        {
            if (axisComparison[a]) rightCount[a]++;
            else leftCount[a]++;
        }
        Vector3 newLeftMin = Vector3.Min(leftMinX, minExtent);
        Vector3 newLeftMax = Vector3.Max(leftMaxX, maxExtent);
        Vector3 newRightMin = Vector3.Min(rightMinX, minExtent);
        Vector3 newRightMax = Vector3.Max(rightMaxX, maxExtent);
        if (axisComparison[0]) { rightMinX = newRightMin; rightMaxX = newRightMax; }
        else { leftMinX = newLeftMin; leftMaxX = newLeftMax; }
        newLeftMin = Vector3.Min(leftMinY, minExtent);
        newLeftMax = Vector3.Max(leftMaxY, maxExtent);
        newRightMin = Vector3.Min(rightMinY, minExtent);
        newRightMax = Vector3.Max(rightMaxY, maxExtent);
        if (axisComparison[1]) { rightMinY = newRightMin; rightMaxY = newRightMax; }
        else { leftMinY = newLeftMin; leftMaxY = newLeftMax; }
        newLeftMin = Vector3.Min(leftMinZ, minExtent);
        newLeftMax = Vector3.Max(leftMaxZ, maxExtent);
        newRightMin = Vector3.Min(rightMinZ, minExtent);
        newRightMax = Vector3.Max(rightMaxZ, maxExtent);
        if (axisComparison[2]) { rightMinZ = newRightMin; rightMaxZ = newRightMax; }
        else { leftMinZ = newLeftMin; leftMaxZ = newLeftMax; }
    }

    /// <summary>Get split stats for all 3 axes. <c>rwc_GetSplitStatsAllAxis</c> (<c>rwckdtreebuilder.cpp</c> lines 407-474).</summary>
    private static void GetSplitStatsAllAxis(KdTreeMultiAxisSplit split, IReadOnlyList<AABBox> entryBboxes,
        IList<RwEntry> entries, int startIndex, int numEntries, AABBox nodeBbox)
    {
        int[] leftCount = new int[3], rightCount = new int[3];
        Vector3 leftMinX = nodeBbox.Max, leftMaxX = nodeBbox.Min;
        Vector3 rightMinX = nodeBbox.Max, rightMaxX = nodeBbox.Min;
        Vector3 leftMinY = nodeBbox.Max, leftMaxY = nodeBbox.Min;
        Vector3 rightMinY = nodeBbox.Max, rightMaxY = nodeBbox.Min;
        Vector3 leftMinZ = nodeBbox.Max, leftMaxZ = nodeBbox.Min;
        Vector3 rightMinZ = nodeBbox.Max, rightMaxZ = nodeBbox.Min;

        for (int i = 0; i < numEntries; i++)
        {
            var bb = entryBboxes[(int)entries[startIndex + i].EntryIndex];
            var minExtent = bb.Min;
            var maxExtent = bb.Max;
            float cx = (minExtent.X + maxExtent.X) * 0.5f;
            float cy = (minExtent.Y + maxExtent.Y) * 0.5f;
            float cz = (minExtent.Z + maxExtent.Z) * 0.5f;
            bool[] axisComparison = {
                cx > split.MValue[0],
                cy > split.MValue[1],
                cz > split.MValue[2]
            };
            UpdateSplitStats(axisComparison, minExtent, maxExtent,
                rightCount, leftCount,
                ref leftMinX, ref leftMaxX, ref rightMinX, ref rightMaxX,
                ref leftMinY, ref leftMaxY, ref rightMinY, ref rightMaxY,
                ref leftMinZ, ref leftMaxZ, ref rightMinZ, ref rightMaxZ);
        }

        for (int a = 0; a < 3; a++)
        {
            split.MNumLeft[a] = leftCount[a];
            split.MNumRight[a] = rightCount[a];
        }
        split.MLeftBBox[0] = new AABBox(leftMinX, leftMaxX);
        split.MRightBBox[0] = new AABBox(rightMinX, rightMaxX);
        split.MLeftBBox[1] = new AABBox(leftMinY, leftMaxY);
        split.MRightBBox[1] = new AABBox(rightMinY, rightMaxY);
        split.MLeftBBox[2] = new AABBox(leftMinZ, leftMaxZ);
        split.MRightBBox[2] = new AABBox(rightMinZ, rightMaxZ);
    }

    /// <summary>Costs for each axis. rwc_GetMultiSplitLowestCost (rwckdtreebuilder.cpp lines 627-652).</summary>
    public static float[] GetMultiSplitLowestCost(AABBox nodeBbox, KdTreeMultiAxisSplit multiSplit)
    {
        float[] leftWeight = new float[3], rightWeight = new float[3];
        for (int i = 0; i < 3; i++)
        {
            leftWeight[i] = multiSplit.MNumLeft[i] * BBoxSurfaceArea(multiSplit.MLeftBBox[i]);
            rightWeight[i] = multiSplit.MNumRight[i] * BBoxSurfaceArea(multiSplit.MRightBBox[i]);
        }
        float nodeBBArea = (multiSplit.MNumLeft[0] + multiSplit.MNumRight[0]) * BBoxSurfaceArea(nodeBbox);
        float[] costs = new float[3];
        for (int i = 0; i < 3; i++)
            costs[i] = (leftWeight[i] + rightWeight[i]) / nodeBBArea;
        return costs;
    }

    /// <summary>Select axis with lowest cost and fill result. rwc_SelectLowestCostSplit (lines 570-599).</summary>
    public static float SelectLowestCostSplit(RwKdTreeSplit result, KdTreeMultiAxisSplit multiSplit, float[] costs)
    {
        float lowestCost;
        int axis;
        if (costs[0] <= costs[1] && costs[0] <= costs[2]) { lowestCost = costs[0]; axis = 0; }
        else if (costs[1] <= costs[2]) { lowestCost = costs[1]; axis = 1; }
        else { lowestCost = costs[2]; axis = 2; }
        result.MAxis = (uint)axis;
        result.MValue = multiSplit.MValue[axis];
        result.MLeftBBox = multiSplit.MLeftBBox[axis];
        result.MRightBBox = multiSplit.MRightBBox[axis];
        result.MNumLeft = (uint)multiSplit.MNumLeft[axis];
        result.MNumRight = (uint)multiSplit.MNumRight[axis];
        return lowestCost;
    }

    /// <summary>Find best split. RenderWare rwc_FindBestSplit (rwckdtreebuilder.cpp lines 852-1002).</summary>
    public static RwKdTreeSplit? FindBestSplit(
        AABBox nodeBbox,
        IReadOnlyList<AABBox> entryBboxes,
        IList<RwEntry> entries,
        int startIndex,
        int numEntries,
        float largeItemThreshold,
        float minChildEntriesThreshold,
        int maxEntriesPerNode,
        float minSimilarAreaThreshold)
    {
        var tightBbox = entryBboxes[(int)entries[startIndex].EntryIndex];
        float sumBBoxSurfaceArea = entries[startIndex].EntryBBoxSurfaceArea;
        float smallestBBoxSurfaceArea = entries[startIndex].EntryBBoxSurfaceArea;

        for (int i = 1; i < numEntries; i++)
        {
            var bb = entryBboxes[(int)entries[startIndex + i].EntryIndex];
            tightBbox = tightBbox.Expand(bb);
            float sa = entries[startIndex + i].EntryBBoxSurfaceArea;
            sumBBoxSurfaceArea += sa;
            smallestBBoxSurfaceArea = System.Math.Min(smallestBBoxSurfaceArea, sa);
        }

        float nodeSurfaceArea = BBoxSurfaceArea(nodeBbox);
        float meanBBoxSurfaceArea = sumBBoxSurfaceArea / numEntries;

        // Empty leaf split (rwckdtreebuilder.cpp lines 894-938).
        var curSplit = new RwKdTreeSplit();
        float minChildSurfaceArea = nodeSurfaceArea;
        for (int i = 0; i < 3; i++)
        {
            var childBbox = new AABBox(nodeBbox.Min, nodeBbox.Max);
            childBbox = new AABBox(childBbox.Min, Vector3Extensions.WithComponent(childBbox.Max, i, Vector3Extensions.GetComponent(tightBbox.Max, i)));
            float childSurfaceArea = BBoxSurfaceArea(childBbox);
            if (childSurfaceArea < minChildSurfaceArea)
            {
                minChildSurfaceArea = childSurfaceArea;
                curSplit.MAxis = (uint)i;
                curSplit.MValue = Vector3Extensions.GetComponent(tightBbox.Max, i);
                curSplit.MNumLeft = (uint)numEntries;
                curSplit.MNumRight = 0;
                curSplit.MLeftBBox = tightBbox;
                curSplit.MRightBBox = new AABBox(nodeBbox.Max, nodeBbox.Min); // Inverted
            }

            childBbox = new AABBox(nodeBbox.Min, nodeBbox.Max);
            childBbox = new AABBox(Vector3Extensions.WithComponent(childBbox.Min, i, Vector3Extensions.GetComponent(tightBbox.Min, i)), childBbox.Max);
            childSurfaceArea = BBoxSurfaceArea(childBbox);
            if (childSurfaceArea < minChildSurfaceArea)
            {
                minChildSurfaceArea = childSurfaceArea;
                curSplit.MAxis = (uint)i;
                curSplit.MValue = Vector3Extensions.GetComponent(tightBbox.Min, i);
                curSplit.MNumLeft = 0;
                curSplit.MNumRight = (uint)numEntries;
                curSplit.MLeftBBox = new AABBox(nodeBbox.Max, nodeBbox.Min); // Inverted
                curSplit.MRightBBox = tightBbox;
            }
        }

        if (minChildSurfaceArea < (KdTreeConstants.RwcKdtreebuildEmptyLeafThreshold * nodeSurfaceArea))
            return curSplit;

        // SAH multi-axis (rwckdtreebuilder.cpp lines 940-960).
        var multiSplit = new KdTreeMultiAxisSplit();
        multiSplit.MValue[0] = (tightBbox.Min.X + tightBbox.Max.X) * 0.5f;
        multiSplit.MValue[1] = (tightBbox.Min.Y + tightBbox.Max.Y) * 0.5f;
        multiSplit.MValue[2] = (tightBbox.Min.Z + tightBbox.Max.Z) * 0.5f;
        GetSplitStatsAllAxis(multiSplit, entryBboxes, entries, startIndex, numEntries, nodeBbox);
        float[] costs = GetMultiSplitLowestCost(nodeBbox, multiSplit);
        var bestSplit = new RwKdTreeSplit();
        float bestCost = SelectLowestCostSplit(bestSplit, multiSplit, costs);

        if (bestSplit.MNumLeft > 0 && bestSplit.MNumRight > 0 && bestCost < KdTreeConstants.RwcKdtreebuildSplitCostThreshold)
        {
            SortSplitEntries(bestSplit, entryBboxes, entries, startIndex, numEntries);
            return bestSplit;
        }

        // Large items alternative (rwckdtreebuilder.cpp lines 962-979).
        if (largeItemThreshold < 1.0f)
        {
            GetSplitStatsAllAxisLargeItems(multiSplit, entryBboxes, entries, startIndex, numEntries, nodeBbox, largeItemThreshold);
            costs = GetMultiSplitLowestCost(nodeBbox, multiSplit);
            bestCost = SelectLowestCostSplit(bestSplit, multiSplit, costs);
            if (bestSplit.MNumLeft > 0 && bestSplit.MNumRight > 0 && bestCost < KdTreeConstants.RwcKdtreebuildSplitCostThreshold)
            {
                SortSplitEntriesLargeItems(bestSplit, entryBboxes, entries, startIndex, numEntries, nodeBbox, largeItemThreshold);
                return bestSplit;
            }
        }

        // Safety net non-spatial split (rwckdtreebuilder.cpp lines 981-998).
        if (smallestBBoxSurfaceArea < (minSimilarAreaThreshold * nodeSurfaceArea) || numEntries >= maxEntriesPerNode)
        {
            // Sort by descending entryBBoxSurfaceArea (qsort + CompareEntries).
            //
            // NB: must use a STABLE sort here. `List<T>.Sort` is introsort
            // and is *not* stable — entries with identical surface areas
            // would shuffle non-deterministically, producing different KD
            // tree shapes across runs and (more importantly) preventing
            // byte-for-byte parity with the BlenRose Python port (whose
            // `list.sort` is Timsort and stable). LINQ `OrderByDescending`
            // is stable, so we use it instead.
            var slice = new List<RwEntry>(numEntries);
            for (int i = 0; i < numEntries; i++) slice.Add(entries[startIndex + i]);
            slice = slice.OrderByDescending(e => e.EntryBBoxSurfaceArea).ToList();
            for (int i = 0; i < numEntries; i++) entries[startIndex + i] = slice[i];

            var nonSpatial = new RwKdTreeSplit();
            SplitNonSpatial(nonSpatial, nodeBbox, meanBBoxSurfaceArea, entryBboxes, entries, startIndex, numEntries, minChildEntriesThreshold);
            return nonSpatial;
        }

        return null;
    }

    private static void GetSplitMeanSurfaceArea(RwKdTreeSplit meanSplit, float meanBBoxSurfaceArea, IList<RwEntry> entries, int startIndex, int numEntries)
    {
        int entryIndex = 0;
        for (; entryIndex < numEntries; ++entryIndex)
        {
            if (entries[startIndex + entryIndex].EntryBBoxSurfaceArea <= meanBBoxSurfaceArea)
                break;
        }
        meanSplit.MNumLeft = (uint)entryIndex;
        meanSplit.MNumRight = (uint)(numEntries - entryIndex);
    }

    private static void FindBestNonSpatialAxis(RwKdTreeSplit nonSpatialSplit, AABBox nodeBbox, AABBox leftTightBbox, AABBox rightTightBbox)
    {
        float minChildSurfaceArea = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            var leftChildBbox = new AABBox(nodeBbox.Min, nodeBbox.Max);
            var rightChildBbox = new AABBox(nodeBbox.Min, nodeBbox.Max);
            leftChildBbox = new AABBox(leftChildBbox.Min, Vector3Extensions.WithComponent(leftChildBbox.Max, i, Vector3Extensions.GetComponent(leftTightBbox.Max, i)));
            rightChildBbox = new AABBox(Vector3Extensions.WithComponent(rightChildBbox.Min, i, Vector3Extensions.GetComponent(rightTightBbox.Min, i)), rightChildBbox.Max);
            float childSurfaceArea = BBoxSurfaceArea(leftChildBbox) + BBoxSurfaceArea(rightChildBbox);
            if (childSurfaceArea < minChildSurfaceArea)
            {
                minChildSurfaceArea = childSurfaceArea;
                nonSpatialSplit.MAxis = (uint)i;
            }
        }
    }

    private static void SplitNonSpatial(
        RwKdTreeSplit nonSpatialSplit,
        AABBox nodeBbox,
        float meanBBoxSurfaceArea,
        IReadOnlyList<AABBox> entryBboxes,
        IList<RwEntry> entries,
        int startIndex,
        int numEntries,
        float minChildEntriesThreshold)
    {
        GetSplitMeanSurfaceArea(nonSpatialSplit, meanBBoxSurfaceArea, entries, startIndex, numEntries);

        uint minEntries = (numEntries * minChildEntriesThreshold) > 1.0f
            ? (uint)(numEntries * minChildEntriesThreshold)
            : 1u;
        if (nonSpatialSplit.MNumLeft < minEntries || nonSpatialSplit.MNumRight < minEntries)
        {
            if (nonSpatialSplit.MNumLeft > nonSpatialSplit.MNumRight)
            {
                nonSpatialSplit.MNumRight = minEntries;
                nonSpatialSplit.MNumLeft = (uint)numEntries - nonSpatialSplit.MNumRight;
            }
            else
            {
                nonSpatialSplit.MNumLeft = minEntries;
                nonSpatialSplit.MNumRight = (uint)numEntries - nonSpatialSplit.MNumLeft;
            }
        }

        var tightLeft = entryBboxes[(int)entries[startIndex].EntryIndex];
        for (int i = 1; i < (int)nonSpatialSplit.MNumLeft; ++i)
            tightLeft = tightLeft.Expand(entryBboxes[(int)entries[startIndex + i].EntryIndex]);

        var tightRight = entryBboxes[(int)entries[startIndex + (int)nonSpatialSplit.MNumLeft].EntryIndex];
        for (int i = (int)nonSpatialSplit.MNumLeft + 1; i < numEntries; ++i)
            tightRight = tightRight.Expand(entryBboxes[(int)entries[startIndex + i].EntryIndex]);

        FindBestNonSpatialAxis(nonSpatialSplit, nodeBbox, tightLeft, tightRight);
        nonSpatialSplit.MLeftBBox = tightLeft;
        nonSpatialSplit.MRightBBox = tightRight;
    }

    public static void GetSplitStatsAllAxisLargeItems(KdTreeMultiAxisSplit split, IReadOnlyList<AABBox> entryBboxes,
        IList<RwEntry> entries, int startIndex, int numEntries, AABBox nodeBbox, float largeItemThreshold)
    {
        int[] leftCount = new int[3], rightCount = new int[3];
        Vector3 leftMinX = nodeBbox.Max, leftMaxX = nodeBbox.Min;
        Vector3 rightMinX = nodeBbox.Max, rightMaxX = nodeBbox.Min;
        Vector3 leftMinY = nodeBbox.Max, leftMaxY = nodeBbox.Min;
        Vector3 rightMinY = nodeBbox.Max, rightMaxY = nodeBbox.Min;
        Vector3 leftMinZ = nodeBbox.Max, leftMaxZ = nodeBbox.Min;
        Vector3 rightMinZ = nodeBbox.Max, rightMaxZ = nodeBbox.Min;

        var nodeSize = nodeBbox.Max - nodeBbox.Min;
        var thresholdSize = nodeSize * largeItemThreshold;

        for (int i = 0; i < numEntries; i++)
        {
            var bb = entryBboxes[(int)entries[startIndex + i].EntryIndex];
            var minExtent = bb.Min;
            var maxExtent = bb.Max;
            var boxSize = maxExtent - minExtent;
            bool[] axisComparison =
            {
                boxSize.X >= thresholdSize.X,
                boxSize.Y >= thresholdSize.Y,
                boxSize.Z >= thresholdSize.Z
            };
            UpdateSplitStats(axisComparison, minExtent, maxExtent,
                rightCount, leftCount,
                ref leftMinX, ref leftMaxX, ref rightMinX, ref rightMaxX,
                ref leftMinY, ref leftMaxY, ref rightMinY, ref rightMaxY,
                ref leftMinZ, ref leftMaxZ, ref rightMinZ, ref rightMaxZ);
        }

        for (int a = 0; a < 3; a++)
        {
            split.MNumLeft[a] = leftCount[a];
            split.MNumRight[a] = rightCount[a];
        }
        split.MLeftBBox[0] = new AABBox(leftMinX, leftMaxX);
        split.MRightBBox[0] = new AABBox(rightMinX, rightMaxX);
        split.MLeftBBox[1] = new AABBox(leftMinY, leftMaxY);
        split.MRightBBox[1] = new AABBox(rightMinY, rightMaxY);
        split.MLeftBBox[2] = new AABBox(leftMinZ, leftMaxZ);
        split.MRightBBox[2] = new AABBox(rightMinZ, rightMaxZ);
    }

    public static void SortSplitEntriesLargeItems(RwKdTreeSplit split, IReadOnlyList<AABBox> entryBboxes, IList<RwEntry> entries, int startIndex, int numEntries, AABBox nodeBbox, float largeItemThreshold)
    {
        int splitAxis = (int)split.MAxis;
        float nodeSizeAxis = splitAxis switch
        {
            0 => nodeBbox.Max.X - nodeBbox.Min.X,
            1 => nodeBbox.Max.Y - nodeBbox.Min.Y,
            _ => nodeBbox.Max.Z - nodeBbox.Min.Z
        };
        float thresholdSize = nodeSizeAxis * largeItemThreshold;

        int iLeft = startIndex;
        int iRight = startIndex + numEntries - 1;
        while (iLeft <= iRight)
        {
            var bb = entryBboxes[(int)entries[iLeft].EntryIndex];
            float boxSizeAxis = split.MAxis switch
            {
                0 => bb.Max.X - bb.Min.X,
                1 => bb.Max.Y - bb.Min.Y,
                _ => bb.Max.Z - bb.Min.Z
            };
            bool swap = boxSizeAxis >= thresholdSize; // GREATER OR EQUAL
            if (swap)
            {
                (entries[iLeft], entries[iRight]) = (entries[iRight], entries[iLeft]);
                iRight--;
            }
            else
                iLeft++;
        }

        int actualLeft = iLeft - startIndex;
        int actualRight = numEntries - actualLeft;
        if (split.MNumLeft != (uint)actualLeft || split.MNumRight != (uint)actualRight)
            throw new InvalidOperationException($"Split count mismatch (large items): expected left={split.MNumLeft} right={split.MNumRight}, got left={actualLeft} right={actualRight}");
    }
}
