using System.Collections.Generic;
using ArenaBuilder.Core;
using DlcBuilder.Inputs;
using DlcBuilder.Modules.DlcManifest;
using DlcBuilder.Modules.LocatorPsg;
using DlcBuilder.Modules.LocXml;
using DlcBuilder.Outputs;

namespace DlcBuilder.Modules.OtsPsg;

/// Bundle of artifacts produced for one OTS challenge.
public sealed record OtsArtifacts
{
    public required string ChallengeKey { get; init; }
    /// Boundary polygon XML. Always produced.
    public required string BoundaryXml { get; init; }
    /// Stream tile XML. Always produced.
    public required string StreamXml { get; init; }
    /// Sim.loc XML (locator transforms). Always produced.
    public required string LocXml { get; init; }
    /// cSim_Global.psg bytes (trigger volumes + locators wrapped in RW4 PS3
    /// arena). Null when the PSG body port isn't yet implemented (current
    /// state — depends on the ArenaBuilder.Collision pipeline port).
    public byte[]? PsgBytes { get; init; }
}

/// Builds the per-OTS-challenge artifacts. Today the XML companions
/// (boundary, stream, .loc) are fully produced; the PSG body is deferred —
/// it depends on the ArenaBuilder.Collision pipeline (ClusteredMesh + arena
/// composition) which is a separate, sizeable port.
///
/// Front-ends can call this today and stage the XML files on disk; the PSG
/// will land here once the collision-mesh port is done.
public static class OtsPsgBuilder
{
    /// Build everything for one OTS challenge: boundary.xml, stream.xml,
    /// `<key>_Sim.loc`, and the `cSim_Global.psg` bytes.
    public static OtsArtifacts Build(OtsChallengeSpec spec, IList<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(diagnostics);

        string boundaryXml = BuildBoundaryXml(spec);
        string streamXml = BuildStreamXml(spec);
        string locXml = BuildSimLocXml(spec);

        // PSG bytes — bundles the LocationDescData (chevrons / start / vis /
        // wait sub-locators) with the ClusteredMesh-backed trigger volumes.
        byte[]? psgBytes = null;
        try
        {
            // Each named OTS locator becomes a top-level tLocationDesc so
            // RegArena registers them by name in cLocationManager. The
            // anchor itself is NOT shipped in the per-mission PSG (matches
            // DW: ots_dwmc_01 cSim_Global PSG has chev/vis/start/wait, no
            // `<key>_challengelocator_01`); the anchor lives in the world
            // Sim.loc + DLC_BAM global_locator PSG instead.
            var locators = OtsLocatorPlanner.PlanMissionPsgLocators(spec);
            using var ms = new MemoryStream();
            OtsPsgBytesBuilder.Build(spec.ChallengeKey, spec.Triggers, locators, ms);
            psgBytes = ms.ToArray();
            diagnostics.Add(new Diagnostic(DiagnosticLevel.Info, "OtsPsg",
                $"[{spec.ChallengeKey}] Built boundary.xml ({boundaryXml.Length}B), stream.xml ({streamXml.Length}B), Sim.loc ({locXml.Length}B), cSim_Global.psg ({psgBytes.Length}B)."));
        }
        catch (Exception ex)
        {
            diagnostics.Add(new Diagnostic(DiagnosticLevel.Error, "OtsPsg",
                $"[{spec.ChallengeKey}] PSG body build failed: {ex.GetType().Name}: {ex.Message}"));
        }

        return new OtsArtifacts
        {
            ChallengeKey = spec.ChallengeKey,
            BoundaryXml = boundaryXml,
            StreamXml = streamXml,
            LocXml = locXml,
            PsgBytes = psgBytes,
        };
    }

