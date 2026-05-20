using System.Globalization;
using System.IO;
using System.Text;

namespace DlcBuilder.Modules.DlcManifest.Xml;

/// Per-area freeskate stream XML + boundary XML emitters. Companion to the
/// VLT manifest — the engine reads these to pre-allocate stream tiles for the
/// online freeskate area and to test player-position vs the OOB boundary.
///
/// Algorithm ported from ArenaBuilder Modular's
/// `DistPackRunner.WriteStreamTilesXml` / `CollectTileCenters`. Scans the
/// user's DIST folder for `cPres_<cx>_<cy>_high[_proxy].psf` files (recursive
/// — stock content has them under `content/world/stream/<World>/`, but
/// tooling-built dists put them at the root). Each PSF is one tile center.
public static class FreeskateAreaXmlBuilder
{
    public const int DefaultTileSize = 100;

    /// Skate 2 EBOOT SIMD path for building per-slot stream data compares
    /// counts against 0x3F (63) paths in `sub_112E78`; emitting hundreds of
    /// `<Tile>` rows for a mega-DLC walks past populated asset pointers and
    /// crashes (lvx at NULL+0x50). Cap streamed footprint.
    public const int FreeskateAreaStreamMaxTiles = 63;

    /// Recursively scan a DIST folder for `cPres_<cx>_<cy>_high[_proxy].psf`
    /// files. Returns the set of tile centers (cx, cy). Skips
    /// `cPres_Global` / `cPres_Global_proxy`.
    public static HashSet<(int cx, int cy)> ScanDistTileCenters(string distPath)
    {
        var centers = new HashSet<(int, int)>();
        if (string.IsNullOrEmpty(distPath) || !Directory.Exists(distPath)) return centers;

        foreach (string file in Directory.EnumerateFiles(distPath, "cPres_*_high*.psf", SearchOption.AllDirectories))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.StartsWith("cPres_Global", StringComparison.OrdinalIgnoreCase)) continue;

