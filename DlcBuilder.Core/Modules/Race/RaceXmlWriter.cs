using System.Globalization;
using System.Numerics;
using System.Text;
using DlcBuilder.Inputs;
using DlcBuilder.Modules.DlcManifest.Xml;

namespace DlcBuilder.Modules.Race;

/// Per-race `boundary/&lt;key&gt;.xml` + `stream/&lt;key&gt;.xml` writer.
///
/// Stock retail ships these for **every** race (`StockGameData/.../boundary/race_dwtn_01.xml`
/// + `stream/race_dwtn_01.xml`, plus `_ol` death-race variants). Without them
/// the engine's challenge-launch path tries to open the files, fails, and
/// surfaces "you don't have the current DLC installed to play it" — which
/// is the exact symptom seen when launching a race that's listed in the FE
/// but missing these companion XMLs.
///
/// **Boundary** — `&lt;Boundary&gt;` polygon. Stock uses **floating-point** XZ
/// points (unlike OTS which uses integers). Our writer emits a 4-corner
/// AABB rectangle padded out from the gates' world-space envelope; the
/// engine treats this as the race's "must stay inside this area" footprint.
///
/// **Stream** — `&lt;StreamTiles Count="1"&gt;&lt;StreamTile&gt;&lt;Center&gt;Cx, Cy&lt;/Center&gt;...`
/// listing the 100×100 tile centres the race spans. Same shape as
/// freeskate-area stream XMLs, so we reuse
/// <see cref="FreeskateAreaXmlBuilder.BuildFreeskateAreaStreamXml"/>.
public static class RaceXmlWriter
{
    /// Padding (metres) added on each side of the gate AABB when computing
    /// the boundary polygon. Gives the player a bit of room to manoeuvre
    /// without immediately tripping the boundary on a wide approach.
    private const float BoundaryPaddingMetres = 10f;

