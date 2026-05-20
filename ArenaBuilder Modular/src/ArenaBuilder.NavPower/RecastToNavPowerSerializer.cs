using System.Collections.Generic;
using System.Numerics;
using DotRecast.Recast;

namespace ArenaBuilder.NavPower;

/// <summary>
/// Converts a DotRecast <see cref="RcPolyMesh"/> into the legacy NavPower v23 binary format
/// (Areas + Edges stream) matching retail Skate 3 DIST_University field values.
/// </summary>
/// <remarks>
/// Bit-layout references (bfxAreas.h):
/// <list type="bullet">
///   <item>Area flags1 – NUM_EDGES[0-6], ISLAND[7-23] = 0x1FFFF (retail null sentinel).</item>
///   <item>Area flags2 – LAYER_INDEX[11-15]=2, OB_COST_MULT[16-19]=1, STATIC_COST_MULT[20-23]=1, BASIS_VERT[24-30].</item>
///   <item>Area flags3 – GRAPH_INDEX=512 in bits[16–29] (retail pattern; SEARCH_INDEX=0).</item>
///   <item>Edge flags1 – EDGE_TYPE[15-16]=2 (NORMAL_ADJ_SMALL_HOLE), IS_FORCED_ISLAND_EDGE[17]=1.</item>
///   <item>Edge flags2 – 0 (retail uses no edge costs; BFS pathfinding).</item>
/// </list>
/// Retail pattern observed across all 105 valid DIST_University NavGraphs:
/// island=65535, dist=0, all edges NORMAL_ADJ_SMALL_HOLE|FORCED_ISLAND, layer_index=2.
/// </remarks>
internal static class RecastToNavPowerSerializer
{
    /// <summary>Optional XZ rectangle: keep only NavPower polygons whose XZ AABB intersects this box (nominal cSim tile).</summary>
    internal readonly record struct NavMeshTileCropBounds(float MinX, float MaxX, float MinZ, float MaxZ);

    private const int RC_NULL = RcRecast.RC_MESH_NULL_IDX; // 0xffff — "no index"

    // ── Edge type values from bfxAreas.h EdgeType enum ───────────────────────
    // Retail uses NORMAL_ADJ_SMALL_HOLE (2) for all edges, adjacent or boundary.
    private const uint EdgeTypeNormalAdjSmallHole = 2u;
    private const int EdgeTypeShift = 15;

    // ── FORCED_ISLAND edge flag (bfxAreas.h: IS_FORCED_ISLAND_EDGE = 0x00020000) ──
    private const uint IsForcedIslandEdge = 0x00020000u;

    // ── DEDGE_INDEX: retail initialises to INVALID_DEDGE_INDEX = all-ones (16383) in bits[18-31] ──
    private const uint InvalidDedgeIndex = 0xFFFC0000u; // (16383 << 18)

    // ── Retail edge flags1: DEDGE_INDEX(invalid) | NORMAL_ADJ_SMALL_HOLE | FORCED_ISLAND ──
    // Retail DIST_University: 0xFFFF0000 (ob_id=0) or 0xFFFF2000 (ob_id=8192); we use ob_id=0.
    private const uint RetailEdgeFlags1 =
        InvalidDedgeIndex | (EdgeTypeNormalAdjSmallHole << EdgeTypeShift) | IsForcedIslandEdge;

    // ── Area flags2 ──────────────────────────────────────────────────────────
    // Retail DIST_University: ob_cost_mult=0 and static_cost_mult=0 in the multiplier nibbles,
    // usage_count=256 (0x100) in low 10 bits, layer_index in bits[11-15].
    private const uint RetailAreaUsageCount = 0x100u;      // AREA_USAGE_COUNT low 10 bits = 256
    private const uint LayerIndex2 = 2u << 11;             // LAYER_INDEX_SHIFT = 11 (retail=2)

    // ── Island: retail uses 0xFFFF (null sentinel) for all areas ─────────────
    private const int IslandShift = 7;
    private const uint RetailIsland = 0xFFFFu; // island=65535 packed at bits[7-23]

