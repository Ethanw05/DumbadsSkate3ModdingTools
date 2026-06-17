using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Recast;

namespace ArenaBuilder.NavPower;

/// <summary>
/// Runs the full DotRecast voxelisation pipeline on tile collision geometry and returns an
/// <see cref="RcPolyMesh"/> with real convex polygon connectivity.
/// <para>
/// Coordinate system: the ArenaBuilder world uses <b>Y-up</b> (X/Z are the floor plane), which
/// matches what Recast expects. The NavPower header field <c>m_buildUpAxis = 0 = X_UP</c> is
/// written verbatim to match retail Skate 3 tiles and is independent of the C# coordinate layout.
/// No axis swap is needed.
/// </para>
/// </summary>
internal static class NavPowerRecastMeshBuilder
{
    /// <summary>
    /// Build a polygon mesh from collision triangles clipped to the given tile bounds.
    /// </summary>
    /// <param name="dumpObjPrefix">
    /// Optional file path prefix for diagnostic OBJ exports. When set, writes
    /// <c>{prefix}_input.obj</c> (collision triangles) and <c>{prefix}_navmesh.obj</c>
    /// (resulting nav polygons) so they can be inspected in Blender / MeshLab.
    /// </param>
    /// <returns>
    /// The polygon mesh, or <see langword="null"/> if Recast produced zero polygons
    /// (e.g. no walkable geometry in this tile).
    /// </returns>
    internal static RcPolyMesh? Build(
        IReadOnlyList<Vector3> verts,
        IReadOnlyList<(int A, int B, int C)> faces,
        float tileMinX,
        float tileMaxX,
        float tileMinZ,
        float tileMaxZ,
        NavPowerBuildOptions options,
        string? dumpObjPrefix = null)
    {
        if (faces.Count == 0)
            return null;

        // ── Build flat float[] vertex buffer (x,y,z per vertex) ─────────────
        var flatVerts = new float[verts.Count * 3];
        float yMin = float.MaxValue, yMax = float.MinValue;
        for (int i = 0; i < verts.Count; i++)
        {
            var v = verts[i];
            flatVerts[i * 3 + 0] = v.X;
            flatVerts[i * 3 + 1] = v.Y;
            flatVerts[i * 3 + 2] = v.Z;
            if (v.Y < yMin) yMin = v.Y;
            if (v.Y > yMax) yMax = v.Y;
        }

        if (yMin > yMax) { yMin = -1f; yMax = 1f; }
        float yPad = Math.Max((yMax - yMin) * 0.1f, options.AgentHeight);
        yMin -= yPad;
        yMax += yPad;

        // ── Build flat int[] index buffer (a,b,c per tri) ────────────────────
        var flatTris = new int[faces.Count * 3];
        for (int i = 0; i < faces.Count; i++)
        {
            flatTris[i * 3 + 0] = faces[i].A;
            flatTris[i * 3 + 1] = faces[i].B;
            flatTris[i * 3 + 2] = faces[i].C;
        }

        if (dumpObjPrefix != null)
            DumpInputObj(dumpObjPrefix + "_input.obj", verts, faces);

        var bmin = new RcVec3f(tileMinX, yMin, tileMinZ);
        var bmax = new RcVec3f(tileMaxX, yMax, tileMaxZ);

        var geom = new NavPowerRecastGeomProvider(flatVerts, flatTris, bmin, bmax);
        RcBuilderResult? result = null;
        Exception? watershedError = null;
        foreach (RcPartition partition in new[] { RcPartition.WATERSHED, RcPartition.MONOTONE })
        {
            try
            {
                var cfg = new RcConfig(
                    partition,
                    options.VoxelSize,
                    options.VoxelHeight,
                    options.AgentMaxSlope,
                    options.AgentHeight,
                    options.AgentRadius,
                    options.AgentMaxClimb,
                    options.RegionMinSize,
                    options.RegionMergeSize,
                    options.EdgeMaxLen,
                    options.EdgeMaxError,
                    options.MaxVertsPerPoly,
                    detailSampleDist: options.DetailSampleDist,
                    detailSampleMaxError: options.DetailSampleMaxError,
                    filterLowHangingObstacles: options.FilterLowHangingObstacles,
                    filterLedgeSpans: options.FilterLedgeSpans,
                    filterWalkableLowHeightSpans: options.FilterWalkableLowHeightSpans,
                    walkableAreaMod: new RcAreaModification(RcRecast.RC_WALKABLE_AREA),
                    // Detail mesh can improve fidelity but may exceed DotRecast per-poly detail tri cap (255) on large globals.
                    buildMeshDetail: options.BuildMeshDetail);

                var builderCfg = new RcBuilderConfig(cfg, bmin, bmax);
                var ctx = new RcContext();
                RcHeightfield solid = NavPowerRecastVoxelization.BuildSolidHeightfield(ctx, geom, builderCfg, options);
                var builder = new RcBuilder();
                result = builder.Build(ctx, builderCfg.tileX, builderCfg.tileZ, geom, builderCfg.cfg, solid, keepInterResults: false);
                if (result.Mesh != null && result.Mesh.npolys > 0)
                    break;
            }
            catch (Exception ex) when (partition == RcPartition.WATERSHED)
            {
                // Watershed usually produces cleaner stairs/ledges; keep monotone as robust fallback.
                watershedError = ex;
            }
        }

        if (result == null && watershedError != null)
            throw watershedError;

        if (result == null || result.Mesh == null || result.Mesh.npolys == 0)
            return null;

        if (dumpObjPrefix != null)
            DumpNavMeshObj(dumpObjPrefix + "_navmesh.obj", result.Mesh, options);

        return result.Mesh;
    }

