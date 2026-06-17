using ArenaBuilder.Core.Platforms.Common.Pegasus.WorldPainter;

namespace ArenaBuilder.WorldPainter;

/// <summary>
/// Builds a WPQUAD node list and WPDICT slot list directly from a paint grid.
/// <para>
/// No sampling offsets, no mirror transforms, no dominant-key approximations.
/// Cell boundaries drive subdivision: each leaf covers exactly one paint cell
/// or a uniformly-coloured region.  Root is placed at index 0 (BFS order) so
/// no post-build reorder is required.
/// </para>
/// </summary>
public static class WorldPainterCellQuadTreeBuilder
{
    private readonly record struct PendingNode(double X0, double X1, double Z0, double Z1, int NodeIdx, int Depth);

    /// <summary>
    /// The engine indexes WPQUAD children with signed int16; see <see cref="WorldPainterQuadTreeValidator.MaxQuadNodeCount"/>.
    /// We cap subdivision depth so even a fully-split quadtree cannot exceed that limit.
    /// </summary>
    private static readonly int MaxSafeDepth = ComputeMaxSafeDepth(WorldPainterQuadTreeValidator.MaxQuadNodeCount);

    private static int ComputeMaxSafeDepth(int maxNodes)
    {
        // Full quadtree node count for depth d (root depth=0) is (4^(d+1)-1)/3.
        // Find largest d such that count <= maxNodes.
        if (maxNodes <= 0) return 0;
        int d = 0;
        long nodes = 1;
        while (true)
        {
            // next depth adds 4^(d+1) nodes at level d+1.
            long nextLevel = 1L << (2 * (d + 1)); // 4^(d+1)
            long nextTotal = nodes + nextLevel;
            if (nextTotal > maxNodes)
                return d;
            nodes = nextTotal;
            d++;
        }
    }

