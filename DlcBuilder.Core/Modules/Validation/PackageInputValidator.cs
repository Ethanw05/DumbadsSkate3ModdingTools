using DlcBuilder.Inputs;
using DlcBuilder.Modules.DlcManifest.Xml;
using DlcBuilder.Outputs;

namespace DlcBuilder.Modules.Validation;

/// Pre-build validation. Runs over a `PackageInput` and emits user-actionable
/// diagnostics describing exactly what's missing before the orchestrator
/// touches disk. Hard errors block the build; warnings document things that
/// will produce a working but suboptimal DLC (e.g. missing FE thumbnail).
///
/// Each diagnostic uses the source `Validate.&lt;Subject&gt;` so the front-end can
/// group them by what the user needs to fix (e.g. all `Validate.OtsTriggers`
/// entries point at challenges missing trigger volumes).
public static class PackageInputValidator
{
    public sealed record Result(IReadOnlyList<Diagnostic> Diagnostics)
    {
        public bool HasErrors => Diagnostics.Any(d => d.Level == DiagnosticLevel.Error);
        public bool HasWarnings => Diagnostics.Any(d => d.Level == DiagnosticLevel.Warning);
    }

    public static Result Validate(PackageInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var diags = new List<Diagnostic>();

        ValidatePackage(input, diags);
        for (int i = 0; i < input.Maps.Count; i++)
            ValidateMap(input.Maps[i], i, diags);

        ValidateUniqueChallengeKeys(input, diags);

        return new Result(diags);
    }

