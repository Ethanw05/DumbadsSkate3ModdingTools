using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArenaBuilder.Core.Platforms.Common.Pegasus.WorldPainter;
using ArenaBuilder.Core.Platforms.PS3.Pegasus.WorldPainter;
using ArenaBuilder.Glb;

namespace ArenaBuilder.WorldPainter;

/// <summary>
/// Scans <c>cSim_*_high</c> directories under a source folder and writes one WP PSG per
/// unique 128 m GenTileId cell.  No intermediate JSON file is involved.
/// </summary>
public static class WorldPainterExporter
{
    private const float WpCellHalf = 64f;
    private static readonly JsonSerializerOptions DebugJsonOptions = new() { WriteIndented = true };

    public readonly record struct ExportResult(int Emitted, int Skipped);
    private readonly record struct DebugLeaf(double MinX, double MaxX, double MinZ, double MaxZ, bool Void, uint Lo, uint Hi, int Depth);
    private sealed record DebugLayer(string LayerGuidHex, int NodeCount, int SlotCount, List<DebugLeaf> Leaves);
    private sealed record TileQuadDebugDocument(
        int SchemaVersion,
        string Source,
        int TileU,
        int TileV,
        double RootCenterX,
        double RootCenterZ,
        double RootHalfX,
        double RootHalfZ,
        double TileMinX,
        double TileMaxX,
        double TileMinZ,
        double TileMaxZ,
        List<DebugLayer> Layers);