    // ── Diagnostic OBJ helpers ────────────────────────────────────────────────

    /// <summary>Writes the raw collision triangle soup to a Wavefront OBJ file.</summary>
    private static void DumpInputObj(string path, IReadOnlyList<Vector3> verts, IReadOnlyList<(int A, int B, int C)> faces)
    {
        using var sw = new System.IO.StreamWriter(path, append: false, encoding: System.Text.Encoding.UTF8);
        sw.WriteLine("# NavPower collision input mesh");
        foreach (var v in verts)
            sw.WriteLine(FormattableString.Invariant($"v {v.X:F6} {v.Y:F6} {v.Z:F6}"));
        foreach (var (a, b, c) in faces)
            sw.WriteLine(FormattableString.Invariant($"f {a + 1} {b + 1} {c + 1}"));
    }

    /// <summary>
    /// Writes the Recast <see cref="RcPolyMesh"/> nav polygons (dequantized to world space)
    /// to a Wavefront OBJ file. Each polygon is fan-triangulated.
    /// </summary>
    private static void DumpNavMeshObj(string path, RcPolyMesh pmesh, NavPowerBuildOptions options)
    {
        const int RC_NULL = RcRecast.RC_MESH_NULL_IDX;

        // Extract world-space polygon arrays and parallel OBJ 1-based vertex-index lists.
        int npolys = pmesh.npolys;
        var worldPolys = new System.Collections.Generic.List<Vector3[]>(npolys);
        var faceViLists = new System.Collections.Generic.List<System.Collections.Generic.List<int>>(npolys);

        for (int pi = 0; pi < npolys; pi++)
        {
            int baseIdx = pi * 2 * pmesh.nvp;
            var wverts = new System.Collections.Generic.List<Vector3>(pmesh.nvp);
            var viList  = new System.Collections.Generic.List<int>(pmesh.nvp);
            for (int j = 0; j < pmesh.nvp; j++)
            {
                int vi = pmesh.polys[baseIdx + j];
                if (vi == RC_NULL) break;
                wverts.Add(new Vector3(
                    pmesh.bmin.X + pmesh.verts[vi * 3 + 0] * pmesh.cs,
                    pmesh.bmin.Y + pmesh.verts[vi * 3 + 1] * pmesh.ch,
                    pmesh.bmin.Z + pmesh.verts[vi * 3 + 2] * pmesh.cs));
                viList.Add(vi + 1);
            }
            worldPolys.Add(wverts.Count >= 3 ? wverts.ToArray() : System.Array.Empty<Vector3>());
            faceViLists.Add(viList);
        }

        bool[] keep = RecastToNavPowerSerializer.ComputeIslandKeepMask(worldPolys, options.MinIslandAreaSqMeters);

        using var sw = new System.IO.StreamWriter(path, append: false, encoding: System.Text.Encoding.UTF8);
        sw.WriteLine("# NavPower Recast output nav mesh");

        // Write all vertices; orphan verts from filtered polys are harmless in OBJ.
        for (int vi = 0; vi < pmesh.nverts; vi++)
        {
            float wx = pmesh.bmin.X + pmesh.verts[vi * 3 + 0] * pmesh.cs;
            float wy = pmesh.bmin.Y + pmesh.verts[vi * 3 + 1] * pmesh.ch;
            float wz = pmesh.bmin.Z + pmesh.verts[vi * 3 + 2] * pmesh.cs;
            sw.WriteLine(FormattableString.Invariant($"v {wx:F6} {wy:F6} {wz:F6}"));
        }

        for (int pi = 0; pi < npolys; pi++)
        {
            if (!keep[pi]) continue;
            var vis = faceViLists[pi];
            for (int j = 1; j < vis.Count - 1; j++)
                sw.WriteLine(FormattableString.Invariant($"f {vis[0]} {vis[j]} {vis[j + 1]}"));
        }
    }

