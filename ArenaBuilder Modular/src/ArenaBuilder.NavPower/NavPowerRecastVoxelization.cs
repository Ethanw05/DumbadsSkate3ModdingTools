using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Recast;
using DotRecast.Recast.Geom;
using System.Numerics;

namespace ArenaBuilder.NavPower;

/// <summary>
/// Custom solid heightfield build for NavPower. Walkable triangles are split into two Recast area IDs:
/// near-horizontal surfaces vs sloped (ramp) surfaces. Recast region/watershed code only merges spans that
/// share the same area, so flat floors and ramps no longer collapse into one giant inclined polygon.
/// </summary>
internal static class NavPowerRecastVoxelization
{
    /// <summary>Same as <see cref="RcRecast.RC_WALKABLE_AREA"/> — flat-ish walkable ground.</summary>
    internal const int AreaFlatWalkable = RcRecast.RC_WALKABLE_AREA;

    /// <summary>Second walkable area for sloped faces (ramps). Must be &lt; 64 and non-zero.</summary>
    internal const int AreaSlopedWalkable = 62;

    /// <summary>
    /// Mirrors <see cref="RcVoxelizations.BuildSolidHeightfield"/> but uses <see cref="MarkWalkableTrianglesFlatVsSloped"/>.
    /// </summary>
    internal static RcHeightfield BuildSolidHeightfield(
        RcContext ctx,
        IRcInputGeomProvider geomProvider,
        RcBuilderConfig builderCfg,
        NavPowerBuildOptions navOpts)
    {
        RcConfig cfg = builderCfg.cfg;
        var solid = new RcHeightfield(
            builderCfg.width,
            builderCfg.height,
            builderCfg.bmin,
            builderCfg.bmax,
            cfg.Cs,
            cfg.Ch,
            cfg.BorderSize);

        // Reuse one `areas` buffer across all chunks/meshes — previously a
        // fresh `int[ntris]` was allocated per chunk and immediately discarded
        // after RasterizeTriangles, producing seam-padded tiles' worth of
        // gen-0 churn per build. Grow on demand only.
        int[] areasBuf = Array.Empty<int>();

        foreach (RcTriMesh geom in geomProvider.Meshes())
        {
            float[] verts = geom.GetVerts();
            if (cfg.UseTiles)
            {
                RcVec2f tbmin = new(builderCfg.bmin.X, builderCfg.bmin.Z);
                RcVec2f tbmax = new(builderCfg.bmax.X, builderCfg.bmax.Z);
                List<RcChunkyTriMeshNode> nodes = geom.GetChunksOverlappingRect(tbmin, tbmax);
                foreach (RcChunkyTriMeshNode node in nodes)
                {
                    int[] tris = node.tris;
                    int ntris = tris.Length / 3;
                    if (areasBuf.Length < ntris) areasBuf = new int[ntris];
                    MarkWalkableTrianglesFlatVsSlopedInto(
                        cfg.WalkableSlopeAngle,
                        navOpts.FlatSurfaceMaxSlopeDegrees,
                        verts,
                        tris,
                        ntris,
                        areasBuf);
                    RcRasterizations.RasterizeTriangles(ctx, verts, tris, areasBuf, ntris, solid, cfg.WalkableClimb);
                }
            }
            else
            {
                int[] tris = geom.GetTris();
                int ntris = tris.Length / 3;
                if (areasBuf.Length < ntris) areasBuf = new int[ntris];
                MarkWalkableTrianglesFlatVsSlopedInto(
                    cfg.WalkableSlopeAngle,
                    navOpts.FlatSurfaceMaxSlopeDegrees,
                    verts,
                    tris,
                    ntris,
                    areasBuf);
                RcRasterizations.RasterizeTriangles(ctx, verts, tris, areasBuf, ntris, solid, cfg.WalkableClimb);
            }
        }

        return solid;
    }