    // ─────────────────────────────────────────────────────────────────────────

    internal static RecastNavPowerResult? Serialize(RcPolyMesh pmesh) =>
        Serialize(pmesh, null, null);

    /// <summary>
    /// Serialise Recast polygons to NavPower. When <paramref name="cropToNominalTileXZ"/> is set,
    /// each polygon is geometrically clipped to the tile bounds using Sutherland-Hodgman
    /// (same algorithm as the collision tile splitter), so no polygon spans multiple tiles.
    /// </summary>
    internal static RecastNavPowerResult? Serialize(
        RcPolyMesh pmesh,
        NavMeshTileCropBounds? cropToNominalTileXZ,
        NavPowerBuildOptions? options)
    {
        int npolys = pmesh.npolys;
        if (npolys <= 0)
            return null;

        // ── Dequantize + clip ────────────────────────────────────────────────
        var clippedPolys = new List<Vector3[]>(npolys);
        var sourcePolyIndexByKept = new List<int>(npolys);
        var sourceAdjByKept = new List<int[]>(npolys);
        var sourceEdgeCountByKept = new List<int>(npolys);
        var wasClippedByKept = new List<bool>(npolys);
        for (int i = 0; i < npolys; i++)
        {
            int ec = CountPolyVerts(pmesh, i);
            var sourceVerts = GetPolyVerts(pmesh, i, ec);
            var verts = sourceVerts;
            bool wasClipped = false;
            var sourceAdj = new int[ec];
            for (int e = 0; e < ec; e++)
            {
                int adj = GetAdjPoly(pmesh, i, e);
                sourceAdj[e] = adj == RC_NULL ? -1 : adj;
            }

            if (cropToNominalTileXZ != null)
            {
                var clipped = ClipPolyToTileXZ(verts, cropToNominalTileXZ.Value);
                if (clipped == null)
                    continue;
                wasClipped = clipped.Length != sourceVerts.Length || !ArePolysSameXZ(sourceVerts, clipped, 1e-4f);
                verts = clipped;
            }

            // Drop only truly degenerate (zero-area) polygons at this stage.
            if (verts.Length < 3 || ComputeAreaXZ(verts) < 1e-4f)
                continue;

            clippedPolys.Add(verts);
            sourcePolyIndexByKept.Add(i);
            sourceAdjByKept.Add(sourceAdj);
            sourceEdgeCountByKept.Add(ec);
            wasClippedByKept.Add(wasClipped);
        }

        // ── Island culling: drop small disconnected surface blobs ────────────
        // Thin strips on bench/railing tops form isolated components with tiny
        // total area.  Polygons that are part of a large connected surface are
        // kept even when they happen to be individually narrow.
        if (options?.MinIslandAreaSqMeters > 0f)
        {
            bool[] keepMask = ComputeIslandKeepMask(clippedPolys, options.MinIslandAreaSqMeters);
            var filtered = new List<Vector3[]>(clippedPolys.Count);
            var filteredSourceIdx = new List<int>(clippedPolys.Count);
            var filteredSourceAdj = new List<int[]>(clippedPolys.Count);
            var filteredSourceEc = new List<int>(clippedPolys.Count);
            var filteredWasClipped = new List<bool>(clippedPolys.Count);
            for (int i = 0; i < clippedPolys.Count; i++)
            {
                if (!keepMask[i])
                    continue;
                filtered.Add(clippedPolys[i]);
                filteredSourceIdx.Add(sourcePolyIndexByKept[i]);
                filteredSourceAdj.Add(sourceAdjByKept[i]);
                filteredSourceEc.Add(sourceEdgeCountByKept[i]);
                filteredWasClipped.Add(wasClippedByKept[i]);
            }
            clippedPolys = filtered;
            sourcePolyIndexByKept = filteredSourceIdx;
            sourceAdjByKept = filteredSourceAdj;
            sourceEdgeCountByKept = filteredSourceEc;
            wasClippedByKept = filteredWasClipped;
        }

        int keptCount = clippedPolys.Count;
        if (keptCount == 0)
            return null;

        // ── Pass 1: edge counts + byte offsets ───────────────────────────────
        var edgeCounts = new int[keptCount];
        var byteOffsets = new int[keptCount];
        int runningOffset = NavPowerBinaryConstants.LegacyNavGraphHeaderBytes;
        for (int ni = 0; ni < keptCount; ni++)
        {
            byteOffsets[ni] = runningOffset;
            edgeCounts[ni] = clippedPolys[ni].Length;
            runningOffset += NavPowerBinaryConstants.AreaBaseLegacyBytes
                + NavPowerBinaryConstants.EdgeBytes32 * edgeCounts[ni];
        }

        // ── Pass 2: compute geometric properties ─────────────────────────────
        var centroids = new Vector3[keptCount];
        var radii = new float[keptCount];
        var basisVerts = new int[keptCount];
        var polyVerts = new Vector3[keptCount][];

        for (int ni = 0; ni < keptCount; ni++)
        {
            var verts = clippedPolys[ni];
            polyVerts[ni] = verts;
            centroids[ni] = CalcCentroidOfConvexPolygon(verts);
            radii[ni] = CalcRadiusOfPolygon(verts, centroids[ni]);
            basisVerts[ni] = verts.Length >= 3 ? CalcBasisVert(verts) : 2;
        }

        // Geometric adjacency: shared-edge matching on the (potentially clipped) polygon vertices.
        // Match by XZ edge with a Y tolerance so clipped sloped borders still connect.
        float maxAdjEdgeEndpointYDelta = options != null
            ? MathF.Max(options.VoxelHeight * 2f, options.AgentMaxClimb + options.VoxelHeight)
            : 0.5f;
        int[][] geoNeighborByEdge = BuildGeometricNeighborPerEdge(polyVerts, edgeCounts, maxAdjEdgeEndpointYDelta);
        int[][] nativeNeighborByEdge = BuildNativeNeighborPerEdge(
            sourcePolyIndexByKept,
            sourceAdjByKept,
            sourceEdgeCountByKept,
            wasClippedByKept,
            edgeCounts);
        int[][] finalNeighborByEdge = MergeNativeAndGeometricNeighbors(nativeNeighborByEdge, geoNeighborByEdge);

        // ── Pass 3: serialise areas + edges ──────────────────────────────────
        var areasW = new BigEndianWriter();
        var prims = new List<NavPrim>(keptCount);
        var graphBBoxMin = new Vector3(float.MaxValue);
        var graphBBoxMax = new Vector3(float.MinValue);

        for (int ni = 0; ni < keptCount; ni++)
        {
            var pos = centroids[ni];
            float radius = radii[ni];
            int edgeCount = edgeCounts[ni];
            var verts = polyVerts[ni];

            var aMin = verts[0];
            var aMax = verts[0];
            for (int j = 1; j < verts.Length; j++)
            {
                aMin = Vector3.Min(aMin, verts[j]);
                aMax = Vector3.Max(aMax, verts[j]);
            }

            graphBBoxMin = Vector3.Min(graphBBoxMin, aMin);
            graphBBoxMax = Vector3.Max(graphBBoxMax, aMax);

            areasW.WriteUInt32(0);
            areasW.WriteUInt32(0);
            areasW.WriteUInt32(0);
            areasW.WriteUInt32(0);
            areasW.WriteFloat32(pos.X);
            areasW.WriteFloat32(pos.Y);
            areasW.WriteFloat32(pos.Z);
            areasW.WriteFloat32(radius);
            areasW.WriteUInt32(0);
            areasW.WriteUInt32(0);
            uint flags1 = ((uint)edgeCount & 0x7Fu) | ((RetailIsland << IslandShift) & 0x00FFFF80u);
            areasW.WriteUInt32(flags1);
            uint flags2 = RetailAreaUsageCount | LayerIndex2
                | (((uint)basisVerts[ni] << 24) & 0x7F000000u);
            areasW.WriteUInt32(flags2);
            areasW.WriteUInt32(NavPowerBinaryConstants.RetailAreaFlags3GraphIndex);

            for (int j = 0; j < edgeCount; j++)
            {
                int adjNi = finalNeighborByEdge[ni][j];
                uint adjOffset = adjNi >= 0 ? (uint)byteOffsets[adjNi] : 0u;

                areasW.WriteUInt32(adjOffset);
                var edgeStart = verts[j];
                areasW.WriteFloat32(edgeStart.X);
                areasW.WriteFloat32(edgeStart.Y);
                areasW.WriteFloat32(edgeStart.Z);
                areasW.WriteUInt32(RetailEdgeFlags1);
                areasW.WriteUInt32(0u);
            }

            prims.Add(new NavPrim
            {
                PrimOffset = byteOffsets[ni],
                Min = aMin,
                Max = aMax,
            });
        }

        var graphBBox = keptCount > 0
            ? new Box(graphBBoxMin, graphBBoxMax)
            : new Box(new Vector3(-1), new Vector3(1));

        return new RecastNavPowerResult(areasW.ToMemory(), prims, graphBBox);
    }