    /// <summary>
    /// Build a quadtree for one WorldPainter 128 m tile.
    /// </summary>
    /// <param name="cols">Grid columns (X axis, col 0 = min X).</param>
    /// <param name="rows">Grid rows (Z axis, row 0 = min Z = south).</param>
    /// <param name="minX">World X of the west edge of the paint grid.</param>
    /// <param name="minZ">World Z of the south edge of the paint grid.</param>
    /// <param name="maxX">World X of the east edge of the paint grid.</param>
    /// <param name="maxZ">World Z of the north edge of the paint grid.</param>
    /// <param name="cells">Row-major cell values: index = row * cols + col.</param>
    /// <param name="tileMinX">WP tile west bound (typically wpRootCenter.X - 64).</param>
    /// <param name="tileMaxX">WP tile east bound (typically wpRootCenter.X + 64).</param>
    /// <param name="tileMinZ">WP tile south bound.</param>
    /// <param name="tileMaxZ">WP tile north bound.</param>
    /// <returns>
    /// Null when the tile contains zero painted cells (caller should skip this layer).
    /// </returns>
    public static (IReadOnlyList<WorldPainterQuadTreeDataBuilder.WorldPainterQuadNode> Nodes,
                   IReadOnlyList<IReadOnlyList<uint>> Slots)?
        Build(int cols, int rows,
              double minX, double minZ, double maxX, double maxZ,
              WpCell[] cells,
              double tileMinX, double tileMaxX, double tileMinZ, double tileMaxZ)
    {
        if (cols <= 0 || rows <= 0 || cells == null || cells.Length < cols * rows)
            return null;
        if (maxX <= minX || maxZ <= minZ)
            return null;

        double cellW = (maxX - minX) / cols;
        double cellH = (maxZ - minZ) / rows;

        if (!HasAnyPaint(cols, rows, minX, minZ, cellW, cellH, cells,
                         tileMinX, tileMaxX, tileMinZ, tileMaxZ))
            return null;

        var nodes = new List<WorldPainterQuadTreeDataBuilder.WorldPainterQuadNode>(64);
        var slotByKey = new Dictionary<(uint Lo, uint Hi), ushort>();
        var slots = new List<IReadOnlyList<uint>>();

        // Reserve index 0 for the root.
        nodes.Add(default);

        var queue = new Queue<PendingNode>();
        queue.Enqueue(new PendingNode(tileMinX, tileMaxX, tileMinZ, tileMaxZ, 0, 0));

        while (queue.Count > 0)
        {
            var (x0, x1, z0, z1, nodeIdx, depth) = queue.Dequeue();
            ProcessNode(x0, x1, z0, z1, nodeIdx, depth,
                        cols, rows, minX, minZ, cellW, cellH, cells,
                        nodes, queue, slotByKey, slots);
        }

        // If everything resolved to void leaves there are no real slots — skip the layer.
        if (slots.Count == 0)
            return null;

        return (nodes, slots);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool HasAnyPaint(
        int cols, int rows,
        double minX, double minZ, double cellW, double cellH,
        WpCell[] cells,
        double tileMinX, double tileMaxX, double tileMinZ, double tileMaxZ)
    {
        int cMin = Math.Max(0, (int)Math.Floor((tileMinX - minX) / cellW));
        int cMax = Math.Min(cols - 1, (int)Math.Floor((tileMaxX - minX - 1e-9) / cellW));
        int rMin = Math.Max(0, (int)Math.Floor((tileMinZ - minZ) / cellH));
        int rMax = Math.Min(rows - 1, (int)Math.Floor((tileMaxZ - minZ - 1e-9) / cellH));
        for (int r = rMin; r <= rMax; r++)
            for (int c = cMin; c <= cMax; c++)
                if (!cells[r * cols + c].IsEmpty) return true;
        return false;
    }

    private static void ProcessNode(
        double x0, double x1, double z0, double z1,
        int nodeIdx, int depth,
        int cols, int rows,
        double minX, double minZ, double cellW, double cellH,
        WpCell[] cells,
        List<WorldPainterQuadTreeDataBuilder.WorldPainterQuadNode> nodes,
        Queue<PendingNode> queue,
        Dictionary<(uint, uint), ushort> slotByKey,
        List<IReadOnlyList<uint>> slots)
    {
        // Raw (possibly out-of-range) cell indices for this region.
        int rawCMin = (int)Math.Floor((x0 - minX) / cellW);
        int rawCMax = (int)Math.Floor((x1 - minX - 1e-9) / cellW);
        int rawRMin = (int)Math.Floor((z0 - minZ) / cellH);
        int rawRMax = (int)Math.Floor((z1 - minZ - 1e-9) / cellH);

        // Region is entirely outside the paint map → void leaf.
        if (rawCMax < 0 || rawCMin >= cols || rawRMax < 0 || rawRMin >= rows)
        {
            nodes[nodeIdx] = WorldPainterQuadTreeDataBuilder.WorldPainterQuadNode.Leaf(0xFFFF);
            return;
        }

        int cMin = Math.Max(0, rawCMin);
        int cMax = Math.Min(cols - 1, rawCMax);
        int rMin = Math.Max(0, rawRMin);
        int rMax = Math.Min(rows - 1, rawRMax);

        // Straddles the paint-map border → implicit void in the outside portion.
        bool straddles = rawCMin < 0 || rawCMax >= cols || rawRMin < 0 || rawRMax >= rows;

        // Collect unique non-void values and track the most frequent (dominant).
        var uniqueVals = new Dictionary<WpCell, int>(4);
        int emptyCount = straddles ? 1 : 0;
        WpCell dominant = default;
        int dominantCount = 0;

        for (int r = rMin; r <= rMax; r++)
        {
            for (int c = cMin; c <= cMax; c++)
            {
                var cell = cells[r * cols + c];
                if (cell.IsEmpty)
                {
                    emptyCount++;
                }
                else
                {
                    uniqueVals.TryGetValue(cell, out int cnt);
                    int newCnt = cnt + 1;
                    uniqueVals[cell] = newCnt;
                    if (newCnt > dominantCount)
                    {
                        dominantCount = newCnt;
                        dominant = cell;
                    }
                }
            }
        }

        // Conditions that force a leaf:
        //   • all void
        //   • single uniform value across all cells with no void mixed in
        //   • region covers at most one cell (no finer subdivision possible)
        //   • max depth safety limit (engine int16 node index constraint)
        bool isSingleCell = cMin == cMax && rMin == rMax && !straddles;
        bool isLeaf = uniqueVals.Count == 0
                   || (uniqueVals.Count == 1 && emptyCount == 0)
                   || isSingleCell
                   || depth >= MaxSafeDepth;

        if (isLeaf)
        {
            if (uniqueVals.Count == 0)
                nodes[nodeIdx] = WorldPainterQuadTreeDataBuilder.WorldPainterQuadNode.Leaf(0xFFFF);
            else
                nodes[nodeIdx] = WorldPainterQuadTreeDataBuilder.WorldPainterQuadNode.Leaf(
                    EnsureSlot(dominant, slotByKey, slots));
            return;
        }

        // Split at midpoints and enqueue four quadrants.
        // Engine child index ordering is SW, NW, SE, NE (not SW, SE, NW, NE).
        // Using the wrong order transposes X/Z at runtime (looks like 90deg+mirror in world space).
        double mx = (x0 + x1) * 0.5;
        double mz = (z0 + z1) * 0.5;

        int c0 = nodes.Count; nodes.Add(default);
        int c1 = nodes.Count; nodes.Add(default);
        int c2 = nodes.Count; nodes.Add(default);
        int c3 = nodes.Count; nodes.Add(default);

        nodes[nodeIdx] = new WorldPainterQuadTreeDataBuilder.WorldPainterQuadNode(
            (short)c0, (short)c1, (short)c2, (short)c3,
            WorldPainterQuadTreeDataBuilder.InternalNodeDictionaryLookup);

        queue.Enqueue(new PendingNode(x0, mx, z0, mz, c0, depth + 1)); // SW
        queue.Enqueue(new PendingNode(x0, mx, mz, z1, c1, depth + 1)); // NW
        queue.Enqueue(new PendingNode(mx, x1, z0, mz, c2, depth + 1)); // SE
        queue.Enqueue(new PendingNode(mx, x1, mz, z1, c3, depth + 1)); // NE
    }

    private static ushort EnsureSlot(
        WpCell cell,
        Dictionary<(uint, uint), ushort> slotByKey,
        List<IReadOnlyList<uint>> slots)
    {
        var key = (cell.Lo, cell.Hi);
        if (slotByKey.TryGetValue(key, out ushort idx))
            return idx;
        if (slots.Count >= WorldPainterQuadTreeValidator.MaxDictionarySlotCount)
            throw new InvalidOperationException(
                $"WorldPainter: too many unique (Lo,Hi) keys in one tile " +
                $"(limit {WorldPainterQuadTreeValidator.MaxDictionarySlotCount}).");
        idx = (ushort)slots.Count;
        slotByKey[key] = idx;
        slots.Add(new uint[] { cell.Lo, cell.Hi });
        return idx;
    }
}