    // ── Package-level ─────────────────────────────────────────────────────
    private static void ValidatePackage(PackageInput input, List<Diagnostic> diags)
    {
        if (string.IsNullOrWhiteSpace(input.PackageName))
        {
            diags.Add(Err("Package",
                "Package name is required. Set a name in the editor's package settings (used for the manifest VLT, vaultlist filename, and the engine-side DLC slot)."));
            return;
        }

        // Slug derivation — make sure non-empty
        string slug = ToSlug(input.PackageName);
        if (string.IsNullOrEmpty(slug))
        {
            diags.Add(Err("Package",
                $"Package name '{input.PackageName}' contains no alphanumeric characters. " +
                "Add at least one letter or digit so a valid slug can be derived (used as the engine-facing dlc_<slug> identifier)."));
        }
        else if (slug.Length > 4)
        {
            // Engine clamp truncates, but warn so the user knows the ambiguity.
            diags.Add(Warn("Package",
                $"Package slug '{slug}' will be clamped to 4 chars ('{slug[..4]}') for the engine-facing framework key. " +
                "This is required by the progressionbanks state-graph dispatch — long keys crash on KilledIt completion. Make sure 4 chars are unique against other installed DLCs."));
        }

        if (input.Maps.Count == 0)
        {
            diags.Add(Err("Package",
                "At least one map is required. Add a DIST folder via the editor's New Map flow, " +
                "then mark at least one locator inside it as a Freeskate location " +
                "(each Freeskate locator becomes one DLC menu entry)."));
        }

        // Slug-suffix collision check across maps. SceneToPackageInput
        // disambiguates intra-DIST collisions, but two DISTs naming a location
        // the same way (e.g. both contributing a "Rails") still collide because
        // the slug includes the DIST suffix + location suffix and ends up
        // equal across the package. Each location row hashes the slug, so a
        // duplicate makes the engine pick the wrong row at random.
        var slugSeen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < input.Maps.Count; i++)
        {
            var m = input.Maps[i];
            string distLeaf = string.IsNullOrEmpty(m.DistFolderPath)
                ? $"map_{i}"
                : Path.GetFileName(m.DistFolderPath.TrimEnd('\\', '/'));
            string composite = string.IsNullOrWhiteSpace(m.SlugSuffix)
                ? distLeaf
                : $"{distLeaf}__{m.SlugSuffix}";
            if (slugSeen.TryGetValue(composite, out string? other))
            {
                diags.Add(Err("Package",
                    $"Maps[{i}]({m.DisplayName}): location slug '{composite}' collides with {other}. " +
                    "Rename one of the Freeskate locators so each becomes a unique menu entry."));
            }
            else
            {
                slugSeen[composite] = $"Maps[{i}]({m.DisplayName})";
            }
        }
    }

    // ── Per-map ───────────────────────────────────────────────────────────
    private static void ValidateMap(MapInput map, int index, List<Diagnostic> diags)
    {
        string ctx = $"Maps[{index}]({map.DisplayName})";

        // DIST folder must exist + contain at least one cPres tile.
        if (string.IsNullOrWhiteSpace(map.DistFolderPath))
        {
            diags.Add(Err("Map.Dist",
                $"{ctx}: DIST folder path is empty. Pick the map's DIST folder in the editor."));
            return;
        }
        if (!Directory.Exists(map.DistFolderPath))
        {
            diags.Add(Err("Map.Dist",
                $"{ctx}: DIST folder does not exist on disk: {map.DistFolderPath}. " +
                "The folder may have moved or been renamed since you imported it."));
            return;
        }

        // Two valid DIST shapes:
        //
        //   (a) FULL DIST — ships its own `cPres_<cx>_<cy>_high.psf` tile set.
        //       The freeskate boundary scan reads tile centres to derive
        //       playable extents. Stock DLCs (Danny Way etc.) all ship this.
        //
        //   (b) STUB DIST — only the 4 manifest stubs (`_Pres.pmm/psm/pss/pst`).
        //       Used when the DLC rides on top of an EXISTING world's freeskate
        //       area (e.g. a death-race authored against DIST_DownTown's
        //       tiles, where the DLC ships only the race-specific content,
        //       not a re-export of the base-game world). In this case the
        //       engine streams tiles from the base game and the area-XML
        //       writer produces a minimal boundary.
        //
        // Distinguish by checking for the manifest-stub set. Presence of the
        // 4 stubs without tiles → stub DIST, skip the tile requirement.
        // Absence of BOTH stubs and tiles → unusable.
        var tiles = FreeskateAreaXmlBuilder.ScanDistTileCenters(map.DistFolderPath);
        if (tiles.Count == 0)
        {
            string distFolderName = Path.GetFileName(
                map.DistFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            bool hasStubSet = HasManifestStubSet(map.DistFolderPath, distFolderName);
            if (!hasStubSet)
            {
                diags.Add(Err("Map.Dist",
                    $"{ctx}: DIST folder has neither cPres tiles nor the manifest-stub set " +
                    $"({distFolderName}_Pres.pmm/psm/pss/pst). The folder isn't a usable DIST — " +
                    "either re-export from source content, or pick a different folder."));
            }
            else
            {
                diags.Add(Warn("Map.Dist",
                    $"{ctx}: stub-only DIST (no cPres tiles). Engine will stream " +
                    "freeskate tiles from the base-game world; the DLC ships only the " +
                    "race/OTS-specific content on top. Make sure the parent world is " +
                    "already installed."));
            }
        }

        // No Freeskate locator → no menu entry. SceneToPackageInput emits a
        // fallback MapInput (with SlugSuffix=null) so the build doesn't drop
        // the DIST silently — but exporting that produces a DLC with no way
        // to launch the world from the menu, which is a hard fail.
        if (string.IsNullOrEmpty(map.SlugSuffix))
        {
            diags.Add(Err("Map.Locations",
                $"{ctx}: this DIST has no Freeskate-flagged locator, so there's no menu entry to launch the world. " +
                "Add at least one locator under the Freeskate section — its Name becomes the menu entry, its Category " +
                "groups it with siblings under one heading (e.g. 'Rails' under 'Street', 'Huge Jumps' under 'Mega'). " +
                "Multiple Freeskate locators in one DIST = multiple menu entries."));
            return;   // skip checks below — there's no spawn yet anyway.
        }

        // Per-map locators / triggers
        ValidateChallenges(map, ctx, diags);
    }

    // ── Per-challenge ─────────────────────────────────────────────────────
    private static void ValidateChallenges(MapInput map, string mapCtx, List<Diagnostic> diags)
    {
        for (int i = 0; i < map.Challenges.Count; i++)
        {
            var ch = map.Challenges[i];
            string chCtx = $"{mapCtx}.Challenges[{i}]";

            // Name is mandatory — used as ChallengeKey + HALID stems + row keys.
            if (string.IsNullOrWhiteSpace(ch.Name))
            {
                diags.Add(Err("Challenge.Name",
                    $"{chCtx}: Challenge has no name. Set one in the editor — the name becomes the engine-side challenge key " +
                    "(`ots_<name>`), the HAL string ID stem (`ID_CHALLENGE_<NAME>_TITLE`), and the per-instance VLT filename."));
                continue;
            }

            // Name must be slug-safe — used as a VLT row key + filename.
            string nameSlug = ToSlug(ch.Name);
            if (string.IsNullOrEmpty(nameSlug))
            {
                diags.Add(Err("Challenge.Name",
                    $"{chCtx}: Challenge name '{ch.Name}' contains no alphanumeric characters. " +
                    "Pick a name that produces a valid slug (letters / digits / underscores)."));
                continue;
            }

            switch (ch.Kind)
            {
                case ChallengeKind.Ots:
                    ValidateOtsChallenge(ch, map, chCtx, diags);
                    break;
                case ChallengeKind.Race:
                    ValidateRaceChallenge(ch, map, chCtx, diags);
                    break;
                case ChallengeKind.Skate:
                    ValidateSkateChallenge(ch, map, chCtx, diags);
                    break;
                default:
                    diags.Add(Warn("Challenge.Kind",
                        $"{chCtx}: Challenge kind '{ch.Kind}' is not yet implemented " +
                        "(currently OTS is fully implemented; Race input validation is in place but its VLT pipeline is still being authored). " +
                        "It will be skipped during build."));
                    continue;
            }
        }
    }

    private static void ValidateOtsChallenge(
        ChallengeInput ch, MapInput map, string chCtx, List<Diagnostic> diags)
    {
        // Scoring volume is the ANCHOR — its center is where the OTS spawns.
        // Without it the orchestrator falls back to (0,0,0) which is almost
        // always under the world, so we hard-require it.
        if (ch.ScoringVolumeId is not Guid svId)
        {
            diags.Add(Err("Challenge.ScoringVolume",
                $"{chCtx}: OTS challenge has no scoring volume assigned. " +
                "Add a scoring volume in the editor and link it to the challenge — its center becomes the OTS spawn point. " +
                "Without it the challenge spawns at world origin (0,0,0) and is unreachable."));
            return;
        }

        var scoring = map.TriggerVolumes.FirstOrDefault(v => v.Id == svId);
        if (scoring is null)
        {
            diags.Add(Err("Challenge.ScoringVolume",
                $"{chCtx}: Challenge references scoring volume {svId} but no trigger volume on this map has that ID. " +
                "The volume may have been deleted; re-link the challenge to an existing volume."));
            return;
        }

        if (scoring.HalfExtents.X <= 0 || scoring.HalfExtents.Y <= 0 || scoring.HalfExtents.Z <= 0)
        {
            diags.Add(Err("Challenge.ScoringVolume",
                $"{chCtx}: scoring volume '{scoring.Name}' has non-positive half-extents " +
                $"({scoring.HalfExtents.X:0.##}, {scoring.HalfExtents.Y:0.##}, {scoring.HalfExtents.Z:0.##}). " +
                "Resize it in the editor — a zero/negative volume means tricks can never score."));
        }

        if (ch.OwnedPoints <= 0)
        {
            diags.Add(Err("Challenge.Points",
                $"{chCtx}: Owned points = {ch.OwnedPoints}. Must be > 0 — Owned tier is unreachable otherwise."));
        }
        if (ch.KilledItPoints <= ch.OwnedPoints)
        {
            diags.Add(Warn("Challenge.Points",
                $"{chCtx}: Killed-it points ({ch.KilledItPoints}) ≤ owned points ({ch.OwnedPoints}). " +
                "Killed-it tier should require more points than Owned, or it can be hit by accident."));
        }

        // Warn if the user assigned a ChallengeBoundary volume that no longer
        // exists on the map. The orchestrator now consumes this volume's
        // centre + half-extents to drive the on-disk OTSTriggerBoundary slab
        // (DlcBuildOrchestrator.cs); without a valid match the layout falls
        // back to a 50×20×50m slab around the spawn.
        if (ch.ChallengeBoundaryId is Guid cbId
            && map.TriggerVolumes.All(v => v.Id != cbId))
        {
            diags.Add(Warn("Challenge.ChallengeBoundary",
                $"{chCtx}: Challenge references challenge-boundary volume {cbId} but no trigger volume on this map has that ID. " +
                "Falling back to the auto-derived 50m boundary around spawn — re-author the volume or relink the reference."));
        }
    }

    // ── Race ──────────────────────────────────────────────────────────────
    // Race challenges are hierarchical: race → heats → legs → gates. Each
    // gate references a trigger volume; the engine's gate hit-test reads
    // the volume's center + half-extents at runtime. Validation here makes
    // sure the authored input has at least one runnable heat/leg/gate
    // chain before the (yet-to-be-written) Race VLT pipeline runs.
    //
    // Source structure mirrored from stock dumps under
    // `AttribDumpOut/race_dwtn_01/Dump/Skate3_skater/Collections/`:
    //   • challenge_local_data/race_<key>.xml  → heats[] + visual indicators
    //   • challenge_race_heats/race_<key>_N.xml → legs[] + TimeLimit + KilledItTime
    //   • challenge_race_legs/race_<key>_N.xml  → gates[] + split-time triggers
    //   • challenge_race_gates/race_<key>_N.xml → GateVolume + Time_Bonus
    private static void ValidateRaceChallenge(
        ChallengeInput ch, MapInput map, string chCtx, List<Diagnostic> diags)
    {
        if (ch.RaceHeats.Count == 0)
        {
            diags.Add(Err("Challenge.RaceHeats",
                $"{chCtx}: Race challenge has no heats. Add at least one RaceHeatInput " +
                "with legs and gates — without heats the engine has nothing to schedule."));
            return;
        }

        var volumesById = map.TriggerVolumes.ToDictionary(v => v.Id);
        var locatorsById = map.Locators.ToDictionary(l => l.Id);

        if (ch.StartLocatorId is Guid chStart && !locatorsById.ContainsKey(chStart))
        {
            diags.Add(Warn("Challenge.StartLocator",
                $"{chCtx}: Challenge references start locator {chStart} but no locator on this map has that ID. " +
                "Per-heat StartLocatorId or the heat's first gate will be used as the fallback spawn."));
        }

        for (int hi = 0; hi < ch.RaceHeats.Count; hi++)
        {
            var heat = ch.RaceHeats[hi];
            string heatCtx = $"{chCtx}.RaceHeats[{hi}]";

            if (heat.TimeLimitSeconds <= 0)
            {
                diags.Add(Err("Challenge.RaceHeats.TimeLimit",
                    $"{heatCtx}: TimeLimitSeconds = {heat.TimeLimitSeconds}. Must be > 0 — " +
                    "the heat clock starts at this value and ticks down to 0 (heat fails at 0)."));
            }
            if (heat.KilledItSeconds <= 0f)
            {
                diags.Add(Err("Challenge.RaceHeats.KilledItSeconds",
                    $"{heatCtx}: KilledItSeconds = {heat.KilledItSeconds}. Must be > 0 — " +
                    "this is the time-to-beat for the Killed It tier."));
            }
            else if (heat.KilledItSeconds >= heat.TimeLimitSeconds)
            {
                diags.Add(Warn("Challenge.RaceHeats.KilledItSeconds",
                    $"{heatCtx}: KilledItSeconds ({heat.KilledItSeconds}) ≥ TimeLimitSeconds ({heat.TimeLimitSeconds}). " +
                    "Killed It is then unreachable in the heat window — pick a tighter target time."));
            }

            if (heat.StartLocatorId is Guid hStart && !locatorsById.ContainsKey(hStart))
            {
                diags.Add(Warn("Challenge.RaceHeats.StartLocator",
                    $"{heatCtx}: StartLocatorId {hStart} does not resolve to any locator on this map. " +
                    "Engine will fall back to the challenge-level StartLocatorId."));
            }

            if (heat.Legs.Count == 0)
            {
                diags.Add(Err("Challenge.RaceHeats.Legs",
                    $"{heatCtx}: heat has no legs. Add at least one RaceLegInput with gates."));
                continue;
            }

            for (int li = 0; li < heat.Legs.Count; li++)
            {
                var leg = heat.Legs[li];
                string legCtx = $"{heatCtx}.Legs[{li}]";

                if (leg.Gates.Count == 0)
                {
                    diags.Add(Err("Challenge.RaceHeats.Legs.Gates",
                        $"{legCtx}: leg has no gates. Add at least one RaceGateInput pointing at a trigger volume."));
                    continue;
                }

                for (int gi = 0; gi < leg.Gates.Count; gi++)
                {
                    var gate = leg.Gates[gi];
                    string gateCtx = $"{legCtx}.Gates[{gi}]";

                    if (!volumesById.TryGetValue(gate.TriggerVolumeId, out var tv))
                    {
                        diags.Add(Err("Challenge.RaceHeats.Legs.Gates.Volume",
                            $"{gateCtx}: Gate references trigger volume {gate.TriggerVolumeId} but no trigger volume on this map has that ID. " +
                            "Authoring the volume in the editor or re-linking the gate."));
                        continue;
                    }
                    if (tv.HalfExtents.X <= 0 || tv.HalfExtents.Y <= 0 || tv.HalfExtents.Z <= 0)
                    {
                        diags.Add(Err("Challenge.RaceHeats.Legs.Gates.Volume",
                            $"{gateCtx}: gate volume '{tv.Name}' has non-positive half-extents " +
                            $"({tv.HalfExtents.X:0.##}, {tv.HalfExtents.Y:0.##}, {tv.HalfExtents.Z:0.##}). " +
                            "A zero/negative volume means the player can never pass through the gate."));
                    }
                }

                foreach (var sttId in leg.SplitTimeTriggerVolumeIds)
                {
                    if (!volumesById.ContainsKey(sttId))
                    {
                        diags.Add(Warn("Challenge.RaceHeats.Legs.SplitTimeTriggers",
                            $"{legCtx}: SplitTimeTriggerVolumeIds contains {sttId} which doesn't resolve to a trigger volume on this map. " +
                            "Sectional timing for that segment will be skipped."));
                    }
                }
            }
        }
    }

    // ── Cross-cutting ─────────────────────────────────────────────────────
    private static void ValidateUniqueChallengeKeys(PackageInput input, List<Diagnostic> diags)
    {
        // Per MinimalDlcBuilder/ModernMainForm.cs:588, OTS challenge keys are
        // always shaped `ots_<map.Slug>` — the map slug carries each location's
        // SlugSuffix, so the same challenge name on DIFFERENT maps produces
        // DIFFERENT keys. Duplicates only collide WITHIN a single map (where
        // both challenges share the same map slug suffix).
        for (int mi = 0; mi < input.Maps.Count; mi++)
        {
            var map = input.Maps[mi];
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int ci = 0; ci < map.Challenges.Count; ci++)
            {
                var ch = map.Challenges[ci];
                if (string.IsNullOrWhiteSpace(ch.Name)) continue;
                string slug = ToSlug(ch.Name);
                if (string.IsNullOrEmpty(slug)) continue;

                if (seen.TryGetValue(slug, out string? existingPath))
                {
                    diags.Add(Err("Challenge.Name",
                        $"Maps[{mi}].Challenges[{ci}]({ch.Name}): challenge key '{slug}' " +
                        $"already used by {existingPath} on the same map. " +
                        "Rename one — duplicates within the same location collide on hashed row keys."));
                }
                else
                {
                    seen[slug] = $"Maps[{mi}].Challenges[{ci}]({ch.Name})";
                }
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────
    private static Diagnostic Err(string source, string msg) =>
        new(DiagnosticLevel.Error, "Validate." + source, msg);

    private static Diagnostic Warn(string source, string msg) =>
        new(DiagnosticLevel.Warning, "Validate." + source, msg);

    private static string ToSlug(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return new string(s.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == '_')
            .ToArray());
    }

    /// Validates a Game of S.K.A.T.E. challenge. Own pipeline — does NOT
    /// share OTS validation. Per-spot requires: 1-2 SpotVolumes, a
    /// TurnBasedStartVolume, a ChallengeBoundary, a StartLocator, a
    /// WaitLocator, and 1-2 visual indicator locators. All Guid refs must
    /// resolve to a volume / locator on the owning map.
    private static void ValidateSkateChallenge(
        ChallengeInput ch, MapInput map, string chCtx, List<Diagnostic> diags)
    {
        if (ch.SkateSpotVolumeIds.Count < 1 || ch.SkateSpotVolumeIds.Count > 2)
            diags.Add(Err("Skate.SpotVolumes",
                $"{chCtx}: Skate spot must have 1 or 2 SkateSpotVolumeIds (got {ch.SkateSpotVolumeIds.Count}). " +
                "Base game ships 1-2 per spot."));

        if (ch.SkateVisualIndicatorLocatorIds.Count < 1 || ch.SkateVisualIndicatorLocatorIds.Count > 2)
            diags.Add(Err("Skate.VisualIndicators",
                $"{chCtx}: Skate spot must have 1 or 2 VisualIndicator locators (got {ch.SkateVisualIndicatorLocatorIds.Count})."));

        if (ch.SkateTurnBasedStartVolumeId is null)
            diags.Add(Err("Skate.TurnBasedStartVolume",
                $"{chCtx}: Skate spot requires a SkateTurnBasedStartVolumeId."));

        if (ch.ChallengeBoundaryId is null)
            diags.Add(Err("Skate.ChallengeBoundary",
                $"{chCtx}: Skate spot requires a ChallengeBoundaryId."));

        if (ch.StartLocatorId is null)
            diags.Add(Err("Skate.StartLocator",
                $"{chCtx}: Skate spot requires a StartLocatorId."));

        if (ch.SkateWaitLocatorId is null)
            diags.Add(Warn("Skate.WaitLocator",
                $"{chCtx}: Skate spot has no SkateWaitLocatorId; falling back to StartLocator position."));

        // Resolve refs.
        var volumeIds = map.TriggerVolumes.Select(v => v.Id).ToHashSet();
        var locatorIds = map.Locators.Select(l => l.Id).ToHashSet();

        foreach (var vid in ch.SkateSpotVolumeIds)
            if (!volumeIds.Contains(vid))
                diags.Add(Err("Skate.SpotVolume.Ref",
                    $"{chCtx}: SkateSpotVolumeId {vid} does not resolve to a TriggerVolume on the map."));

        if (ch.SkateTurnBasedStartVolumeId is Guid svId && !volumeIds.Contains(svId))
            diags.Add(Err("Skate.TurnBasedStartVolume.Ref",
                $"{chCtx}: SkateTurnBasedStartVolumeId {svId} does not resolve."));

        if (ch.ChallengeBoundaryId is Guid cbId && !volumeIds.Contains(cbId))
            diags.Add(Err("Skate.ChallengeBoundary.Ref",
                $"{chCtx}: ChallengeBoundaryId {cbId} does not resolve."));

        if (ch.StartLocatorId is Guid slId && !locatorIds.Contains(slId))
            diags.Add(Err("Skate.StartLocator.Ref",
                $"{chCtx}: StartLocatorId {slId} does not resolve."));

        if (ch.SkateWaitLocatorId is Guid wlId && !locatorIds.Contains(wlId))
            diags.Add(Err("Skate.WaitLocator.Ref",
                $"{chCtx}: SkateWaitLocatorId {wlId} does not resolve."));

        foreach (var lid in ch.SkateVisualIndicatorLocatorIds)
            if (!locatorIds.Contains(lid))
                diags.Add(Err("Skate.VisualIndicator.Ref",
                    $"{chCtx}: SkateVisualIndicatorLocatorId {lid} does not resolve."));

        if (ch.SkateTimeLimitSeconds <= 0f)
            diags.Add(Warn("Skate.TimeLimit",
                $"{chCtx}: SkateTimeLimitSeconds is {ch.SkateTimeLimitSeconds}; base default 15.0f."));
    }

    /// True when the DIST folder contains the 4-file Pres manifest-stub set
    /// (`<DistFolderName>_Pres.pmm/psm/pss/pst`). Authoring workflows that
    /// build on top of an existing in-game world ship only these stubs (no
    /// cPres tile content) — the engine streams tiles from the base-game
    /// world stream and the DLC layers race/OTS-specific content on top.
    private static bool HasManifestStubSet(string distFolderPath, string distFolderName)
    {
        if (string.IsNullOrWhiteSpace(distFolderName)) return false;
        foreach (string ext in new[] { ".pmm", ".psm", ".pss", ".pst" })
        {
            string path = Path.Combine(distFolderPath, distFolderName + "_Pres" + ext);
            if (!File.Exists(path)) return false;
        }
        return true;
    }
}