    // ── Sutherland-Hodgman polygon clipping (mirrors collision tile splitter) ──

    /// <summary>
    /// Clip a convex polygon to the tile XZ rectangle using Sutherland-Hodgman
    /// against the 4 half-planes. Returns null if the polygon is entirely outside.
    /// </summary>
    /// <remarks>
    /// Previous shape allocated:
    ///   • One <c>List&lt;Vector3&gt;</c> per clip pass (4 per polygon).
    ///   • Two <c>Func&lt;&gt;</c> closures per pass (capture of <c>c.MinX</c> etc.).
    ///   • Final <c>poly.ToArray()</c>.
    /// On a global navmesh with ~10–20k polygons that's ~80k allocations per
    /// tile just for the clipper. Hand-rolled, per-plane clip routines ping-
    /// pong between two stack-allocated <see cref="Span{T}"/> buffers — zero
    /// heap allocation along the clip path; the only allocation is the
    /// final output array (which we still have to return).
    /// </remarks>
    private static Vector3[]? ClipPolyToTileXZ(Vector3[] verts, NavMeshTileCropBounds c)
    {
        // Sutherland-Hodgman against a convex region (4 planes) bounds the
        // output at input + 4 verts in the pathological case. Skate navmesh
        // polys come out of Recast as quads or hexes; 32 covers every real
        // input with margin.
        const int MaxVerts = 32;
        if (verts.Length == 0 || verts.Length > MaxVerts - 4)
            return null;

        Span<Vector3> bufA = stackalloc Vector3[MaxVerts];
        Span<Vector3> bufB = stackalloc Vector3[MaxVerts];
        verts.AsSpan().CopyTo(bufA);
        int count = verts.Length;

        count = ClipMinX(bufA, count, bufB, c.MinX); if (count < 3) return null;
        count = ClipMaxX(bufB, count, bufA, c.MaxX); if (count < 3) return null;
        count = ClipMinZ(bufA, count, bufB, c.MinZ); if (count < 3) return null;
        count = ClipMaxZ(bufB, count, bufA, c.MaxZ); if (count < 3) return null;

        return bufA.Slice(0, count).ToArray();
    }