    /// <summary>
    /// Per-triangle area: <see cref="RcRecast.RC_NULL_AREA"/> if too steep;
    /// <see cref="AreaFlatWalkable"/> if face normal is within <paramref name="flatMaxSlopeDeg"/> of horizontal;
    /// otherwise <see cref="AreaSlopedWalkable"/> (still walkable under <paramref name="walkableSlopeDeg"/>).
    /// </summary>
    /// <remarks>
    /// Legacy convenience overload — allocates a fresh <c>int[nt]</c> each
    /// call. New code should use <see cref="MarkWalkableTrianglesFlatVsSlopedInto"/>
    /// to reuse a caller-owned buffer across chunks.
    /// </remarks>
    internal static int[] MarkWalkableTrianglesFlatVsSloped(
        float walkableSlopeDeg,
        float flatMaxSlopeDeg,
        float[] verts,
        int[] tris,
        int nt)
    {
        var areas = new int[nt];
        MarkWalkableTrianglesFlatVsSlopedInto(walkableSlopeDeg, flatMaxSlopeDeg, verts, tris, nt, areas);
        return areas;
    }

    /// <summary>
    /// In-place variant of <see cref="MarkWalkableTrianglesFlatVsSloped"/>.
    /// Writes the per-triangle area code into the first <paramref name="nt"/>
    /// slots of <paramref name="areasOut"/>.
    /// </summary>
    /// <remarks>
    /// Tight inner loop:
    /// <list type="bullet">
    /// <item>Reads vertex floats directly from <paramref name="verts"/> — no
    /// <c>Vector3</c> struct construction per vertex.</item>
    /// <item>Computes the face-normal cross product directly. Avoids
    /// <c>Vector3.Normalize</c> (which has a <c>MathF.Sqrt</c>) by comparing
    /// against squared thresholds: the slope check
    /// <c>norm.Y &lt;= walkableThr</c> is equivalent to
    /// <c>cross.Y &lt; 0 || cross.Y² &lt;= walkableThr² · |cross|²</c>
    /// for walkableThr ≥ 0 (cos of slope angle 0–90°). Same trick for
    /// the flat-vs-sloped threshold. One sqrt → zero per triangle, plus
    /// fewer struct ops.</item>
    /// </list>
    /// </remarks>
    internal static void MarkWalkableTrianglesFlatVsSlopedInto(
        float walkableSlopeDeg,
        float flatMaxSlopeDeg,
        float[] verts,
        int[] tris,
        int nt,
        int[] areasOut)
    {
        float walkableThr = MathF.Cos(walkableSlopeDeg * (MathF.PI / 180f));
        float flatDeg = Math.Min(flatMaxSlopeDeg, Math.Max(0.5f, walkableSlopeDeg - 0.5f));
        float flatThr = MathF.Cos(flatDeg * (MathF.PI / 180f));

        // Square the thresholds once; the inner loop compares squared values
        // against squared cross-product magnitude.
        float walkableThrSq = walkableThr * walkableThr;
        float flatThrSq = flatThr * flatThr;

        for (int i = 0; i < nt; i++)
        {
            int t = i * 3;
            int i0 = tris[t + 0] * 3;
            int i1 = tris[t + 1] * 3;
            int i2 = tris[t + 2] * 3;

            float v0x = verts[i0 + 0]; float v0y = verts[i0 + 1]; float v0z = verts[i0 + 2];
            float v1x = verts[i1 + 0]; float v1y = verts[i1 + 1]; float v1z = verts[i1 + 2];
            float v2x = verts[i2 + 0]; float v2y = verts[i2 + 1]; float v2z = verts[i2 + 2];

            float e1x = v1x - v0x, e1y = v1y - v0y, e1z = v1z - v0z;
            float e2x = v2x - v0x, e2y = v2y - v0y, e2z = v2z - v0z;

            // cross = e1 × e2
            float cx = e1y * e2z - e1z * e2y;
            float cy = e1z * e2x - e1x * e2z;
            float cz = e1x * e2y - e1y * e2x;

            float crossSq = cx * cx + cy * cy + cz * cz;
            if (crossSq <= 0f)
            {
                // Degenerate triangle — treat as too steep (null area).
                areasOut[i] = RcRecast.RC_NULL_AREA;
                continue;
            }

            // norm.Y <= walkableThr  ⇔  cy <= 0 || cy² <= walkableThr² · |cross|²
            // (walkableThr ∈ [0, 1] for slope angle 0°–90°).
            float cySq = cy * cy;
            if (cy <= 0f || cySq <= walkableThrSq * crossSq)
            {
                areasOut[i] = RcRecast.RC_NULL_AREA;
                continue;
            }

            areasOut[i] = cySq >= flatThrSq * crossSq ? AreaFlatWalkable : AreaSlopedWalkable;
        }
    }
}
