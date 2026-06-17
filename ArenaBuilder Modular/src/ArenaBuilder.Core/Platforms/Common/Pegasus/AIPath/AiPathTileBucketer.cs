namespace ArenaBuilder.Core.Platforms.Common.Pegasus.AIPath;

/// <summary>
/// Assigns paths to cSim_X_Y_high stream tiles by spatial bounding box.
///
/// Skate 3 tile convention (observed against DWMC DLC at
/// content/world/stream/DLC_DW_MegaCompund/): every cSim tile is a 100 m square
/// centered on (X, Y) where X,Y ∈ {..., -150, -50, 50, 150, 250, ...}. The tile
/// covers world XY rectangle [X-50, X+50) × [Y-50, Y+50) and is named
/// "cSim_X_Y_high" (note the cSim folder uses X then Y, with Y being the world
/// Z axis -- Skate uses Y-up, so the "ground plane" tiles are X/Z).
///
/// A path may overlap more than one tile, in which case it must be included in
/// each overlapping tile's PSG -- the engine streams each tile independently and
/// only the path nodes physically present in the loaded tile are available to
/// the AI. Duplicating across overlapping tiles is what makes a path appear
/// continuous to a skater crossing the seam (confirmed against
/// cSim_-50_-50_high which ships two AIPath PSGs).
/// </summary>
public static class AiPathTileBucketer
{
    /// <summary>Default tile edge length, in world units (meters).</summary>
    public const float DefaultTileSize = 100f;

    /// <summary>
    /// Default tile-center offset from the world grid: tiles are centered at
    /// multiples of TileSize plus this offset. DWMC corpus uses 50, so tile
    /// centers are at ±50, ±150, ±250, etc.
    /// </summary>
    public const float DefaultTileCenterOffset = 50f;

    /// <summary>
    /// Extra meters added on every side of the tile when testing overlap.
    /// Keeps paths whose endpoints just touch a tile boundary inside the tile's
    /// PSG (engine load-time branch synthesis needs both endpoints loaded
    /// simultaneously to form a seam branch).
    /// </summary>
    public const float TileMargin = 5f;

    public readonly record struct TileKey(int X, int Y)
    {
        public string FolderName => $"cSim_{X}_{Y}_high";
    }

    public readonly record struct Bbox(float MinX, float MinY, float MinZ,
                                       float MaxX, float MaxY, float MaxZ)
    {
        public static Bbox FromPositions(IEnumerable<(float X, float Y, float Z)> pts)
        {
            float mnx = float.PositiveInfinity, mny = float.PositiveInfinity, mnz = float.PositiveInfinity;
            float mxx = float.NegativeInfinity, mxy = float.NegativeInfinity, mxz = float.NegativeInfinity;
            int seen = 0;
            foreach (var (x, y, z) in pts)
            {
                if (x < mnx) mnx = x; if (x > mxx) mxx = x;
                if (y < mny) mny = y; if (y > mxy) mxy = y;
                if (z < mnz) mnz = z; if (z > mxz) mxz = z;
                seen++;
            }
            if (seen == 0)
                throw new ArgumentException("cannot bbox an empty point set", nameof(pts));
            return new Bbox(mnx, mny, mnz, mxx, mxy, mxz);
        }
    }

    /// <summary>
    /// Enumerate every cSim tile that <paramref name="bbox"/> overlaps. World XZ
    /// plane drives tile selection (Skate 3 tiles ignore world Y / elevation).
    /// tileSize / tileCenterOffset default to the Skate 3 convention (100 / 50);
    /// callers from the GLB pipeline pass values from <c>TileBuildOptions</c>
    /// to keep folder naming aligned with the cSim collision tiles.
    /// </summary>
    public static IEnumerable<TileKey> TilesFor(Bbox bbox,
                                                  float tileSize         = DefaultTileSize,
                                                  float tileCenterOffset = DefaultTileCenterOffset)
    {
        // Tile centers are at (tileCenterOffset + k * tileSize) for any integer k.
        // For an XZ point P, the tile center index is k = floor((P - tileCenterOffset) / tileSize + 0.5).
        // Bbox bounds + margin then expand to all (k_x, k_z) in the overlap region.
        float xMin = bbox.MinX - TileMargin;
        float xMax = bbox.MaxX + TileMargin;
        float zMin = bbox.MinZ - TileMargin;
        float zMax = bbox.MaxZ + TileMargin;

        int kxLo = TileCenterIndex(xMin, tileSize, tileCenterOffset);
        int kxHi = TileCenterIndex(xMax, tileSize, tileCenterOffset);
        int kzLo = TileCenterIndex(zMin, tileSize, tileCenterOffset);
        int kzHi = TileCenterIndex(zMax, tileSize, tileCenterOffset);

        for (int kx = kxLo; kx <= kxHi; kx++)
        {
            int tileX = (int)Math.Round(tileCenterOffset + kx * tileSize);
            for (int kz = kzLo; kz <= kzHi; kz++)
            {
                int tileZ = (int)Math.Round(tileCenterOffset + kz * tileSize);
                yield return new TileKey(tileX, tileZ);
            }
        }
    }

    /// <summary>
    /// Group a set of (path, bbox) pairs into per-tile buckets. Each path can land
    /// in multiple buckets; the returned dictionary value lists every original path
    /// index that overlaps that tile.
    /// </summary>
    public static IReadOnlyDictionary<TileKey, IReadOnlyList<int>> Bucket(
        IReadOnlyList<Bbox> pathBboxes,
        float tileSize         = DefaultTileSize,
        float tileCenterOffset = DefaultTileCenterOffset)
    {
        var buckets = new Dictionary<TileKey, List<int>>();
        for (int i = 0; i < pathBboxes.Count; i++)
        {
            foreach (var tile in TilesFor(pathBboxes[i], tileSize, tileCenterOffset))
            {
                if (!buckets.TryGetValue(tile, out var list))
                {
                    list = new List<int>();
                    buckets[tile] = list;
                }
                list.Add(i);
            }
        }
        return buckets.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<int>)kv.Value);
    }

    private static int TileCenterIndex(float worldCoord, float tileSize, float tileCenterOffset)
    {
        // worldCoord lies inside tile k if (tileCenterOffset + (k-0.5) * tileSize)
        //   <= worldCoord < (tileCenterOffset + (k+0.5) * tileSize).
        // Solving for k: k = floor((worldCoord - tileCenterOffset) / tileSize + 0.5).
        return (int)Math.Floor((worldCoord - tileCenterOffset) / tileSize + 0.5f);
    }
}