    // Four plane-specific clip passes. Each reads from `src` (containing
    // `count` verts) and writes the clipped result into `dst`, returning the
    // new vert count. Inlined inside checks (`v.X >= xPlane` etc.) eliminate
    // the delegate-invoke overhead of the generic `ClipPolyHalfPlane`.
    private static int ClipMinX(ReadOnlySpan<Vector3> src, int count, Span<Vector3> dst, float xPlane)
    {
        int outN = 0;
        Vector3 prev = src[count - 1];
        bool prevIn = prev.X >= xPlane;
        for (int i = 0; i < count; i++)
        {
            Vector3 cur = src[i];
            bool curIn = cur.X >= xPlane;
            if (curIn)
            {
                if (!prevIn) dst[outN++] = LerpAtX(prev, cur, xPlane);
                dst[outN++] = cur;
            }
            else if (prevIn)
            {
                dst[outN++] = LerpAtX(prev, cur, xPlane);
            }
            prev = cur; prevIn = curIn;
        }
        return outN;
    }

    private static int ClipMaxX(ReadOnlySpan<Vector3> src, int count, Span<Vector3> dst, float xPlane)
    {
        int outN = 0;
        Vector3 prev = src[count - 1];
        bool prevIn = prev.X <= xPlane;
        for (int i = 0; i < count; i++)
        {
            Vector3 cur = src[i];
            bool curIn = cur.X <= xPlane;
            if (curIn)
            {
                if (!prevIn) dst[outN++] = LerpAtX(prev, cur, xPlane);
                dst[outN++] = cur;
            }
            else if (prevIn)
            {
                dst[outN++] = LerpAtX(prev, cur, xPlane);
            }
            prev = cur; prevIn = curIn;
        }
        return outN;
    }