    /// <summary>
    /// Geometrically clips polygons from <paramref name="pmesh"/> to the tile
    /// <paramref name="crop"/> bounds (Sutherland-Hodgman, same as collision splitter)
    /// and writes the result to an OBJ file. Used for per-tile diagnostics.
    /// </summary>
    internal static void DumpCroppedNavMeshObj(
        string path,
        RcPolyMesh pmesh,
        RecastToNavPowerSerializer.NavMeshTileCropBounds crop,
        NavPowerBuildOptions options)
    {
        const int RC_NULL = RcRecast.RC_MESH_NULL_IDX;

        // Clip all polygons to tile bounds; keep empty slots so indices stay aligned.
        var clippedPolys = new System.Collections.Generic.List<Vector3[]>(pmesh.npolys);
        for (int pi = 0; pi < pmesh.npolys; pi++)
        {
            int baseIdx = pi * 2 * pmesh.nvp;
            var worldVerts = new System.Collections.Generic.List<Vector3>(pmesh.nvp);
            for (int j = 0; j < pmesh.nvp; j++)
            {
                int vi = pmesh.polys[baseIdx + j];
                if (vi == RC_NULL) break;
                worldVerts.Add(new Vector3(
                    pmesh.bmin.X + pmesh.verts[vi * 3 + 0] * pmesh.cs,
                    pmesh.bmin.Y + pmesh.verts[vi * 3 + 1] * pmesh.ch,
                    pmesh.bmin.Z + pmesh.verts[vi * 3 + 2] * pmesh.cs));
            }

            if (worldVerts.Count < 3) { clippedPolys.Add(System.Array.Empty<Vector3>()); continue; }

            var clipped = ClipPolyToRect(worldVerts, crop.MinX, crop.MaxX, crop.MinZ, crop.MaxZ);
            clippedPolys.Add(clipped ?? System.Array.Empty<Vector3>());
        }

        bool[] keep = RecastToNavPowerSerializer.ComputeIslandKeepMask(clippedPolys, options.MinIslandAreaSqMeters);

        using var sw = new System.IO.StreamWriter(path, append: false, encoding: System.Text.Encoding.UTF8);
        sw.WriteLine("# NavPower per-tile clipped nav mesh");

        int vertCounter = 0;
        for (int pi = 0; pi < clippedPolys.Count; pi++)
        {
            if (!keep[pi]) continue;
            var poly = clippedPolys[pi];
            int firstIdx = vertCounter + 1;
            foreach (var v in poly)
            {
                sw.WriteLine(FormattableString.Invariant($"v {v.X:F6} {v.Y:F6} {v.Z:F6}"));
                vertCounter++;
            }
            for (int j = 1; j < poly.Length - 1; j++)
                sw.WriteLine(FormattableString.Invariant($"f {firstIdx} {firstIdx + j} {firstIdx + j + 1}"));
        }
    }

