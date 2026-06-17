using ArenaBuilder.Core.Platforms.Common.Pegasus.AIPath;

namespace ArenaBuilder.Core.Platforms.Common.Pegasus.Irradiance;

/// <summary>
/// Buckets probes into <c>cPres_X_Y_high</c> stream tiles by world XZ position.
/// Reuses the AIPath 100 m grid convention (centered at ±50, ±150…) — same grid
/// the stock cPres tiles use.
///
/// No edge duplication: per user direction the engine blends neighboring hulls
/// at runtime (<c>SHLightingMan::GetClosestLightProbe @ 0x82b70e00</c> iterates
/// every loaded hull's rbtree entry in mProbeGridMap, runs per-hull SpatialGrid
/// 41×41 intersect, and picks the closest probe across bbox-overlapping hulls),
/// so a probe needs to live in exactly one tile.
/// </summary>
public static class IrradianceProbeBucketer
{
    public const float TileSize         = AiPathTileBucketer.DefaultTileSize;
    public const float TileCenterOffset = AiPathTileBucketer.DefaultTileCenterOffset;

    public readonly record struct TileKey(int X, int Y)
    {
        public string FolderName => $"cPres_{X}_{Y}_high";
    }

    /// <summary>
    /// Group probes by their owning tile. Each probe lives in exactly one tile;
    /// queries near tile seams blend at runtime via the engine's per-hull search.
    /// </summary>
    public static IReadOnlyDictionary<TileKey, IReadOnlyList<int>> Bucket(
        IReadOnlyList<Probe> probes)
    {
        var buckets = new Dictionary<TileKey, List<int>>();
        for (int i = 0; i < probes.Count; i++)
        {
            var tile = TileFor(probes[i].X, probes[i].Z);
            if (!buckets.TryGetValue(tile, out var list))
            {
                list = new List<int>();
                buckets[tile] = list;
            }
            list.Add(i);
        }
        return buckets.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<int>)kv.Value);
    }

    public static TileKey TileFor(float worldX, float worldZ)
    {
        int kx = (int)Math.Floor((worldX - TileCenterOffset) / TileSize + 0.5f);
        int kz = (int)Math.Floor((worldZ - TileCenterOffset) / TileSize + 0.5f);
        int tileX = (int)Math.Round(TileCenterOffset + kx * TileSize);
        int tileZ = (int)Math.Round(TileCenterOffset + kz * TileSize);
        return new TileKey(tileX, tileZ);
    }

    /// <summary>
    /// Engine <c>SpatialGrid&lt;tIrradianceData*, 41, 41&gt;</c> cell count per axis.
    /// AddHullLightProbes (sk82_na_zd.xex @ 0x82b71370) zeros 0x691 (1681) cell slots
    /// and SetValue writes one probe pointer per cell — last-write-wins. So binning
    /// authored probes into a 41×41 XZ grid matching the runtime layout is lossless
    /// from the engine's lookup POV (any finer density would collapse anyway).
    /// </summary>
    public const int EngineGrid = 41;

    /// <summary>
    /// Downsample one tile's probes into the engine's 41×41 XZ grid.
    ///
    /// Engine grid is <strong>2D in XZ only</strong> (sk82_na_zd.xex
    /// AddHullLightProbes @ 0x82b71370 zeros 0x691 = 1681 cells of 4 bytes each;
    /// SetValue overwrites — last-write-wins per cell). When the Blender addon
    /// stacks multiple Y per XZ column (e.g. rooftop + tunnel-floor probes for a
    /// building over a tunnel), multiple probes land in the same XZ cell.
    /// Averaging across floors blends bright (rooftop) with dark (tunnel) →
    /// wrong lighting for the skater inside.
    ///
    /// Strategy: per cell, keep the <strong>lowest-Y</strong> probe. Skaters
    /// travel on lower floors; rooftops above are dropped. Deterministic + biased
    /// toward where gameplay happens. If two probes share both XZ cell and Y
    /// (rare), first-encountered wins.
    ///
    /// Returns the input unchanged if already at or below
    /// <paramref name="maxProbes"/>.
    /// </summary>
    public static IReadOnlyList<Probe> DownsampleToEngineGrid(
        IReadOnlyList<Probe> tileProbes,
        TileKey tile,
        int maxProbes)
    {
        if (tileProbes is null) throw new ArgumentNullException(nameof(tileProbes));
        if (tileProbes.Count <= maxProbes) return tileProbes;

        float tileMinX = tile.X - TileSize * 0.5f;
        float tileMinZ = tile.Y - TileSize * 0.5f;
        float cellSize = TileSize / EngineGrid;

        var winners = new Dictionary<int, Probe>(Math.Min(tileProbes.Count, EngineGrid * EngineGrid));
        for (int i = 0; i < tileProbes.Count; i++)
        {
            var p = tileProbes[i];
            int cx = Math.Clamp((int)MathF.Floor((p.X - tileMinX) / cellSize), 0, EngineGrid - 1);
            int cz = Math.Clamp((int)MathF.Floor((p.Z - tileMinZ) / cellSize), 0, EngineGrid - 1);
            int key = cz * EngineGrid + cx;
            if (!winners.TryGetValue(key, out var existing) || p.Y < existing.Y)
                winners[key] = p;
        }

        return new List<Probe>(winners.Values);
    }
}
