using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ArenaBuilder.Glb;

/// <summary>
/// Utility helpers for assigning world-space geometry to streaming tiles.
/// </summary>
public static class WorldTileGrid
{
    // GLB world horizontal plane for tiling is X/Z. Y is vertical and must not affect tile assignment.
    private const float TileEpsilon = 1e-5f;
    // Tolerate tiny float drift from import/export pipelines near exact tile boundaries.
    private const float BoundarySnapTolerance = 1e-3f;

    public readonly record struct TileKey(int U, int V);

    public const float DefaultTileSize = 100.0f;
    public const float DefaultOriginX = 0.0f;
    public const float DefaultOriginY = 0.0f;

    public static TileKey GetTileForPoint(
        Vector3 point,
        float tileSize = DefaultTileSize,
        float originX = DefaultOriginX,
        float originY = DefaultOriginY)
    {
        ValidateTileSize(tileSize);

        int u = (int)MathF.Floor((point.X - originX) / tileSize);
        int v = (int)MathF.Floor((point.Z - originY) / tileSize);
        return new TileKey(u, v);
    }

    /// <summary>
    /// Returns inclusive tile index range for an XZ-space bounds rectangle.
    /// Uses epsilon on max edges so exact boundary values don't spill into the next tile.
    /// </summary>
    public static (int UMin, int UMax, int VMin, int VMax) GetTileRangeForBoundsXY(
        float minX,
        float maxX,
        float minZ,
        float maxZ,
        float tileSize = DefaultTileSize,
        float originX = DefaultOriginX,
        float originY = DefaultOriginY)
    {
        ValidateTileSize(tileSize);
        minX = SnapCoordToTileBoundary(minX, tileSize, originX);
        maxX = SnapCoordToTileBoundary(maxX, tileSize, originX);
        minZ = SnapCoordToTileBoundary(minZ, tileSize, originY);
        maxZ = SnapCoordToTileBoundary(maxZ, tileSize, originY);
        int uMin = (int)MathF.Floor((minX - originX) / tileSize);
        int uMax = (int)MathF.Floor(((maxX - originX) / tileSize) - TileEpsilon);
        int vMin = (int)MathF.Floor((minZ - originY) / tileSize);
        int vMax = (int)MathF.Floor(((maxZ - originY) / tileSize) - TileEpsilon);
        if (uMax < uMin) uMax = uMin;
        if (vMax < vMin) vMax = vMin;
        return (uMin, uMax, vMin, vMax);
    }

    /// <summary>
    /// Returns tile-space XZ bounds for a tile key.
    /// </summary>
    public static (float MinX, float MaxX, float MinZ, float MaxZ) GetTileBoundsXY(
        TileKey tile,
        float tileSize = DefaultTileSize,
        float originX = DefaultOriginX,
        float originY = DefaultOriginY)
    {
        ValidateTileSize(tileSize);
        float minX = originX + tile.U * tileSize;
        float maxX = minX + tileSize;
        float minZ = originY + tile.V * tileSize;
        float maxZ = minZ + tileSize;
        return (minX, maxX, minZ, maxZ);
    }

    public static TileKey GetTileForBounds(
        (Vector3 Min, Vector3 Max) bounds,
        float tileSize = DefaultTileSize,
        float originX = DefaultOriginX,
        float originY = DefaultOriginY)
    {
        var center = (bounds.Min + bounds.Max) * 0.5f;
        return GetTileForPoint(center, tileSize, originX, originY);
    }

    public static Vector2 GetTileCenter(
        TileKey tile,
        float tileSize = DefaultTileSize,
        float originX = DefaultOriginX,
        float originY = DefaultOriginY)
    {
        ValidateTileSize(tileSize);

        float cx = originX + (tile.U + 0.5f) * tileSize;
        float cz = originY + (tile.V + 0.5f) * tileSize;
        return new Vector2(cx, cz);
    }