    private static int ClipMinZ(ReadOnlySpan<Vector3> src, int count, Span<Vector3> dst, float zPlane)
    {
        int outN = 0;
        Vector3 prev = src[count - 1];
        bool prevIn = prev.Z >= zPlane;
        for (int i = 0; i < count; i++)
        {
            Vector3 cur = src[i];
            bool curIn = cur.Z >= zPlane;
            if (curIn)
            {
                if (!prevIn) dst[outN++] = LerpAtZ(prev, cur, zPlane);
                dst[outN++] = cur;
            }
            else if (prevIn)
            {
                dst[outN++] = LerpAtZ(prev, cur, zPlane);
            }
            prev = cur; prevIn = curIn;
        }
        return outN;
    }

    private static int ClipMaxZ(ReadOnlySpan<Vector3> src, int count, Span<Vector3> dst, float zPlane)
    {
        int outN = 0;
        Vector3 prev = src[count - 1];
        bool prevIn = prev.Z <= zPlane;
        for (int i = 0; i < count; i++)
        {
            Vector3 cur = src[i];
            bool curIn = cur.Z <= zPlane;
            if (curIn)
            {
                if (!prevIn) dst[outN++] = LerpAtZ(prev, cur, zPlane);
                dst[outN++] = cur;
            }
            else if (prevIn)
            {
                dst[outN++] = LerpAtZ(prev, cur, zPlane);
            }
            prev = cur; prevIn = curIn;
        }
        return outN;
    }

    private static Vector3 LerpAtX(Vector3 a, Vector3 b, float xPlane)
    {
        float denom = b.X - a.X;
        float t = MathF.Abs(denom) < 1e-8f ? 0f : (xPlane - a.X) / denom;
        t = Math.Clamp(t, 0f, 1f);
        return Vector3.Lerp(a, b, t);
    }

    private static Vector3 LerpAtZ(Vector3 a, Vector3 b, float zPlane)
    {
        float denom = b.Z - a.Z;
        float t = MathF.Abs(denom) < 1e-8f ? 0f : (zPlane - a.Z) / denom;
        t = Math.Clamp(t, 0f, 1f);
        return Vector3.Lerp(a, b, t);
    }

