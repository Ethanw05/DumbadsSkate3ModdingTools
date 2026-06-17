namespace ArenaBuilder.NavPower;

/// <summary>Build parameters for NavGraph generation from collision triangles via DotRecast.</summary>
public sealed class NavPowerBuildOptions
{
    // ── Legacy NavGraph header descriptor values ───────────────────────────
    // These are the four floats (buildScale, voxSize, radius, step) stored in the 312-byte
    // NavGraphHeader on disk. The runtime BFS planner reads them; they MUST match retail
    // (0.12, 0.35, 0.20, 1.60) even when Recast uses different voxel / agent parameters.
    /// <summary>Retail Skate <c>m_buildScale</c> written into the legacy NavGraph header.</summary>
    public float HeaderBuildScale { get; init; } = 0.12f;
    /// <summary>Retail Skate <c>m_voxSize</c> in the header (0.35 across all 105 DIST_University graphs).</summary>
    public float HeaderVoxSize { get; init; } = 0.35f;
    /// <summary>Retail Skate <c>m_radius</c> in the header (0.20 across all 105 DIST_University graphs).</summary>
    public float HeaderRadius { get; init; } = 0.20f;
    /// <summary>Retail Skate <c>m_step</c> in the header (1.60 across all 105 DIST_University graphs).</summary>
    public float HeaderStep { get; init; } = 1.60f;

    // ── Recast voxelisation ──────────────────────────────────────────────────────
    // NOTE: These are DotRecast build parameters — independent of the Header* descriptor
    // values above. The Header* floats (0.12, 0.35, 0.20, 1.60) are read by the NavPower
    // runtime BFS planner after loading. Recast needs finer voxels and a smaller erosion
    // radius to correctly trace thin geometry (rails, walls, stair edges).

    /// <summary>Voxel size on the XZ plane (metres). Slightly coarser default reduces accidental rail-top walkability.</summary>
    public float VoxelSize { get; init; } = 0.1f;

    /// <summary>Voxel height on the Y axis (metres). Keep in lockstep with XZ voxel size for predictable step quantization.</summary>
    public float VoxelHeight { get; init; } = 0.05f;

    // ── Agent shape ──────────────────────────────────────────────────────────
    /// <summary>Minimum clearance height above the floor for a walkable span (metres).</summary>
    public float AgentHeight { get; init; } = 1.80f;

    /// <summary>
    /// Agent XZ radius for erosion (metres). 0.40 m increases wall/rail clearance so narrow strips
    /// are less likely to be considered walkable. Independent of <see cref="HeaderRadius"/>.
    /// </summary>
    public float AgentRadius { get; init; } = 0.30f;

    /// <summary>
    /// Maximum step height between adjacent heightfield columns (metres). Lower default prevents
    /// nearby-but-higher flat surfaces from being merged into one walkable region.
    /// Independent of <see cref="HeaderStep"/>.
    /// </summary>
    public float AgentMaxClimb { get; init; } = 0.24f;

    /// <summary>
    /// Maximum walkable slope in degrees. Reduced default avoids sloped/near-vertical bleed into rails or facades.
    /// </summary>
    public float AgentMaxSlope { get; init; } = 42f;

    /// <summary>
    /// Triangles whose normal is within this many degrees of vertical (Y-up) use the default Recast
    /// walkable area id; steeper but still walkable faces use a second area id so watershed regions do not
    /// merge flat floors with ramps into one inclined polygon.
    /// </summary>
    public float FlatSurfaceMaxSlopeDegrees { get; init; } = 3f;

    // ── Recast span filters (stairs / ledges) ─────────────────────────────────
    /// <summary>Recast low-hanging obstacle filter.</summary>
    public bool FilterLowHangingObstacles { get; init; } = true;

    /// <summary>
    /// Recast ledge filter. Enabled to prevent pedestrians being routed onto cliff edges.
    /// With AgentMaxClimb=0.40 this is safe — stair treads are not aggressively removed.
    /// </summary>
    public bool FilterLedgeSpans { get; init; } = true;

    /// <summary>Recast filter that removes spans with low ceiling clearance.</summary>
    public bool FilterWalkableLowHeightSpans { get; init; } = true;

    // ── Region merging ───────────────────────────────────────────────────────
    /// <summary>Minimum region area in voxels² before it is discarded.</summary>
    public int RegionMinSize { get; init; } = 1;

    /// <summary>
    /// Region area below which a region is merged into a larger neighbour.
    /// </summary>
    public int RegionMergeSize { get; init; } = 10;