    /// <summary>
    /// WPQUAD root on one axis for absolute world <paramref name="streamTileWorldCenterAxis"/> using the same 128 m grid as
    /// <see cref="GenTileIdSkate"/> (<c>floor(world / extent) * extent + extent/2</c>). Skate indexes cells with
    /// <c>floor(worldX / width)</c>, not <c>floor((world - streamOrigin) / width)</c>, so stream-tile origins must not shift
    /// this snap (negative tiles + non-zero <see cref="TileBuildOptions.OriginX"/> used to desync bake from <c>GetTileHandle</c>).
    /// <paramref name="originAxis"/> is ignored (kept for a stable public signature).
    /// </summary>
    public static float GetWorldPainterQuadRootCenterAxis(
        float streamTileWorldCenterAxis,
        float originAxis,
        float worldPainterCellExtent = 128f)
    {
        _ = originAxis;
        return GetWorldPainterQuadRootCenterAxisAbsolute(streamTileWorldCenterAxis, worldPainterCellExtent);
    }

    /// <summary>
    /// Same 128 m cell center as <see cref="GenTileIdSkate"/> on one axis: <c>floor(worldAxis/extent)*extent + extent/2</c>.
    /// </summary>
    public static float GetWorldPainterQuadRootCenterAxisAbsolute(
        float worldAxis,
        float worldPainterCellExtent = 128f)
    {
        if (worldPainterCellExtent <= 0f)
            throw new ArgumentOutOfRangeException(nameof(worldPainterCellExtent), "Cell extent must be > 0.");
        worldAxis = Float32Sanitize(worldAxis);
        worldPainterCellExtent = Float32Sanitize(worldPainterCellExtent);
        float half = worldPainterCellExtent * 0.5f;
        return MathF.Floor(worldAxis / worldPainterCellExtent) * worldPainterCellExtent + half;
    }

    /// <summary>
    /// Picks the pegasus WPQUAD root center on one horizontal axis so the 128 m cell (±64 m) covers as much of the
    /// stream footprint <c>[smin, smax)</c> (absolute world) as possible. Uses the same cell indices as
    /// <see cref="GenTileIdSkate"/> (not origin-shifted stream tiling). A 100 m tile can straddle two 128 m cells; using only
    /// the tile midpoint with <see cref="GetWorldPainterQuadRootCenterAxisAbsolute"/> can leave part of the footprint outside
    /// the quad AABB, so <c>DoQuadTreeLookup</c> misses and ambience falls back or comes from another registration.
    /// </summary>
    public static float GetWorldPainterQuadRootCenterAxisForStreamBounds(
        float smin,
        float smax,
        float worldPainterCellExtent = 128f)
    {
        if (worldPainterCellExtent <= 0f)
            throw new ArgumentOutOfRangeException(nameof(worldPainterCellExtent), "Cell extent must be > 0.");
        if (!(smax > smin))
            throw new ArgumentOutOfRangeException(nameof(smax), "Stream bounds must have smax > smin.");
        smin = Float32Sanitize(smin);
        smax = Float32Sanitize(smax);
        worldPainterCellExtent = Float32Sanitize(worldPainterCellExtent);
        float half = worldPainterCellExtent * 0.5f;
        float mid = (smin + smax) * 0.5f;

        int kStart = (int)MathF.Floor(smin / worldPainterCellExtent);
        int kEnd = (int)MathF.Floor((smax - TileEpsilon) / worldPainterCellExtent);
        if (kEnd < kStart)
            kEnd = kStart;

        float bestCenter = GetWorldPainterQuadRootCenterAxisAbsolute(mid, worldPainterCellExtent);
        float bestLen = -1f;
        float bestDist = float.MaxValue;

        for (int k = kStart; k <= kEnd; k++)
        {
            float c = k * worldPainterCellExtent + half;
            float q0 = c - half;
            float q1 = c + half;
            float lo = MathF.Max(smin, q0);
            float hi = MathF.Min(smax, q1);
            float len = hi > lo ? hi - lo : 0f;
            float dist = MathF.Abs(c - mid);
            if (len > bestLen + 1e-4f
                || (MathF.Abs(len - bestLen) < 1e-4f && dist < bestDist - 1e-4f))
            {
                bestLen = len;
                bestDist = dist;
                bestCenter = c;
            }
        }

        return bestCenter;
    }

    /// <summary>
    /// WPQUAD root (horizontal X, horizontal Z stored as Y) for a stream tile. Uses
    /// <see cref="GetWorldPainterQuadRootCenterAxisForStreamBounds"/> per axis from <see cref="GetTileBoundsXY"/>.
    /// </summary>
    public static Vector2 GetWorldPainterQuadRootCenter(
        TileKey tile,
        float streamTileSize,
        float originX,
        float originY,
        float worldPainterCellExtent = 128f)
    {
        var (minX, maxX, minZ, maxZ) = GetTileBoundsXY(tile, streamTileSize, originX, originY);
        float rx = GetWorldPainterQuadRootCenterAxisForStreamBounds(minX, maxX, worldPainterCellExtent);
        float rz = GetWorldPainterQuadRootCenterAxisForStreamBounds(minZ, maxZ, worldPainterCellExtent);
        return new Vector2(rx, rz);
    }