    /// <summary>
    /// Build and write WorldPainter PSGs into every <c>cSim_*_high</c> folder found under
    /// <paramref name="sourceFolder"/>.
    /// </summary>
    /// <param name="sourceFolder">Root folder that contains <c>cSim_*_high</c> sub-directories.</param>
    /// <param name="cols">Paint grid column count (X axis).</param>
    /// <param name="rows">Paint grid row count (Z axis, row 0 = south = min Z).</param>
    /// <param name="minX">World X of the paint grid west edge.</param>
    /// <param name="minZ">World Z of the paint grid south edge.</param>
    /// <param name="maxX">World X of the paint grid east edge.</param>
    /// <param name="maxZ">World Z of the paint grid north edge.</param>
    /// <param name="grids">Per-layer cell arrays (only layers with at least one painted cell need be included).</param>
    /// <param name="tileSize">Stream tile size in metres (default 100 m).</param>
    /// <param name="originX">Stream tile grid origin X (default 0).</param>
    /// <param name="originZ">Stream tile grid origin Z (default 0).</param>
    /// <param name="log">Optional progress/info callback.</param>
    public static ExportResult Export(
        string sourceFolder,
        int cols, int rows,
        double minX, double minZ, double maxX, double maxZ,
        IReadOnlyDictionary<ulong, WpCell[]> grids,
        float tileSize = 100f,
        float originX = 0f,
        float originZ = 0f,
        Action<string>? log = null)
    {
        log ??= _ => { };
        string debugRoot = Path.Combine(sourceFolder, "_worldpainter_debug");

        // Match cSim_<cx>_<cz>_high where coordinates may be signed integers or decimals.
        var folderPattern = new Regex(
            @"^cSim_(-?[\d]+(?:\.\d+)?)_(-?[\d]+(?:\.\d+)?)_high$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var matchedFolders = Directory
            .GetDirectories(sourceFolder)
            .Select(path => (Path: path, Name: Path.GetFileName(path)))
            .Select(x => (x.Path, x.Name, Match: folderPattern.Match(x.Name)))
            .Where(x => x.Match.Success)
            .Select(x =>
            {
                float cx = float.Parse(x.Match.Groups[1].Value, CultureInfo.InvariantCulture);
                float cz = float.Parse(x.Match.Groups[2].Value, CultureInfo.InvariantCulture);
                // Tile U/V from center: center = originX + (U + 0.5) * tileSize → U = floor((cx - originX) / tileSize)
                int tileU = (int)MathF.Floor((cx - originX) / tileSize);
                int tileV = (int)MathF.Floor((cz - originZ) / tileSize);
                return (x.Path, x.Name, TileU: tileU, TileV: tileV, CenterX: cx, CenterZ: cz);
            })
            .ToList();

        if (matchedFolders.Count == 0)
        {
            log("[WorldPainter] No cSim_*_high folders found — nothing to export.");
            return new ExportResult(0, 0);
        }

        // Determine which folder "owns" each unique 128 m WP cell (GenTileId).
        // When multiple stream tiles share a WP cell we write the PSG only into the first folder
        // whose tile center is closest to the WP root (or first by U then V as tiebreak).
        var cellOwner = new Dictionary<uint, (string Dir, int TileU, int TileV, Vector2 WpRoot)>();

        foreach (var (path, name, tileU, tileV, cx, cz) in matchedFolders)
        {
            var tile = new WorldTileGrid.TileKey(tileU, tileV);
            Vector2 wpRoot = WorldTileGrid.GetWorldPainterQuadRootCenter(tile, tileSize, originX, originZ);
            uint genId = WorldTileGrid.GenTileIdSkate(wpRoot.X, wpRoot.Y);

            if (cellOwner.TryGetValue(genId, out var existing))
            {
                // Pick the tile closest to the WP root center (matching the pipeline's owner logic).
                float dx0 = cx - wpRoot.X, dz0 = cz - wpRoot.Y;
                float ex = existing.WpRoot.X - wpRoot.X, ez = existing.WpRoot.Y - wpRoot.Y;
                // Use tile center as the comparator (existing owner's center vs new).
                float existCx = originX + (existing.TileU + 0.5f) * tileSize;
                float existCz = originZ + (existing.TileV + 0.5f) * tileSize;
                float d2New = dx0 * dx0 + dz0 * dz0;
                float d2Exist = (existCx - wpRoot.X) * (existCx - wpRoot.X) + (existCz - wpRoot.Y) * (existCz - wpRoot.Y);
                bool newWins = d2New < d2Exist - 1e-4f
                    || (MathF.Abs(d2New - d2Exist) < 1e-4f && (tileU < existing.TileU || (tileU == existing.TileU && tileV < existing.TileV)));
                if (newWins)
                    cellOwner[genId] = (path, tileU, tileV, wpRoot);
            }
            else
            {
                cellOwner[genId] = (path, tileU, tileV, wpRoot);
            }
        }

        int emitted = 0, skipped = 0;

        foreach (var (genId, (dir, tileU, tileV, wpRoot)) in cellOwner)
        {
            double tileMinX = wpRoot.X - WpCellHalf;
            double tileMaxX = wpRoot.X + WpCellHalf;
            double tileMinZ = wpRoot.Y - WpCellHalf;
            double tileMaxZ = wpRoot.Y + WpCellHalf;

            var layerTrees = new List<WorldPainterPsgBuilder.WorldPainterLayerTreeSpec>();
            var debugLayers = new List<DebugLayer>();

            foreach (var (layerGuid, cells) in grids)
            {
                var result = WorldPainterCellQuadTreeBuilder.Build(
                    cols, rows, minX, minZ, maxX, maxZ, cells,
                    tileMinX, tileMaxX, tileMinZ, tileMaxZ);

                if (result == null)
                    continue;

                var (nodes, slots) = result.Value;
                WorldPainterQuadTreeDataBuilder.CountQuadNodeKinds(nodes, out int internalNodes, out int leaves);
                log($"  Layer 0x{layerGuid:X16}: nodes={nodes.Count} (internal={internalNodes}, leaves={leaves}), slots={slots.Count}");
                layerTrees.Add(new WorldPainterPsgBuilder.WorldPainterLayerTreeSpec(layerGuid, nodes, slots));
                debugLayers.Add(new DebugLayer(
                    LayerGuidHex: $"0x{layerGuid:X16}",
                    NodeCount: nodes.Count,
                    SlotCount: slots.Count,
                    Leaves: BuildDebugLeaves(nodes, slots, tileMinX, tileMaxX, tileMinZ, tileMaxZ)));
            }

            bool hasPaint = layerTrees.Count > 0;
            if (!hasPaint)
                log($"[WorldPainter] Tile ({tileU},{tileV}) has no paint — writing DefaultLayers.");

            var wpOptions = new WorldPainterPsgBuilder.WorldPainterPsgBuildOptions
            {
                ArenaId = 0x5750474Cu,
                RootCenterX = wpRoot.X,
                RootCenterY = wpRoot.Y,
                RootHalfX = WpCellHalf,
                RootHalfY = WpCellHalf,
                LayerTrees = hasPaint ? layerTrees : null,
                OmitDefaultLayerSeedFallback = hasPaint,
                OmitUnpaintedDefaultLayerSeeds = hasPaint,
                TocGuidSalt = FormattableString.Invariant($"{tileU}_{tileV}_{wpRoot.X:R}_{wpRoot.Y:R}")
            };

            string wpHash = ArenaBuilder.Core.Lookup8Hash.HashStringToHex(
                $"worldpainter_{tileU}_{tileV}");
            string wpOutPath = Path.Combine(dir, wpHash + ".psg");
            WorldPainterPsgBuilder.WriteMinimal(wpOutPath, wpOptions);
            log($"[WorldPainter PSG] {wpOutPath}  root ({wpRoot.X:F1},{wpRoot.Y:F1}) GenTileId=0x{genId:X8} {(hasPaint ? $"{layerTrees.Count} layer(s)" : "DefaultLayers")}");
            WriteQuadDebugJson(debugRoot, tileU, tileV, wpRoot, tileMinX, tileMaxX, tileMinZ, tileMaxZ, debugLayers, log);
            emitted++;
        }

        // Report skipped (secondary) tiles per shared WP cell.
        foreach (var (path, name, tileU, tileV, cx, cz) in matchedFolders)
        {
            var tile = new WorldTileGrid.TileKey(tileU, tileV);
            Vector2 wpRoot = WorldTileGrid.GetWorldPainterQuadRootCenter(tile, tileSize, originX, originZ);
            uint genId = WorldTileGrid.GenTileIdSkate(wpRoot.X, wpRoot.Y);
            if (cellOwner.TryGetValue(genId, out var owner) && owner.Dir != path)
            {
                log($"[WorldPainter] Skipping {name} — GenTileId 0x{genId:X8} covered by {Path.GetFileName(owner.Dir)}.");
                skipped++;
            }
        }

        log($"[WorldPainter] Done: {emitted} PSG(s) written, {skipped} tile(s) skipped (shared WP cell).");
        return new ExportResult(emitted, skipped);
    }

    private static void WriteQuadDebugJson(
        string debugRoot, int tileU, int tileV, Vector2 wpRoot,
        double tileMinX, double tileMaxX, double tileMinZ, double tileMaxZ,
        List<DebugLayer> debugLayers,
        Action<string> log)
    {
        try
        {
            Directory.CreateDirectory(debugRoot);
            var doc = new TileQuadDebugDocument(
                SchemaVersion: 1,
                Source: "ArenaBuilder.WorldPainter.WorldPainterExporter",
                TileU: tileU,
                TileV: tileV,
                RootCenterX: wpRoot.X,
                RootCenterZ: wpRoot.Y,
                RootHalfX: WpCellHalf,
                RootHalfZ: WpCellHalf,
                TileMinX: tileMinX,
                TileMaxX: tileMaxX,
                TileMinZ: tileMinZ,
                TileMaxZ: tileMaxZ,
                Layers: debugLayers);
            string path = Path.Combine(debugRoot, $"worldpainter_{tileU}_{tileV}_quadtree_debug.json");
            File.WriteAllText(path, JsonSerializer.Serialize(doc, DebugJsonOptions));
            log($"[WorldPainter Debug] {path}");
        }
        catch (Exception ex)
        {
            log($"[WARN] Could not write WP quadtree debug JSON for tile ({tileU},{tileV}): {ex.Message}");
        }
    }

    private static List<DebugLeaf> BuildDebugLeaves(
        IReadOnlyList<WorldPainterQuadTreeDataBuilder.WorldPainterQuadNode> nodes,
        IReadOnlyList<IReadOnlyList<uint>> slots,
        double tileMinX, double tileMaxX, double tileMinZ, double tileMaxZ)
    {
        var leaves = new List<DebugLeaf>(Math.Max(8, nodes.Count / 2));
        if (nodes.Count == 0)
            return leaves;

        var stack = new Stack<(int Node, double X0, double X1, double Z0, double Z1, int Depth)>();
        stack.Push((0, tileMinX, tileMaxX, tileMinZ, tileMaxZ, 0));

        while (stack.Count > 0)
        {
            var (idx, x0, x1, z0, z1, depth) = stack.Pop();
            if ((uint)idx >= (uint)nodes.Count)
                continue;

            var n = nodes[idx];
            bool leaf = n.Child0 == -1;
            if (leaf)
            {
                bool isVoid = n.DictionaryLookup == 0xFFFF;
                uint lo = 0, hi = 0;
                if (!isVoid && n.DictionaryLookup < slots.Count)
                {
                    var slot = slots[n.DictionaryLookup];
                    if (slot.Count >= 2)
                    {
                        lo = slot[0];
                        hi = slot[1];
                    }
                }
                leaves.Add(new DebugLeaf(x0, x1, z0, z1, isVoid, lo, hi, depth));
                continue;
            }

            double mx = (x0 + x1) * 0.5;
            double mz = (z0 + z1) * 0.5;
            // Push reverse traversal order so pop processes SW, NW, SE, NE.
            stack.Push((n.Child3, mx, x1, mz, z1, depth + 1)); // NE
            stack.Push((n.Child2, mx, x1, z0, mz, depth + 1)); // SE
            stack.Push((n.Child1, x0, mx, mz, z1, depth + 1)); // NW
            stack.Push((n.Child0, x0, mx, z0, mz, depth + 1)); // SW
        }

        return leaves;
    }
}
