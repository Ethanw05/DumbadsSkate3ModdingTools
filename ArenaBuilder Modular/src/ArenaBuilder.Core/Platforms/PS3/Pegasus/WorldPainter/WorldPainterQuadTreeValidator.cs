namespace ArenaBuilder.Core.Platforms.PS3.Pegasus.WorldPainter;

/// <summary>
/// Validates quad data before writing a PSG. The engine's <c>DoQuadTreeLookup</c> always starts at <b>node index 0</b>
/// (<c>8238F988</c>): a leaf is detected when <c>(unsigned __int16)m_Children[0] == 0xFFFF</c> (same bits as <c>-1</c>).
/// <c>GetAttribData</c> stores the returned slot index in <c>__int16</c> and uses <c>8 * index</c> before matching TOC
/// (<c>82387890</c>) — see <see cref="MaxDictionarySlotCount"/>. Child indices in <c>m_Children[]</c> are also <c>int16</c>
/// (<c>8238F988</c>); the node array must not exceed <see cref="MaxQuadNodeCount"/> or the engine indexes past the allocation and crashes.
/// </summary>
public static class WorldPainterQuadTreeValidator
{
    /// <summary>
    /// <c>WorldPainter::LayerMan::GetAttribData</c> assigns <c>DoQuadTreeLookup</c>'s return value to an <c>__int16</c>
    /// then uses <c>8 * v11</c> as a byte offset into streamed dictionary data (<c>82387890</c>). Slot indices
    /// <c>&gt;= 32768</c> truncate to negative int16 values and corrupt that pointer math (access violations with
    /// non-canonical addresses). The on-disk field is <c>unsigned __int16</c>, so the builder must not emit more than
    /// <see cref="MaxDictionarySlotCount"/> slots (valid leaf indices <c>0 .. MaxDictionarySlotCount - 1</c>).
    /// </summary>
    public const int MaxDictionarySlotCount = 32768;

    /// <summary>
    /// <c>DoQuadTreeLookup</c> uses <c>signed __int16</c> child indices into <c>m_pRootNode</c> (<c>8238F988</c>).
    /// Valid non-leaf children must be in <c>0 .. MaxQuadNodeCount - 1</c>; larger indices wrap negative and crash.
    /// </summary>
    public const int MaxQuadNodeCount = 32768;

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if the tree cannot be safely consumed by Skate's traversal.
    /// </summary>
    /// <param name="nodes">WPQUAD node array in arena order (root must be index 0).</param>
    /// <param name="slotCount">WPDICT slot count for this layer.</param>
    /// <param name="layerGuidHex">Optional prefix for error messages.</param>
    public static void ValidateOrThrow(
        IReadOnlyList<WorldPainterQuadTreeDataBuilder.WorldPainterQuadNode> nodes,
        int slotCount,
        string? layerGuidHex = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        string prefix = string.IsNullOrEmpty(layerGuidHex) ? "WorldPainter WPQUAD" : $"Layer {layerGuidHex}";

        if (nodes.Count == 0)
            throw new InvalidOperationException($"{prefix}: quad tree has zero nodes (engine requires at least one).");

        if (nodes.Count > MaxQuadNodeCount)
            throw new InvalidOperationException(
                $"{prefix}: {nodes.Count} quad nodes exceed engine limit {MaxQuadNodeCount}. " +
                "DoQuadTreeLookup indexes the node array with signed int16; more nodes corrupt indices and crash.");

        if (slotCount > MaxDictionarySlotCount)
            throw new InvalidOperationException(
                $"{prefix}: WPDICT has {slotCount} slots; Skate stores the quadtree dictionary index in int16 after lookup " +
                $"(max safe slot count {MaxDictionarySlotCount}). Fewer unique (Lo,Hi) pairs per tile are required.");

        int n = nodes.Count;

        for (int i = 0; i < n; i++)
        {
            var node = nodes[i];
            bool leaf = node.Child0 == -1;

            if (leaf)
            {
                if (node.Child1 != -1 || node.Child2 != -1 || node.Child3 != -1)
                    throw new InvalidOperationException(
                        $"{prefix}: node[{i}] is a partial leaf (child0=-1 but other children are not -1).");

                // Retail tiles (e.g. DIST_University) use 0xFFFF on leaves for "void": DoQuadTreeLookup returns 65535, then
                // GetAttribData stores the result in __int16 → -1 and returns 0 without indexing WPDICT.
                if (node.DictionaryLookup == WorldPainterQuadTreeDataBuilder.InternalNodeDictionaryLookup)
                    continue;

                if (node.DictionaryLookup >= slotCount)
                    throw new InvalidOperationException(
                        $"{prefix}: leaf node[{i}] dictionary index {node.DictionaryLookup} is >= WPDICT slot count {slotCount} (GetAttribData OOB deref).");
            }
            else
            {
                if (node.DictionaryLookup != WorldPainterQuadTreeDataBuilder.InternalNodeDictionaryLookup)
                    throw new InvalidOperationException(
                        $"{prefix}: internal node[{i}] must use dictionary 0xFFFF (internal); got {node.DictionaryLookup}.");

                foreach (var (ch, idx) in new[] { (node.Child0, 0), (node.Child1, 1), (node.Child2, 2), (node.Child3, 3) })
                {
                    if (ch < 0 || ch >= n)
                        throw new InvalidOperationException(
                            $"{prefix}: internal node[{i}] child{idx}={ch} is out of range [0,{n}) (DoQuadTreeLookup OOB on m_pRootNode[child]).");
                    if (ch == i)
                        throw new InvalidOperationException(
                            $"{prefix}: internal node[{i}] child{idx} references itself — DoQuadTreeLookup would loop forever or crash.");
                }
            }
        }

        // Engine always begins traversal at node 0 (see 8238F988: v7 = 0).
        var seen = new bool[n];
        var q = new Queue<int>();
        q.Enqueue(0);
        seen[0] = true;
        while (q.Count > 0)
        {
            int i = q.Dequeue();
            var node = nodes[i];
            if (node.Child0 == -1)
                continue;
            foreach (short ch in new[] { node.Child0, node.Child1, node.Child2, node.Child3 })
            {
                if (!seen[ch])
                {
                    seen[ch] = true;
                    q.Enqueue(ch);
                }
            }
        }

        for (int i = 0; i < n; i++)
        {
            if (!seen[i])
                throw new InvalidOperationException(
                    $"{prefix}: node[{i}] is not reachable from root index 0; engine only walks from node 0, so this is a logic bug.");
        }
    }
}