    /// <summary>
    /// WPQUAD root center from a combined XZ footprint (union of stream tiles). Use when several cSim tiles register
    /// the same 128 m <see cref="GenTileIdSkate"/> cell: baking from the owner tile alone skewed the quad center and
    /// mis-mapped paint keys for pedestrians in sibling tiles.
    /// </summary>
    public static Vector2 GetWorldPainterQuadRootCenterForBounds(
        float minX,
        float maxX,
        float minZ,
        float maxZ,
        float worldPainterCellExtent = 128f)
    {
        float rx = GetWorldPainterQuadRootCenterAxisForStreamBounds(minX, maxX, worldPainterCellExtent);
        float rz = GetWorldPainterQuadRootCenterAxisForStreamBounds(minZ, maxZ, worldPainterCellExtent);
        return new Vector2(rx, rz);
    }

    /// <summary>
    /// Skate <c>WorldPainter::LayerMan::GenTileId</c> (default 128 m cells): <c>iy * 100000 + ix</c> with
    /// <c>ix = floor(worldX / width)</c>, <c>iy = floor(worldZ / width)</c>, packed as <c>uint32</c> (unsigned wrap).
    /// Horizontal plane is X / Z (same as <see cref="GetTileForPoint"/> using <c>point.Z</c> as the second axis).
    /// For <c>WorldPainter::LayerMan::mTileMap</c> / <c>GetTileHandle</c>, see <c>documentation/WorldPainter/82382B50</c>.
    /// </summary>
    /// <remarks>
    /// Points on exact multiples of <paramref name="tileWidth"/> sit on a tile-index seam (e.g. <c>z == 0</c> uses
    /// <c>iy == 0</c> while <c>z == -0.01</c> uses <c>iy == -1</c>). WorldPainter sampling should stay slightly inside
    /// stream AABB on those edges so built WPQUAD keys match the tile that owns the cSim folder.
    /// <para>
    /// <c>WorldPainter::LayerMan::GetTileHandle</c> keys off this id (<c>documentation/WorldPainter/82382B50</c>,
    /// <c>82384318</c>); a sample that resolves to a different id than the stream tile center bakes the <em>neighbor</em>
    /// cell's paint into this folder's PSG (e.g. Downtown on the <c>iy == 0</c> row while the cSim folder is <c>iy == -1</c>).
    /// </para>
    /// </remarks>
    public static uint GenTileIdSkate(float worldX, float worldZ, float tileWidth = 128f)
    {
        if (tileWidth <= 0f)
            throw new ArgumentOutOfRangeException(nameof(tileWidth), "Tile width must be > 0.");
        worldX = Float32Sanitize(worldX);
        worldZ = Float32Sanitize(worldZ);
        tileWidth = Float32Sanitize(tileWidth);
        int ix = (int)MathF.Floor(worldX / tileWidth);
        int iy = (int)MathF.Floor(worldZ / tileWidth);
        long packed = (long)iy * 100_000L + ix;
        return unchecked((uint)packed);
    }

    /// <summary>
    /// Inverse of <see cref="GenTileIdSkate"/> for packed <c>uint32</c> keys (same layout as Skate: <c>iy * 100000 + ix</c> as signed int).
    /// </summary>
    public static void DecodeGenTileIdSkate(uint cellId, out int ix, out int iy)
    {
        int s = unchecked((int)cellId);
        iy = s / 100_000;
        ix = s - iy * 100_000;
    }