    // ── Polygon mesh ─────────────────────────────────────────────────────────
    /// <summary>
    /// Maximum edge length in <b>world metres</b> for contour simplification.
    /// Large-world global builds must stay under the ~65k Recast contour-vertex cap before BuildPolyMesh;
    /// MONOTONE + flat/ramp area split increases vertex count, so defaults are slightly coarser than ideal per-tile builds.
    /// </summary>
    public float EdgeMaxLen { get; init; } = 0.6f;

    /// <summary>
    /// Maximum allowed contour simplification error (voxels).
    /// </summary>
    public float EdgeMaxError { get; init; } = 0.35f;

    /// <summary>Maximum number of vertices per polygon. NavPower supports up to 127; 6 is typical retail.</summary>
    public int MaxVertsPerPoly { get; init; } = 24;

    /// <summary>
    /// Remove disconnected nav-mesh surface blobs (islands) whose total XZ area is below this
    /// threshold (square metres).  Thin strips on railings, bench tops, or other elevated geometry
    /// that has no walkable connection to the main floor form small isolated components and are
    /// removed.  Polygons that belong to a large connected surface are always kept regardless of
    /// their individual polygon shape.  Set to &lt;= 0 to disable island culling entirely.
    /// </summary>
    public float MinIslandAreaSqMeters { get; init; } = 8.0f;

    // ── Detail mesh ──────────────────────────────────────────────────────────
    /// <summary>Detail mesh sampling distance (world units). Must be >= cs * 2 to avoid DotRecast hull triangulation bugs on large meshes.</summary>
    public float DetailSampleDist { get; init; } = 1.0f;

    /// <summary>Maximum height error for detail mesh vertices (world units).</summary>
    public float DetailSampleMaxError { get; init; } = 0.01f;

    /// <summary>
    /// Enable Recast detail mesh generation (<c>RcPolyMeshDetail</c>). This can improve surface fidelity,
    /// but may increase build cost and can trigger DotRecast per-poly detail triangulation limits on very large meshes.
    /// </summary>
    public bool BuildMeshDetail { get; init; } = false;

    /// <summary>
    /// Upper safety bound for attempting a single global Recast build, expressed as estimated
    /// XZ voxels: <c>(worldWidth * worldHeight) / (VoxelSize^2)</c>.
    /// If the estimate exceeds this value, the pipeline should switch to per-tile generation.
    /// </summary>
    public double GlobalMaxEstimatedVoxels { get; init; } = 12_000_000d;

    /// <summary>
    /// When true, per-tile NavPower Recast includes a thin XZ halo of neighbor collision so erosion
    /// is evaluated against true seam context; output is still cropped to nominal tile bounds.
    /// </summary>
    public bool IncludeNeighborSeams { get; init; } = true;

    /// <summary>
    /// Optional directory path for diagnostic OBJ exports. When set, writes
    /// <c>{dir}/navdebug_{U}_{V}_input.obj</c> and <c>{dir}/navdebug_{U}_{V}_navmesh.obj</c>.
    /// </summary>
    public string? DumpObjDir { get; init; }

    // ── NavPower layer mapping ───────────────────────────────────────────────
    /// <summary>
    /// Maps collision surface IDs to NavPower <c>layer_index</c> values (stored in area flags2).
    /// Retail DIST_University uses layers 2 (primary), 20, and 30. When null or when a surface ID
    /// has no entry, the <see cref="DefaultNavPowerLayer"/> is used.
    /// </summary>
    public IReadOnlyDictionary<int, int>? SurfaceIdToNavLayer { get; init; }

    /// <summary>
    /// Default NavPower <c>layer_index</c> for surfaces not found in <see cref="SurfaceIdToNavLayer"/>.
    /// Retail predominant layer is 2 (64% of areas in DIST_University).
    /// </summary>
    public int DefaultNavPowerLayer { get; init; } = 2;

    // ── Legacy ───────────────────────────────────────────────────────────────
    /// <summary>Hard cap on static areas per tile (KD + graph size).</summary>
    public int MaxAreas { get; init; } = 4096;

    /// <summary>XZ cell size kept for fallback / legacy callers.</summary>
    public float CellSize { get; init; } = 4f;

    /// <summary>
    /// World-space XZ pad for Recast when merging neighbor cSim collision (see tile build pipeline).
    /// Matches Recast-style border: agent radius in voxels plus a few cells so erosion sees context across tile seams.
    /// </summary>
    public float ComputeNavSeamPadWorld()
    {
        int radiusVx = Math.Max(1, (int)MathF.Ceiling(AgentRadius / VoxelSize));
        return (radiusVx + 4) * VoxelSize;
    }
}