    /// <summary>
    /// Computes a keep mask for <paramref name="allPolys"/> based on connected-component (island)
    /// total area.  Polygons whose component's summed XZ area is below
    /// <paramref name="minIslandAreaSqMeters"/> are marked <c>false</c>.
    ///
    /// This correctly preserves individually-narrow polygons that are part of a large connected
    /// surface (e.g. corridor tiles), while removing small isolated blobs such as railing tops,
    /// bench surfaces and ledge strips that are not reachable from the main walkable area.
    /// </summary>
    internal static bool[] ComputeIslandKeepMask(
        IReadOnlyList<Vector3[]> allPolys,
        float minIslandAreaSqMeters)
    {
        int total = allPolys.Count;
        var result = new bool[total];

        // Build compact index over non-degenerate polys only.
        var validIdx = new List<int>(total);
        for (int i = 0; i < total; i++)
            if (allPolys[i].Length >= 3) validIdx.Add(i);

        int n = validIdx.Count;
        if (n == 0) return result;

        if (minIslandAreaSqMeters <= 0f)
        {
            foreach (int i in validIdx) result[i] = true;
            return result;
        }

        var polyArr = new Vector3[n][];
        var edgeCounts = new int[n];
        for (int k = 0; k < n; k++)
        {
            polyArr[k] = allPolys[validIdx[k]];
            edgeCounts[k] = polyArr[k].Length;
        }

        int[][] adj = BuildGeometricNeighborPerEdge(polyArr, edgeCounts, maxEndpointYDelta: 0.5f);

        // BFS flood-fill to find connected components.
        int[] compId = new int[n];
        Array.Fill(compId, -1);
        int numComp = 0;
        for (int start = 0; start < n; start++)
        {
            if (compId[start] >= 0) continue;
            var queue = new Queue<int>();
            queue.Enqueue(start);
            compId[start] = numComp;
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                for (int e = 0; e < edgeCounts[cur]; e++)
                {
                    int nb = adj[cur][e];
                    if (nb >= 0 && compId[nb] < 0)
                    {
                        compId[nb] = numComp;
                        queue.Enqueue(nb);
                    }
                }
            }
            numComp++;
        }

        // Sum total XZ area per component.
        var compArea = new float[numComp];
        for (int k = 0; k < n; k++)
            compArea[compId[k]] += ComputeAreaXZ(polyArr[k]);

        // Keep polys whose component meets the minimum area threshold.
        for (int k = 0; k < n; k++)
            result[validIdx[k]] = compArea[compId[k]] >= minIslandAreaSqMeters;