    /// <summary>
    /// Sutherland-Hodgman polygon clip against the tile XZ rectangle.
    /// Returns the clipped polygon as a fresh <c>Vector3[]</c>, or <c>null</c>
    /// if the polygon is degenerate after any plane. Uses stack-allocated
    /// ping-pong buffers internally — no per-clip <c>List&lt;Vector3&gt;</c>
    /// or <c>Func&lt;&gt;</c> closure allocations (the previous shape
    /// allocated ~10 heap objects per polygon, multiplied by N polygons
    /// per tile).
    /// </summary>
    private static System.Numerics.Vector3[]? ClipPolyToRect(
        System.Collections.Generic.List<System.Numerics.Vector3> poly,
        float minX, float maxX, float minZ, float maxZ)
    {
        const int MaxVerts = 32;
        if (poly.Count == 0 || poly.Count > MaxVerts - 4)
            return null;

        Span<System.Numerics.Vector3> bufA = stackalloc System.Numerics.Vector3[MaxVerts];
        Span<System.Numerics.Vector3> bufB = stackalloc System.Numerics.Vector3[MaxVerts];
        for (int i = 0; i < poly.Count; i++) bufA[i] = poly[i];
        int count = poly.Count;

        count = ClipPlane(bufA, count, bufB, axisIsX: true,  greater: true,  minX); if (count < 3) return null;
        count = ClipPlane(bufB, count, bufA, axisIsX: true,  greater: false, maxX); if (count < 3) return null;
        count = ClipPlane(bufA, count, bufB, axisIsX: false, greater: true,  minZ); if (count < 3) return null;
        count = ClipPlane(bufB, count, bufA, axisIsX: false, greater: false, maxZ); if (count < 3) return null;

        return bufA.Slice(0, count).ToArray();
    }

    /// <summary>
    /// Single plane clip pass. <paramref name="axisIsX"/> selects which axis;
    /// <paramref name="greater"/> picks the half-plane direction
    /// (<c>true</c> = keep verts where coord ≥ plane, <c>false</c> = ≤ plane).
    /// Inlined branch on axis lets the inner test be a single direct
    /// comparison; no delegate dispatch.
    /// </summary>
    private static int ClipPlane(
        ReadOnlySpan<System.Numerics.Vector3> src,
        int count,
        Span<System.Numerics.Vector3> dst,
        bool axisIsX,
        bool greater,
        float plane)
    {
        int outN = 0;
        var prev = src[count - 1];
        bool prevIn = IsInside(prev, axisIsX, greater, plane);
        for (int i = 0; i < count; i++)
        {
            var cur = src[i];
            bool curIn = IsInside(cur, axisIsX, greater, plane);
            if (curIn)
            {
                if (!prevIn) dst[outN++] = LerpOnAxis(prev, cur, axisIsX, plane);
                dst[outN++] = cur;
            }
            else if (prevIn)
            {
                dst[outN++] = LerpOnAxis(prev, cur, axisIsX, plane);
            }
            prev = cur; prevIn = curIn;
        }
        return outN;
    }

    private static bool IsInside(System.Numerics.Vector3 v, bool axisIsX, bool greater, float plane)
    {
        float coord = axisIsX ? v.X : v.Z;
        return greater ? coord >= plane : coord <= plane;
    }

    private static System.Numerics.Vector3 LerpOnAxis(System.Numerics.Vector3 a, System.Numerics.Vector3 b, bool axisIsX, float plane) =>
        axisIsX ? LerpX(a, b, plane) : LerpZ(a, b, plane);

    private static System.Numerics.Vector3 LerpX(System.Numerics.Vector3 a, System.Numerics.Vector3 b, float x)
    {
        float d = b.X - a.X;
        float t = MathF.Abs(d) < 1e-8f ? 0f : (x - a.X) / d;
        t = Math.Clamp(t, 0f, 1f);
        return System.Numerics.Vector3.Lerp(a, b, t);
    }

    private static System.Numerics.Vector3 LerpZ(System.Numerics.Vector3 a, System.Numerics.Vector3 b, float z)
    {
        float d = b.Z - a.Z;
        float t = MathF.Abs(d) < 1e-8f ? 0f : (z - a.Z) / d;
        t = Math.Clamp(t, 0f, 1f);
        return System.Numerics.Vector3.Lerp(a, b, t);
    }


}