    /// <summary>
    /// 128 m WPQUAD root center for the cell identified by <see cref="GenTileIdSkate"/> (center of the cell, not union-of-footprint).
    /// Use when union-bake <see cref="GetWorldPainterQuadRootCenterForBounds"/> re-hashes to a different id than the stream tiles' <c>wpTileId</c>.
    /// </summary>
    public static Vector2 GetWorldPainterQuadRootCenterForGenTileId(uint cellId, float worldPainterCellExtent = 128f)
    {
        if (worldPainterCellExtent <= 0f)
            throw new ArgumentOutOfRangeException(nameof(worldPainterCellExtent), "Cell extent must be > 0.");
        DecodeGenTileIdSkate(cellId, out int ix, out int iy);
        float half = worldPainterCellExtent * 0.5f;
        float cx = Float32Sanitize(ix * worldPainterCellExtent + half);
        float cz = Float32Sanitize(iy * worldPainterCellExtent + half);
        return new Vector2(cx, cz);
    }

    /// <summary>
    /// Round-trip through IEEE binary32 like AltiVec float lanes in <c>WorldPainter::DoQuadTreeLookup</c>
    /// (<c>documentation/WorldPainter/8238F988</c>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Float32Sanitize(float value) =>
        BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(value));

    /// <summary>
    /// Same as <see cref="Float32Sanitize(float)"/> after a double intermediate.
    /// </summary>
    public static float Float32Sanitize(double value) => Float32Sanitize((float)value);

    /// <summary>
    /// If <paramref name="worldX"/>/<paramref name="worldZ"/> would use a different <see cref="GenTileIdSkate"/> than
    /// the stream tile reference (e.g. <see cref="GetTileCenter"/>), lerps toward <paramref name="refX"/>/<paramref name="refZ"/>
    /// until the id matches or falls back to the reference point. Matches the engine rule that one cSim folder's WorldPainter
    /// asset is selected by <c>GetTileHandle(pos)</c> for that tile's GenTileId.
    /// </summary>
    public static void NudgeToSameGenTileIdAsReference(
        ref float worldX,
        ref float worldZ,
        float refX,
        float refZ,
        float tileWidth = 128f)
    {
        refX = Float32Sanitize(refX);
        refZ = Float32Sanitize(refZ);
        worldX = Float32Sanitize(worldX);
        worldZ = Float32Sanitize(worldZ);
        uint want = GenTileIdSkate(refX, refZ, tileWidth);
        if (GenTileIdSkate(worldX, worldZ, tileWidth) == want)
            return;

        float ax = worldX;
        float az = worldZ;
        for (int i = 0; i < 28; i++)
        {
            ax = Float32Sanitize(Float32Sanitize(ax + refX) * 0.5f);
            az = Float32Sanitize(Float32Sanitize(az + refZ) * 0.5f);
            if (GenTileIdSkate(ax, az, tileWidth) == want)
            {
                worldX = ax;
                worldZ = az;
                return;
            }
        }

        worldX = refX;
        worldZ = refZ;
    }

    public static string BuildFolderName(
        string prefix,
        TileKey tile,
        float tileSize = DefaultTileSize,
        float originX = DefaultOriginX,
        float originY = DefaultOriginY,
        string suffix = "high")
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("Tile folder prefix is required.", nameof(prefix));
        if (string.IsNullOrWhiteSpace(suffix))
            throw new ArgumentException("Tile folder suffix is required.", nameof(suffix));

        Vector2 center = GetTileCenter(tile, tileSize, originX, originY);
        string cx = FormatCoord(center.X);
        string cy = FormatCoord(center.Y);
        return $"{prefix}_{cx}_{cy}_{suffix}";
    }

    /// <summary>
    /// Tile key in the half-offset cTex grid. cPres/cSim tiles are centered at
    /// <c>origin + (U + 0.5) * tileSize</c>; cTex tiles in stock Skate maps (e.g.
    /// <c>DLC_DW_MegaCompund</c> with <c>cPres_50_50_high.psf</c> alongside
    /// <c>cTex_0_0_high.psf</c>, <c>cTex_100_100_high.psf</c>, ...) are centered at
    /// <c>origin + CU * tileSize</c>. A cTex tile <c>(CU, CV)</c> sits on the corner
    /// shared by the four cPres tiles <c>{(CU-1, CV-1), (CU, CV-1), (CU-1, CV), (CU, CV)}</c>.
    ///
    /// <para>
    /// Stock duplication policy (verified empirically against DIST_University and
    /// DLC_DW_MegaCompund): a texture's full-resolution copy is placed in every cTex tile whose
    /// world rectangle is touched by the texture's using-geometry XZ AABB. Single-cPres-user
    /// textures land in 1, 2, or 4 cTex tiles depending on whether the mesh sits inside / on an
    /// edge / on a corner of the cTex grid. Multi-cPres-user textures get the union of those sets.
    /// </para>
    /// </summary>
    public readonly record struct CTexTileKey(int CU, int CV);

    /// <summary>
    /// World-space center of a cTex tile <paramref name="tile"/> in the half-offset grid.
    /// </summary>
    public static Vector2 GetCTexTileCenter(
        CTexTileKey tile,
        float tileSize = DefaultTileSize,
        float originX = DefaultOriginX,
        float originY = DefaultOriginY)
    {
        ValidateTileSize(tileSize);
        float cx = originX + tile.CU * tileSize;
        float cz = originY + tile.CV * tileSize;
        return new Vector2(cx, cz);
    }

    /// <summary>
    /// World-space XZ bounds of a cTex tile in the half-offset grid (size = <paramref name="tileSize"/>).
    /// </summary>
    public static (float MinX, float MaxX, float MinZ, float MaxZ) GetCTexTileBoundsXY(
        CTexTileKey tile,
        float tileSize = DefaultTileSize,
        float originX = DefaultOriginX,
        float originY = DefaultOriginY)
    {
        ValidateTileSize(tileSize);
        float half = tileSize * 0.5f;
        float cx = originX + tile.CU * tileSize;
        float cz = originY + tile.CV * tileSize;
        return (cx - half, cx + half, cz - half, cz + half);
    }

    /// <summary>
    /// The 4 cTex tiles that overlap a given cPres tile in the half-offset grid:
    /// <c>{(U, V), (U+1, V), (U, V+1), (U+1, V+1)}</c>.
    /// </summary>
    public static IEnumerable<CTexTileKey> GetCTexCandidatesForPresTile(TileKey presTile)
    {
        yield return new CTexTileKey(presTile.U, presTile.V);
        yield return new CTexTileKey(presTile.U + 1, presTile.V);
        yield return new CTexTileKey(presTile.U, presTile.V + 1);
        yield return new CTexTileKey(presTile.U + 1, presTile.V + 1);
    }

    /// <summary>
    /// All cTex tiles whose world-space rectangle overlaps an XZ-aligned AABB. Used to assign
    /// per-mesh-AABB textures to cTex tiles. Empirically verified against stock DISTs
    /// (DIST_University, DLC_DW_MegaCompund): 100% of stock cTex copies live within
    /// <c>cTex_tiles_overlapping(mesh_aabb)</c>, and the per-mesh distribution of "1 / 2 / 4 cTex
    /// copies" matches geometry containment within / straddling-edge / straddling-corner of cTex tiles.
    /// </summary>
    /// <remarks>
    /// cTex tile <c>(CU,CV)</c> covers
    /// <c>[origin + CU·ts - ts/2, origin + CU·ts + ts/2]</c> on each axis, so:
    /// <c>cuMin = ceil((minX - originX - ts/2) / ts)</c>,
    /// <c>cuMax = floor((maxX - originX + ts/2) / ts)</c> (closed-interval overlap, points exactly
    /// on a cTex boundary count as overlapping the lower-index tile).
    /// </remarks>
    public static IEnumerable<CTexTileKey> GetCTexTilesOverlappingAabbXY(
        float minX,
        float maxX,
        float minZ,
        float maxZ,
        float tileSize = DefaultTileSize,
        float originX = DefaultOriginX,
        float originY = DefaultOriginY)
    {
        ValidateTileSize(tileSize);
        if (!(maxX >= minX) || !(maxZ >= minZ))
            yield break;

        float half = tileSize * 0.5f;

        int cuMin = (int)MathF.Ceiling((minX - originX - half) / tileSize);
        int cuMax = (int)MathF.Floor((maxX - originX + half) / tileSize);
        int cvMin = (int)MathF.Ceiling((minZ - originY - half) / tileSize);
        int cvMax = (int)MathF.Floor((maxZ - originY + half) / tileSize);

        if (cuMax < cuMin) cuMax = cuMin;
        if (cvMax < cvMin) cvMax = cvMin;

        for (int cu = cuMin; cu <= cuMax; cu++)
            for (int cv = cvMin; cv <= cvMax; cv++)
                yield return new CTexTileKey(cu, cv);
    }

    /// <summary>
    /// True when cTex <paramref name="ctex"/>'s 2x2 cPres footprint contains <paramref name="presTile"/>.
    /// </summary>
    public static bool CTexCoversPresTile(CTexTileKey ctex, TileKey presTile)
    {
        return (presTile.U == ctex.CU - 1 || presTile.U == ctex.CU)
            && (presTile.V == ctex.CV - 1 || presTile.V == ctex.CV);
    }

    /// <summary>
    /// Greedy set cover: assigns every cPres in <paramref name="presTiles"/> to one cTex from that
    /// cPres’s four <see cref="GetCTexCandidatesForPresTile"/> candidates, minimizing the number
    /// of distinct cTex tiles. The tile build <b>unions</b> these homes with
    /// <see cref="GetCTexTilesOverlappingAabbXY"/> on each tile-split mesh piece so full-res
    /// exists both where geometry overlaps cTex and where the engine expects a per-cPres &quot;home&quot;.
    /// </summary>
    /// <remarks>
    /// Each chosen home always lies in the cPres’s 2×2 overlap. The undivided mesh AABB is not
    /// used (can fan out to thousands of cTex cells); split AABB is unioned with these homes.
    /// </remarks>
    public static IReadOnlyDictionary<TileKey, CTexTileKey> AssignPresTilesToCTexCover(
        IReadOnlyCollection<TileKey> presTiles)
    {
        var assignments = new Dictionary<TileKey, CTexTileKey>();
        if (presTiles.Count == 0)
            return assignments;

        var remaining = new HashSet<TileKey>(presTiles);
        while (remaining.Count > 0)
        {
            var coverage = new Dictionary<CTexTileKey, HashSet<TileKey>>();
            foreach (var pt in remaining)
            {
                foreach (var ct in GetCTexCandidatesForPresTile(pt))
                {
                    if (!coverage.TryGetValue(ct, out var set))
                    {
                        set = new HashSet<TileKey>();
                        coverage[ct] = set;
                    }
                    set.Add(pt);
                }
            }

            var best = coverage
                .OrderByDescending(kv => kv.Value.Count)
                .ThenBy(kv => kv.Key.CU)
                .ThenBy(kv => kv.Key.CV)
                .First();

            foreach (var pt in best.Value)
            {
                assignments[pt] = best.Key;
                remaining.Remove(pt);
            }
        }

        return assignments;
    }

    /// <summary>
    /// Folder name for a cTex tile in the half-offset grid (e.g. <c>cTex_0_0_high</c>,
    /// <c>cTex_100_100_high</c>) — matches stock Skate maps where cTex tiles are centered
    /// at integer multiples of <paramref name="tileSize"/> while cPres tiles are at the
    /// half-tile offset. Verified against
    /// <c>cAssetStreamSystem::ParseXmlStreamTile</c> (<c>0x824031a0</c>) which rounds the
    /// tile center via <c>floor(c + 0.5)</c> and formats the collection ID with
    /// <c>"c%s_%.f_%.f_high"</c>; integer centers therefore hash deterministically.
    /// </summary>
    public static string BuildCTexFolderName(
        CTexTileKey tile,
        float tileSize = DefaultTileSize,
        float originX = DefaultOriginX,
        float originY = DefaultOriginY,
        string suffix = "high")
    {
        if (string.IsNullOrWhiteSpace(suffix))
            throw new ArgumentException("Tile folder suffix is required.", nameof(suffix));

        Vector2 center = GetCTexTileCenter(tile, tileSize, originX, originY);
        string cx = FormatCoord(center.X);
        string cy = FormatCoord(center.Y);
        return $"cTex_{cx}_{cy}_{suffix}";
    }

    private static void ValidateTileSize(float tileSize)
    {
        if (tileSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(tileSize), "Tile size must be > 0.");
    }

    private static float SnapCoordToTileBoundary(float value, float tileSize, float origin)
    {
        float normalized = (value - origin) / tileSize;
        float nearest = MathF.Round(normalized);
        if (MathF.Abs(normalized - nearest) <= (BoundarySnapTolerance / tileSize))
            return origin + nearest * tileSize;
        return value;
    }

    private static string FormatCoord(float value)
    {
        // Normalize tiny "-0" style values for clean folder names.
        if (MathF.Abs(value) < 0.0001f)
            value = 0f;

        float rounded = MathF.Round(value);
        if (MathF.Abs(value - rounded) < 0.0001f)
            return ((int)rounded).ToString(CultureInfo.InvariantCulture);

        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