    /// Write boundary + stream XML for one race. Returns the two output paths
    /// for orchestrator bookkeeping.
    public static void Write(
        RaceChallengeSpec spec,
        MapInput mapInput,
        string stagingDataDir,
        IList<string> writtenFiles)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(mapInput);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDataDir);
        ArgumentNullException.ThrowIfNull(writtenFiles);

        string boundaryDir = Path.Combine(stagingDataDir, "boundary");
        string streamDir = Path.Combine(stagingDataDir, "stream");
        Directory.CreateDirectory(boundaryDir);
        Directory.CreateDirectory(streamDir);

        string boundaryPath = Path.Combine(boundaryDir, spec.ChallengeKey + ".xml");
        File.WriteAllText(boundaryPath, BuildBoundaryXml(spec));
        writtenFiles.Add(boundaryPath);

        string streamPath = Path.Combine(streamDir, spec.ChallengeKey + ".xml");
        File.WriteAllText(streamPath, BuildStreamXml(spec, mapInput));
        writtenFiles.Add(streamPath);
    }

    /// `<Boundary>` polygon. Stock retail uses irregular hand-authored
    /// polygons that hug the race line; we approximate with a 4-corner AABB
    /// rectangle covering all gate volumes plus a configurable margin.
    /// Adequate for the engine's point-in-polygon containment check.
    public static string BuildBoundaryXml(RaceChallengeSpec spec)
    {
        // AABB of all gate volume corners (each gate's centre ± half-extents
        // contributes 4 XZ corners). Falls back to a 50m square around the
        // start anchor if there are no gates — race validator already errors
        // on empty gates so this branch is defensive only.
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        bool any = false;
        foreach (RaceGateSpec gate in spec.AllGates)
        {
            Vector3 c = gate.Volume.Center;
            Vector3 h = gate.Volume.HalfExtents;
            minX = MathF.Min(minX, c.X - h.X);
            maxX = MathF.Max(maxX, c.X + h.X);
            minZ = MathF.Min(minZ, c.Z - h.Z);
            maxZ = MathF.Max(maxZ, c.Z + h.Z);
            any = true;
        }
        if (!any)
        {
            minX = spec.AnchorPosition.X - 25f; maxX = spec.AnchorPosition.X + 25f;
            minZ = spec.AnchorPosition.Z - 25f; maxZ = spec.AnchorPosition.Z + 25f;
        }

        minX -= BoundaryPaddingMetres; maxX += BoundaryPaddingMetres;
        minZ -= BoundaryPaddingMetres; maxZ += BoundaryPaddingMetres;

        var sb = new StringBuilder(256);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"us-ascii\"?>");
        sb.AppendLine("<Boundary>");
        // 4 corners CCW (looking down +Y). Floating-point format with 4
        // decimal digits — matches the precision retail uses for the same
        // file (e.g. `race_dwtn_01.xml` ships values like -94.94788).
        AppendPoint(sb, minX, minZ);
        AppendPoint(sb, maxX, minZ);
        AppendPoint(sb, maxX, maxZ);
        AppendPoint(sb, minX, maxZ);
        sb.AppendLine("</Boundary>");
        return sb.ToString();
    }

    /// `<StreamTiles>` listing every 100×100 tile centre the race AABB
    /// overlaps. Format matches stock retail / freeskate-area stream XML
    /// exactly via `FreeskateAreaXmlBuilder.BuildFreeskateAreaStreamXml`.
    ///
    /// For stub-only DISTs (no actual tile content on disk), we synthesise
    /// the candidate tile set from the race's own AABB — the engine still
    /// reads the tile centres for streaming bookkeeping even if the tile
    /// content lives in a different (base-game) world stream.
    public static string BuildStreamXml(RaceChallengeSpec spec, MapInput mapInput)
    {
        // AABB centroid → tile centre. Reuse freeskate's grid helper so the
        // stride / offset matches base-game tile geometry exactly.
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        bool any = false;
        foreach (RaceGateSpec gate in spec.AllGates)
        {
            Vector3 c = gate.Volume.Center;
            Vector3 h = gate.Volume.HalfExtents;
            minX = MathF.Min(minX, c.X - h.X);
            maxX = MathF.Max(maxX, c.X + h.X);
            minZ = MathF.Min(minZ, c.Z - h.Z);
            maxZ = MathF.Max(maxZ, c.Z + h.Z);
            any = true;
        }
        if (!any)
        {
            minX = spec.AnchorPosition.X - 25f; maxX = spec.AnchorPosition.X + 25f;
            minZ = spec.AnchorPosition.Z - 25f; maxZ = spec.AnchorPosition.Z + 25f;
        }
        float aabbCx = (minX + maxX) * 0.5f;
        float aabbCz = (minZ + maxZ) * 0.5f;
        var (spawnTileCx, spawnTileCy) = FreeskateAreaXmlBuilder.PositionToTileCenter(aabbCx, aabbCz);

        // Candidate tile set. Preferred: scan the parent DIST for real
        // `cPres_<cx>_<cy>_high.psf` tiles. Fallback (stub-only DIST):
        // synthesise tiles from the race AABB so the stream XML lists *some*
        // tiles even when the DIST ships only manifest stubs.
        HashSet<(int cx, int cy)> centers = FreeskateAreaXmlBuilder.ScanDistTileCenters(mapInput.DistFolderPath);
        if (centers.Count == 0)
            centers = SynthesizeTileGrid(minX, minZ, maxX, maxZ);

        return FreeskateAreaXmlBuilder.BuildFreeskateAreaStreamXml(centers, spawnTileCx, spawnTileCy);
    }

    /// Synthesise 100×100 tile centres covering the race AABB. Used when
    /// the source DIST has no shipped tile content (stub-only authoring
    /// against an existing base-game world). Tile grid offset (centre at
    /// multiples of 100 plus 50) matches the base-game tile convention so
    /// the engine's tile lookup finds the matching base-game-loaded tiles.
    private static HashSet<(int cx, int cy)> SynthesizeTileGrid(
        float minX, float minZ, float maxX, float maxZ)
    {
        var set = new HashSet<(int, int)>();
        var (minTileCx, minTileCy) = FreeskateAreaXmlBuilder.PositionToTileCenter(minX, minZ);
        var (maxTileCx, maxTileCy) = FreeskateAreaXmlBuilder.PositionToTileCenter(maxX, maxZ);
        for (int cy = minTileCy; cy <= maxTileCy; cy += 100)
            for (int cx = minTileCx; cx <= maxTileCx; cx += 100)
                set.Add((cx, cy));
        return set;
    }

    private static void AppendPoint(StringBuilder sb, float x, float z) =>
        sb.AppendFormat(CultureInfo.InvariantCulture, "  <Point>{0:0.####}, {1:0.####}</Point>{2}",
            x, z, Environment.NewLine);
}