    /// boundary/&lt;key&gt;.xml — the "must stay inside" challenge polygon.
    /// Mirrors MinimalDlcBuilder/OtsChallengeBuilder.cs:1190-1199 byte-for-byte:
    ///   • XML declaration with us-ascii encoding (the engine's parser keys
    ///     off it; we previously omitted it and the parser dropped the file).
    ///   • Singular `<Boundary>` root element, NOT `<Boundaries>` plural with a
    ///     `<BoundaryPolygon>` wrapper. Wrong shape => parser fails silently
    ///     => engine has no boundary => OTS challenge is silently dropped from
    ///     the discoverable-marker pass and the FE listing.
    ///   • Points are rounded to integers ("100, 200"), not F6 floats. The
    ///     engine's parser uses int.Parse on each comma-split token, and
    ///     barfs on a decimal point.
    public static string BuildBoundaryXml(OtsChallengeSpec spec)
    {
        var sb = new System.Text.StringBuilder(256);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"us-ascii\"?>");
        sb.AppendLine("<Boundary>");
        foreach (var (x, z) in spec.BoundaryPolygon)
            sb.AppendLine($"  <Point>{(int)Math.Round(x)}, {(int)Math.Round(z)}</Point>");
        sb.AppendLine("</Boundary>");
        return sb.ToString();
    }

    /// stream/&lt;key&gt;.xml — registers a single StreamTile so the engine
    /// streams the cSim_Global.psf when the player enters the area.
    public static string BuildStreamXml(OtsChallengeSpec spec)
    {
        var (cx, cy) = spec.StreamTileCenter ?? (150, -50);
        var (minX, minZ, maxX, maxZ) = spec.WorldAabbXZ();

        var sb = new System.Text.StringBuilder(256);
        sb.Append("<StreamTiles>\r\n");
        sb.Append("    <StreamTile>\r\n");
        sb.Append("        <Cx>").Append(cx).Append("</Cx>\r\n");
        sb.Append("        <Cy>").Append(cy).Append("</Cy>\r\n");
        sb.Append("        <MinX>").Append(F(minX)).Append("</MinX>\r\n");
        sb.Append("        <MinZ>").Append(F(minZ)).Append("</MinZ>\r\n");
        sb.Append("        <MaxX>").Append(F(maxX)).Append("</MaxX>\r\n");
        sb.Append("        <MaxZ>").Append(F(maxZ)).Append("</MaxZ>\r\n");
        sb.Append("    </StreamTile>\r\n");
        sb.Append("</StreamTiles>\r\n");
        return sb.ToString();

        static string F(float v) => v.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// content/missions/&lt;key&gt;/&lt;key&gt;_Sim.loc — the LocXml file
    /// listing the anchor + every sub-locator transform. Reuses the
    /// LocXml module so retail byte-parity is preserved.
    public static string BuildSimLocXml(OtsChallengeSpec spec)
    {
        var siblings = new List<(string Name, Builders.Transform44 Transform)>(spec.SubLocators.Count);
        foreach (var sub in spec.SubLocators)
            siblings.Add((sub.Name, sub.Transform));
        return LocXmlBuilder.BuildWithSiblings(spec.AnchorName, spec.AnchorTransform, siblings);
    }

    /// Convenience: build an OtsChallengeSpec from a public-API ChallengeInput
    /// using the auto-derived spawn-relative layout. Front-ends can construct
    /// specs by hand for pixel-perfect control, or call this for sane defaults.
    ///
    /// `scoringCenter` / `scoringHalfExtents` carry the authored scoring
    /// volume's actual world-space box. When null the layout falls back to a
    /// hardcoded 10m half-extent cube around the spawn — that fallback is
    /// what produces the "scoring works anywhere on the map" symptom, since
    /// without dimensions the auto-generated boundary is huge relative to a
    /// typical authored spot. Always pass the user's authored volume when
    /// one exists.
    ///
    /// `challengeBoundaryCenter` / `challengeBoundaryHalfExtents` carry the
    /// authored ChallengeBoundary volume's world-space box. The OTS engine
    /// uses this for OOB tracking + signup detection. With null we fall back
    /// to the hardcoded 50×20×50m slab centred on spawn — the symptom the
    /// user hit ("the challenge deactivates much further out than my authored
    /// boundary; it's like the 50×50 box is the boundary"). Pipe the user's
    /// authored volume through whenever it exists.
    public static OtsChallengeSpec FromChallengeInput(
        ChallengeInput input,
        DlcSpec map,
        float spawnX, float spawnY, float spawnZ, float spawnYawDegrees,
        (float X, float Y, float Z)? scoringCenter = null,
        (float X, float Y, float Z)? scoringHalfExtents = null,
        (float X, float Y, float Z)? startLocatorPosition = null,
        float? startLocatorYawDegrees = null,
        (float X, float Y, float Z)? visualSignupPosition = null,
        float? visualSignupYawDegrees = null,
        IReadOnlyList<(float X, float Y, float Z, System.Numerics.Vector3 RotationDegrees)>? authoredChevronTransforms = null,
        IReadOnlyList<(float X, float Y, float Z, float YawDeg)>? inChallengeRibbonTransforms = null,
        (float X, float Y, float Z)? discoveryBoundaryCenter = null,
        (float X, float Y, float Z)? discoveryBoundaryHalfExtents = null,
        (float X, float Y, float Z)? challengeBoundaryCenter = null,
        (float X, float Y, float Z)? challengeBoundaryHalfExtents = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(map);

        // ChallengeKey must be slug-safe — used verbatim as a VLT row key,
        // a trigger-volume canonical name (`<key>_challengeboundary`), and a
        // HALID stem (`ID_CHALLENGE_<UPPER>_TITLE`). MinimalDlcBuilder
        // /ModernMainForm.cs:588 used `ots_<map.Slug>` because OTS challenges
        // there were intrinsic to a single location. We slug the user-typed
        // name and prefix `ots_`. Per-map uniqueness is guaranteed upstream
        // by SceneToPackageInput (each challenge attaches to exactly one
        // freeskate-location MapInput, never broadcast).
        string slugged = ToSlug(input.Name);
        string challengeKey = string.IsNullOrEmpty(slugged)
            ? $"ots_{map.Slug}"
            : (slugged.StartsWith("ots_", StringComparison.Ordinal) ? slugged : "ots_" + slugged);

        var layout = OtsLayout.BuildSpawnRelative(
            challengeKey: challengeKey,
            worldStreamName: map.WorldStreamName,
            distKey: map.DistKey,
            spawnX: spawnX, spawnY: spawnY, spawnZ: spawnZ,
            spawnYawDegrees: spawnYawDegrees,
            scoringCenter: scoringCenter,
            scoringHalfExtents: scoringHalfExtents,
            startLocatorPosition: startLocatorPosition,
            startLocatorYawDegrees: startLocatorYawDegrees,
            visualSignupPosition: visualSignupPosition,
            visualSignupYawDegrees: visualSignupYawDegrees,
            authoredChevronTransforms: authoredChevronTransforms,
            inChallengeRibbonTransforms: inChallengeRibbonTransforms,
            discoveryBoundaryCenter: discoveryBoundaryCenter,
            discoveryBoundaryHalfExtents: discoveryBoundaryHalfExtents,
            challengeBoundaryCenter: challengeBoundaryCenter,
            challengeBoundaryHalfExtents: challengeBoundaryHalfExtents);

        return new OtsChallengeSpec
        {
            ChallengeKey = challengeKey,
            Map = map,
            DisplayTitle = string.IsNullOrWhiteSpace(input.Name) ? challengeKey : input.Name,
            Description = $"{map.DisplayName} — {input.Kind} challenge.",
            Triggers = layout.Triggers,
            SubLocators = layout.SubLocators,
            AnchorTransform = layout.AnchorTransform,
            OwnedPoints = input.OwnedPoints,
            KilledItPoints = input.KilledItPoints,
            // Retail OTS: RequiredChallengeHull element is PtrN-patched to challenge key string in bin.
            RequiredChallengeHullStringRef = challengeKey,
        };
    }

    private static string ToSlug(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return new string(s.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
    }
}