        return result;
    }

    private static float ComputeAreaXZ(IReadOnlyList<Vector3> verts)
    {
        double sum = 0.0;
        int n = verts.Count;
        for (int i = 0; i < n; i++)
        {
            var a = verts[i];
            var b = verts[(i + 1) % n];
            sum += (double)a.X * b.Z - (double)b.X * a.Z;
        }

        return (float)(Math.Abs(sum) * 0.5);
    }

    private static bool ArePolysSameXZ(IReadOnlyList<Vector3> a, IReadOnlyList<Vector3> b, float eps)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (MathF.Abs(a[i].X - b[i].X) > eps || MathF.Abs(a[i].Z - b[i].Z) > eps)
                return false;
        }
        return true;
    }

    private static int[][] BuildNativeNeighborPerEdge(
        IReadOnlyList<int> sourcePolyIndexByKept,
        IReadOnlyList<int[]> sourceAdjByKept,
        IReadOnlyList<int> sourceEdgeCountByKept,
        IReadOnlyList<bool> wasClippedByKept,
        int[] edgeCounts)
    {
        int n = sourcePolyIndexByKept.Count;
        var native = new int[n][];
        var keptBySource = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++)
            keptBySource[sourcePolyIndexByKept[i]] = i;

        for (int i = 0; i < n; i++)
        {
            int ec = edgeCounts[i];
            native[i] = new int[ec];
            for (int e = 0; e < ec; e++)
                native[i][e] = -1;

            // Edge mapping is only reliable when clipping preserved polygon edge ring 1:1.
            if (wasClippedByKept[i] || sourceEdgeCountByKept[i] != ec)
                continue;

            var srcAdj = sourceAdjByKept[i];
            for (int e = 0; e < ec && e < srcAdj.Length; e++)
            {
                int adjSrc = srcAdj[e];
                if (adjSrc < 0 || !keptBySource.TryGetValue(adjSrc, out int adjKept))
                    continue;
                native[i][e] = adjKept;
            }
        }

        return native;
    }

    private static int[][] MergeNativeAndGeometricNeighbors(int[][] native, int[][] geo)
    {
        int n = native.Length;
        var merged = new int[n][];
        for (int i = 0; i < n; i++)
        {
            int ec = native[i].Length;
            merged[i] = new int[ec];
            for (int e = 0; e < ec; e++)
            {
                int nAdj = native[i][e];
                merged[i][e] = nAdj >= 0 ? nAdj : geo[i][e];
            }
        }
        return merged;
    }

    /// <summary>
    /// For each directed edge of each kept polygon, finds the other kept polygon that shares the same
    /// undirected edge (quantized vertices). Returns neighbor index in kept space, or -1.
    /// </summary>
    private static int[][] BuildGeometricNeighborPerEdge(
        Vector3[][] polyVerts,
        int[] edgeCounts,
        float maxEndpointYDelta)
    {
        int n = polyVerts.Length;
        var result = new int[n][];
        for (int i = 0; i < n; i++)
        {
            int ec = edgeCounts[i];
            result[i] = new int[ec];
            for (int j = 0; j < ec; j++)
                result[i][j] = -1;
        }

        const float quantScale = 1000f;
        static long Q(float f) => (long)MathF.Round(f * quantScale);

        var edgeMap = new Dictionary<(long, long, long, long), List<(int pi, int ei, Vector3 A, Vector3 B)>>();

        for (int pi = 0; pi < n; pi++)
        {
            var v = polyVerts[pi];
            int ec = edgeCounts[pi];
            for (int ei = 0; ei < ec; ei++)
            {
                var va = v[ei];
                var vb = v[(ei + 1) % ec];
                long ax = Q(va.X), az = Q(va.Z);
                long bx = Q(vb.X), bz = Q(vb.Z);
                if (ax > bx || (ax == bx && az > bz))
                    (ax, az, bx, bz) = (bx, bz, ax, az);

                var key = (ax, az, bx, bz);
                if (!edgeMap.TryGetValue(key, out var list))
                {
                    list = new List<(int, int, Vector3, Vector3)>(2);
                    edgeMap[key] = list;
                }

                list.Add((pi, ei, va, vb));
            }
        }

        foreach (var list in edgeMap.Values)
        {
            if (list.Count < 2)
                continue;

            var candidates = new List<(int A, int B, float Score)>();
            for (int i = 0; i < list.Count; i++)
            {
                for (int j = i + 1; j < list.Count; j++)
                {
                    var li = list[i];
                    var lj = list[j];
                    if (li.pi == lj.pi)
                        continue;

                    float sameDir = MathF.Max(MathF.Abs(li.A.Y - lj.A.Y), MathF.Abs(li.B.Y - lj.B.Y));
                    float revDir = MathF.Max(MathF.Abs(li.A.Y - lj.B.Y), MathF.Abs(li.B.Y - lj.A.Y));
                    float yDelta = MathF.Min(sameDir, revDir);
                    if (yDelta > maxEndpointYDelta)
                        continue;
                    candidates.Add((i, j, yDelta));
                }
            }
            if (candidates.Count == 0)
                continue;

            candidates.Sort((a, b) => a.Score.CompareTo(b.Score));
            var used = new bool[list.Count];
            foreach (var c in candidates)
            {
                if (used[c.A] || used[c.B])
                    continue;
                var a = list[c.A];
                var b = list[c.B];
                if (result[a.pi][a.ei] >= 0 || result[b.pi][b.ei] >= 0)
                    continue;

                result[a.pi][a.ei] = b.pi;
                result[b.pi][b.ei] = a.pi;
                used[c.A] = true;
                used[c.B] = true;
            }
        }

        return result;
    }

    // ── Geometry helpers ──────────────────────────────────────────────────────

    private static int CountPolyVerts(RcPolyMesh pmesh, int polyIdx)
    {
        int base_ = polyIdx * 2 * pmesh.nvp;
        int count = 0;
        for (int j = 0; j < pmesh.nvp; j++)
        {
            if (pmesh.polys[base_ + j] == RC_NULL)
                break;
            count++;
        }
        return Math.Max(count, 3);
    }

    private static int GetAdjPoly(RcPolyMesh pmesh, int polyIdx, int edgeIdx)
    {
        int base_ = polyIdx * 2 * pmesh.nvp;
        int raw = pmesh.polys[base_ + pmesh.nvp + edgeIdx];
        // Bit 0x8000 is a Recast "portal" flag (tile boundary side); treat as no neighbor.
        if ((raw & 0x8000) != 0)
            return RC_NULL;
        return raw; // polygon index 0..npolys-1, or RC_NULL (0xffff)
    }

    /// <summary>Dequantize and return the world-space vertices of polygon <paramref name="polyIdx"/>.</summary>
    private static Vector3[] GetPolyVerts(RcPolyMesh pmesh, int polyIdx, int edgeCount)
    {
        int base_ = polyIdx * 2 * pmesh.nvp;
        var result = new Vector3[edgeCount];
        for (int j = 0; j < edgeCount; j++)
        {
            int vi = pmesh.polys[base_ + j];
            if (vi == RC_NULL)
            {
                // Degenerate — repeat last valid vertex to keep count
                result[j] = j > 0 ? result[j - 1] : Vector3.Zero;
                continue;
            }

            result[j] = new Vector3(
                pmesh.bmin.X + pmesh.verts[vi * 3 + 0] * pmesh.cs,
                pmesh.bmin.Y + pmesh.verts[vi * 3 + 1] * pmesh.ch,
                pmesh.bmin.Z + pmesh.verts[vi * 3 + 2] * pmesh.cs);
        }

        return result;
    }

    // ── Port of bfxCollider.cpp CalcCentroidOfConvexPolygon ──────────────────
    private static Vector3 CalcCentroidOfConvexPolygon(Vector3[] verts)
    {
        if (verts.Length == 0)
            return Vector3.Zero;

        var v0 = verts[0];
        var areaWeightedSum = Vector3.Zero;
        float areaSum = 0f;

        for (int i = 1; i < verts.Length - 1; i++)
        {
            var start = verts[i];
            var end = verts[i + 1];
            var triCentroid = (v0 + start + end) / 3f;
            float area2 = Vector3.Cross(start - v0, end - v0).Length();
            areaWeightedSum += triCentroid * area2;
            areaSum += area2;
        }

        if (areaSum > 1e-10f)
            return areaWeightedSum / areaSum;

        // Degenerate: arithmetic mean
        var sum = Vector3.Zero;
        foreach (var v in verts) sum += v;
        return sum / verts.Length;
    }

    // ── Port of bfxCollider.cpp CalcRadiusOfPolygon ──────────────────────────
    private static float CalcRadiusOfPolygon(Vector3[] verts, Vector3 centroid)
    {
        float maxDistSq = 0f;
        foreach (var v in verts)
        {
            float dSq = Vector3.DistanceSquared(v, centroid);
            if (dSq > maxDistSq) maxDistSq = dSq;
        }

        return MathF.Sqrt(maxDistSq);
    }

    // ── Port of bfxCollider.cpp CalcBasisVert ────────────────────────────────
    /// <summary>
    /// Finds the vertex (index >= 2) farthest from the line through vertex[0]→vertex[1].
    /// Used by NavPower to reconstruct the polygon normal from the binary data.
    /// </summary>
    private static int CalcBasisVert(Vector3[] verts)
    {
        if (verts.Length < 3)
            return 2;

        var e0 = verts[0];
        var edgeDir = Vector3.Normalize(verts[1] - e0);
        float maxDistSq = float.MinValue;
        int basis = 2;

        for (int i = 2; i < verts.Length; i++)
        {
            var toVert = verts[i] - e0;
            float along = Vector3.Dot(toVert, edgeDir);
            var closest = e0 + along * edgeDir;
            float dSq = Vector3.DistanceSquared(verts[i], closest);
            if (dSq > maxDistSq)
            {
                maxDistSq = dSq;
                basis = i;
            }
        }

        return basis;
    }

}

/// <summary>Result of <see cref="RecastToNavPowerSerializer.Serialize"/>.</summary>
internal readonly struct RecastNavPowerResult
{
    internal readonly ReadOnlyMemory<byte> AreaBytes;
    internal readonly IReadOnlyList<NavPrim> Prims;
    internal readonly Box GraphBBox;

    internal RecastNavPowerResult(
        ReadOnlyMemory<byte> areaBytes,
        IReadOnlyList<NavPrim> prims,
        Box graphBBox)
    {
        AreaBytes = areaBytes;
        Prims = prims;
        GraphBBox = graphBBox;
    }
}