            const string proxy = "_proxy";
            string trimmed = name.EndsWith(proxy, StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - proxy.Length)
                : name;
            // Required shape: cPres_<cx>_<cy>_high
            if (!trimmed.EndsWith("_high", StringComparison.OrdinalIgnoreCase)) continue;
            string[] parts = trimmed.Split('_');
            if (parts.Length != 4) continue;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cx)) continue;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cy)) continue;
            centers.Add((cx, cy));
        }
        return centers;
    }

    /// Map a world-space (x, z) position to its enclosing tile center. Tile
    /// centers are at offsets `-tileSize/2 + N*tileSize` (so for tileSize=100
    /// the centers are ..., -150, -50, 50, 150, ...).
    public static (int cx, int cy) PositionToTileCenter(float x, float z, int tileSize = DefaultTileSize)
    {
        int half = tileSize / 2;
        int cx = (int)Math.Floor((x + half) / tileSize) * tileSize - half;
        int cy = (int)Math.Floor((z + half) / tileSize) * tileSize - half;
        return (cx, cy);
    }

    /// Pick the `<Center>` for `freeskate_dlc_*.xml`. It must be a tile centre
    /// that exists in `centers` — retail always uses a real tile; using a
    /// stale or far-offset position leaves the primary stream slot empty and
    /// the player falls through the floor.
    /// Order: tile under `preferredWorldX`/`preferredWorldZ`, otherwise the
    /// scanned centre nearest to that world position in XZ.
    public static (int cx, int cy) ResolveFreeskateStreamTileCenter(
        HashSet<(int cx, int cy)> centers,
        float preferredWorldX,
        float preferredWorldZ,
        int tileSize = DefaultTileSize)
    {
        if (centers.Count == 0)
            return PositionToTileCenter(preferredWorldX, preferredWorldZ, tileSize);

        var fromPreferred = PositionToTileCenter(preferredWorldX, preferredWorldZ, tileSize);
        if (centers.Contains(fromPreferred))
            return fromPreferred;

        return centers
            .OrderBy(c => DistSqToPoint(c.cx, c.cy, preferredWorldX, preferredWorldZ))
            .ThenBy(c => c.cx)
            .ThenBy(c => c.cy)
            .First();

        static double DistSqToPoint(int cx, int cy, float wx, float wz)
        {
            double dx = cx - wx, dz = cy - wz;
            return dx * dx + dz * dz;
        }
    }

    /// Per-area freeskate stream XML — distinct from world-level `_Pres/_Sim`.
    /// Retail uses `<StreamTiles Count="1">` with one `<Center>` on the spawn
    /// tile and `<Tile>` children listing the streamed footprint. Capped at
    /// `FreeskateAreaStreamMaxTiles` tiles nearest `<Center>` in world XY.
    public static string BuildFreeskateAreaStreamXml(
        HashSet<(int cx, int cy)> centers,
        int spawnTileCx,
        int spawnTileCy)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"us-ascii\"?>");
        sb.AppendLine("<StreamTiles Count=\"1\">");
        sb.AppendLine("  <StreamTile>");
        sb.AppendLine($"    <Center>{spawnTileCx}, {spawnTileCy}</Center>");

        int budget = FreeskateAreaStreamMaxTiles;
        var footprint = new List<(int cx, int cy)>();
        bool spawnKnown = centers.Count > 0 && centers.Contains((spawnTileCx, spawnTileCy));
        if (spawnKnown) footprint.Add((spawnTileCx, spawnTileCy));

        int remainder = spawnKnown ? budget - 1 : budget;
        if (remainder > 0 && centers.Count > 0)
        {
            var ordered = centers
                .Where(c => !(c.cx == spawnTileCx && c.cy == spawnTileCy))
                .OrderBy(c => DistSq(c.cx, c.cy, spawnTileCx, spawnTileCy))
                .ThenBy(c => c.cx)
                .ThenBy(c => c.cy)
                .Take(remainder);
            footprint.AddRange(ordered);
        }

        foreach (var (cx, cy) in footprint)
            sb.AppendLine($"    <Tile>{cx}, {cy}</Tile>");

        sb.AppendLine("  </StreamTile>");
        sb.AppendLine("</StreamTiles>");
        return sb.ToString();

        static long DistSq(int ax, int az, int bx, int bz)
        {
            long dx = ax - bx, dz = az - bz;
            return dx * dx + dz * dz;
        }
    }

    /// World Presentation stream XML — per-centre ±tileSize stencil,
    /// intersected with scanned tile centres. Identical markup to Sim.
    public static string BuildDistWorldPresStreamTilesXml(HashSet<(int cx, int cy)> centers, int tileSize = DefaultTileSize) =>
        BuildDistWorldStreamTilesInner(centers, tileSize);

    /// World Simulation stream — same 3×3 stencil as Presentation.
    public static string BuildDistWorldSimStreamTilesXml(HashSet<(int cx, int cy)> centers, int tileSize = DefaultTileSize) =>
        BuildDistWorldStreamTilesInner(centers, tileSize);

    private static string BuildDistWorldStreamTilesInner(HashSet<(int cx, int cy)> centers, int tileSize)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"us-ascii\"?>");
        sb.AppendLine("<StreamTiles>");
        const int FallbackHalfWidth = DefaultTileSize;

        if (centers.Count == 0)
        {
            sb.AppendLine("  <StreamTile>");
            sb.AppendLine("    <Center>0, 0</Center>");
            foreach (int dx in new[] { -FallbackHalfWidth, 0, FallbackHalfWidth })
                foreach (int dy in new[] { -FallbackHalfWidth, 0, FallbackHalfWidth })
                {
                    if (dx == 0 && dy == 0) continue;
                    sb.AppendLine($"    <Tile>{dx}, {dy}</Tile>");
                }
            sb.AppendLine("  </StreamTile>");
            sb.AppendLine("</StreamTiles>");
            return sb.ToString();
        }

        foreach (var (cx, cy) in centers.OrderBy(c => c.cx).ThenBy(c => c.cy))
        {
            sb.AppendLine("  <StreamTile>");
            sb.AppendLine($"    <Center>{cx}, {cy}</Center>");
            var tilesForBlock = new List<(int nx, int ny)>();
            for (int dx = -tileSize; dx <= tileSize; dx += tileSize)
                for (int dy = -tileSize; dy <= tileSize; dy += tileSize)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = cx + dx, ny = cy + dy;
                    if (centers.Contains((nx, ny))) tilesForBlock.Add((nx, ny));
                }
            foreach (var (nx, ny) in tilesForBlock.OrderBy(t => t.nx).ThenBy(t => t.ny))
                sb.AppendLine($"    <Tile>{nx}, {ny}</Tile>");
            sb.AppendLine("  </StreamTile>");
        }
        sb.AppendLine("</StreamTiles>");
        return sb.ToString();
    }

    /// Boundary XML — 4-point rectangle bounding all tile centres ± half tile.
    /// Engine point-in-polygon-tests this against the player's position to
    /// derive the `tTriggerVolumeInstanceID` that `sub_22D078` (the freeskate
    /// gate) matches on.
    public static string BuildFreeskateBoundaryXml(HashSet<(int cx, int cy)> centers, int tileSize = DefaultTileSize)
    {
        int half = tileSize / 2;
        int minX, maxX, minY, maxY;
        if (centers.Count == 0)
        {
            // 3×3-tile placeholder (matches the stream fallback).
            minX = -tileSize - half; maxX = tileSize + half;
            minY = -tileSize - half; maxY = tileSize + half;
        }
        else
        {
            minX = centers.Min(c => c.cx) - half;
            maxX = centers.Max(c => c.cx) + half;
            minY = centers.Min(c => c.cy) - half;
            maxY = centers.Max(c => c.cy) + half;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"us-ascii\"?>");
        sb.AppendLine("<Boundary>");
        sb.AppendLine($"  <Point>{minX}, {minY}</Point>");
        sb.AppendLine($"  <Point>{maxX}, {minY}</Point>");
        sb.AppendLine($"  <Point>{maxX}, {maxY}</Point>");
        sb.AppendLine($"  <Point>{minX}, {maxY}</Point>");
        sb.AppendLine("</Boundary>");
        return sb.ToString();
    }
}
