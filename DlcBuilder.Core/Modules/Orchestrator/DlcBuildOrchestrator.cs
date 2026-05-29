using System.Diagnostics;
using System.Linq;
using DlcBuilder.Inputs;
using DlcBuilder.Modules.DlcManifest;
using DlcBuilder.Modules.DlcManifest.Vlt;
using DlcBuilder.Modules.DlcManifest.Vlt.Templates;
using DlcBuilder.Modules.DlcManifest.Xml;
using DlcBuilder.Modules.FeImages;
using DlcBuilder.Modules.FeLang;
using DlcBuilder.Modules.Freeskate;
using DlcBuilder.Modules.LocXml;
using DlcBuilder.Modules.OtsPsg;
using DlcBuilder.Modules.Packing;
using DlcBuilder.Modules.Race;
using DlcBuilder.Modules.Unlocks;
using DlcBuilder.Modules.Validation;
using DlcBuilder.Outputs;

namespace DlcBuilder.Modules.Orchestrator;

/// Real `IDlcBuilder` that chains every ported module in the canonical build
/// order:
///
///   1. DlcManifest.PreparePool        — derive specs + lay out string pool
///   2. UiEntries.Build                — category / filter / listing / progression rows
///   3. Freeskate.Build                — location row + dlc_mapping + aud_bigfiles
///   4. OnlineFreeskate.Build          — online sibling location row
///   5. OtsPsg.Build (per challenge)   — per-OTS XML companions (PSG body deferred)
///   6. Write `.bin` + per-OTS XML/.loc files to disk
///   7. (Future) PSF Packer / BIG Packager once a real BIG-file staging tree
///       is laid out by the manifest VLT writer.
///
/// Each module returns structured artifacts + diagnostics. The orchestrator
/// flattens everything into one `BuildResult` so the front-end UI can show a
/// single "DLC Build Log" with mixed Info / Warning / Error entries.
///
/// Today this writes:
///   • `&lt;outDir&gt;/manifest.json`         — the staging manifest the stub
///                                            also produces, with full per-row spec dump.
///   • `&lt;outDir&gt;/dlc_&lt;slug&gt;.bin`  — the BinPool from PreparePool.
///   • `&lt;outDir&gt;/missions/&lt;chKey&gt;/boundary.xml`
///   • `&lt;outDir&gt;/missions/&lt;chKey&gt;/stream.xml`
///   • `&lt;outDir&gt;/missions/&lt;chKey&gt;/&lt;chKey&gt;_Sim.loc`
///
/// Once the deferred VLT collection-row writers + PSG body land, the orchestrator
/// also writes the `.vlt`, `.psg`, `.psf`, and final `.big.edat`. Public API is
/// stable — only the implementation grows.
public sealed class DlcBuildOrchestrator : IDlcBuilder
{
    public BuildResult Build(PackageInput input, string outputDirectory) =>
        Build(input, outputDirectory, new BuildOptions());

    public BuildResult Build(PackageInput input, string outputDirectory, BuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(options);

        var sw = Stopwatch.StartNew();
        var diagnostics = new List<Diagnostic>();
        var written = new List<string>();

        // Staging matches MinimalDlcBuilder's layout: build intermediate files
        // in `<exeFolder>/data/`, NOT under the user's output folder. The user's
        // output folder receives ONLY the final `<DlcFolder>/custom_<slug>.big.edat`.
        // We delete-and-recreate the staging dir each build so leftover files
        // from a previous package don't end up in the new BIG.
        string stagingRoot = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string stagingDataDir = Path.Combine(stagingRoot, "data");

        // -- Validate ---------------------------------------------------------
        // Comprehensive pre-build check: package metadata, per-map DIST + tile
        // scan, per-challenge name/scoring-volume/points/uniqueness. Any
        // hard error here blocks the build before we touch disk so the user
        // gets one clear list of things to fix instead of a half-written tree.
        var validation = PackageInputValidator.Validate(input);
        diagnostics.AddRange(validation.Diagnostics);
        if (validation.HasErrors)
            return Fail(outputDirectory, diagnostics, sw);

        Directory.CreateDirectory(outputDirectory);
        if (Directory.Exists(stagingDataDir))
            Directory.Delete(stagingDataDir, recursive: true);
        Directory.CreateDirectory(stagingDataDir);

        // Synthesized internal package name used for FILENAMES (manifest VLT,
        // vaultlist, lang pack). Original PackageSpec.Build (line 41) sets
        // `pkg.PackageName = "{prefix}_{slug}_minimal"` for this purpose;
        // the user-facing PackageName ("XLML") is `CategoryDisplayName`,
        // used only as the human-readable menu text. The engine looks up the
        // lang pack file by THIS synthesized token, not the display name —
        // a mismatch makes every HALID render as raw text.
        string packageSlugLower = DlcSpec.ToSlug(input.PackageName);
        string packageFileName = $"{DlcSpec.ToSlug(input.Prefix)}_{packageSlugLower}_minimal";

        // -- 1. Manifest VLT + BIN -------------------------------------------
        ManifestArtifacts manifest;
        try { manifest = DlcManifestVltBuilder.Build(input, out var manifestDiags); diagnostics.AddRange(manifestDiags); }
        catch (Exception ex)
        {
            diagnostics.Add(new(DiagnosticLevel.Error, "DlcManifest", $"Build threw: {ex.Message}"));
            return Fail(outputDirectory, diagnostics, sw);
        }

        // Write the .vlt + .bin pair under data/db/ to mirror the retail layout.
        string dbDir = Path.Combine(stagingDataDir, "db");
        Directory.CreateDirectory(dbDir);
        string binPath = Path.Combine(dbDir, $"{packageFileName}.bin");
        File.WriteAllBytes(binPath, manifest.BinFile);
        written.Add(binPath);
        if (manifest.VltFile is byte[] vlt)
        {
            string vltPath = Path.Combine(dbDir, $"{packageFileName}.vlt");
            File.WriteAllBytes(vltPath, vlt);
            written.Add(vltPath);
        }

        // (UI / map_category / map_filter / map_listing / map progression rows
        // are emitted in step 1 by DlcManifestVltBuilder. The freeskate
        // location rows are emitted in step 5a by ChallengeBankFreeskateVltBuilder
        // — both for offline and online menus, since online entries auto-derive
        // from each Freeskate locator's category.)

        // -- Race challenge specs: resolve up front --------------------------
        // Mirrors the OTS spec-collection pass below. Each Race ChallengeInput
        // is resolved against its owning MapInput (volumes / locators looked
        // up by Guid) into a fully-baked RaceChallengeSpec the race VLT
        // writers can consume directly.
        //
        // The VLT writers themselves are not yet wired in — the race specs
        // currently surface only as Info diagnostics (one per spec) so users
        // authoring races can confirm their gate / heat / leg structure is
        // being understood correctly. When the writers land, this list feeds
        // the race-VLT generation step that mirrors steps 4c–6 below for OTS.
        //
        // Byte-level reference for the writers to be authored:
        //   • AttribDumpOut/dlc_race_dwgh_01/   — DLC race instance VLT
        //   • AttribDumpOut/dlc_dwgh_challengebanks/ — DLC challengebanks rows
        //   • AttribDumpOut/dlc_dwgh_progressionbanks/ — DLC progression rows
        //   • AttribDumpOut/dlc_dwgh_framework/  — DLC framework family rows
        var raceSpecs = new List<(MapInput Map, DlcSpec Spec, RaceChallengeSpec Race)>();
        foreach (var (mapInputR, mapSpecR) in input.Maps.Zip(manifest.MapSpecs))
        {
            foreach (var ch in mapInputR.Challenges)
            {
                if (ch.Kind != ChallengeKind.Race) continue;
                var raceSpec = RaceSpecBuilder.FromChallengeInput(ch, mapInputR, mapSpecR, diagnostics);
                raceSpecs.Add((mapInputR, mapSpecR, raceSpec));
                diagnostics.Add(new(DiagnosticLevel.Info, "Race",
                    $"[{raceSpec.ChallengeKey}] Resolved race spec: " +
                    $"{raceSpec.Heats.Count} heat(s), {raceSpec.TotalGateCount} total gate(s), " +
                    $"gateSkipable={raceSpec.RaceGateSkipable}, isDeathRace={raceSpec.IsDeathRace}."));
            }
        }
        if (raceSpecs.Count > 0)
        {
            // The race pipeline is now wired end-to-end across every band
            // OTS uses:
            //   • per-race challenge_local_data VLT          (RaceLocalDataVltBuilder)
            //   • per-race mission folder + cSim_Global PSG  (RaceMissionFolderWriter,
            //                                                  packed by OtsPsfPacker)
            //   • DLC race family rows                       (RaceFamilyRowsBuilder)
            //   • per-race challengebanks instance rows      (RaceChallengeRowsBuilder)
            //   • shared race-stateenter handler + per-race  (RaceProgressionRowsBuilder)
            //     complete state + stategraph rows
            //   • FE language pack entries                   (FeEnglishLanguageBin
            //     ID_CHALLENGE_<KEY>_TITLE/_DESC               + RaceChallengeEntry)
            //
            // Remaining gaps are byte-fidelity refinements rather than
            // structural holes:
            //   • DLC-wide race-achievement chain (analogous to OTS's
            //     `dlc_<halid>_ots_complete_achievement` 3-row group). Rewards
            //     fire from the per-race progression state transition but
            //     aren't aggregated into a DLC achievement strip.
            //   • Engine-side VolumeID derivation (`RaceLocalDataVltBuilder.
            //     ResolveVolumeId` + `RaceVolumeNaming.GateVolumeId`) needs
            //     IDA verification — stock retail IDs follow a
            //     `2c7017xx00xxxxxx` pattern that may not match
            //     `Lookup8(name + "_" + distKey)`.
            //   • The placeholder `<key>_anchor` locator in the race PSG (a
            //     workaround for OtsPsgBytesBuilder's ≥1-locator requirement)
            //     may need to be removed once the PSG builder accepts empty
            //     locator lists.
            // Build a race DLC, run it, byte-diff against retail.
            diagnostics.Add(new(DiagnosticLevel.Info, "Build.Race",
                $"{raceSpecs.Count} Race challenge(s) staged end-to-end: " +
                "challenge_local_data VLT + mission folder (gate volumes + start " +
                "locator + 6-slot multiplayer spawn grid + per-gate _vi_<NN> chevron " +
                "locators) + DLC race family/instance rows + progression state/stategraph " +
                "+ FE language entries. Remaining byte-fidelity gaps: VolumeID " +
                "derivation (Lookup8 vs engine's world-stream+index allocator — fine " +
                "for new DLC gates), NIS playback default strings (placeholders vs " +
                "Sk3_* engine keys), no DLC-wide achievement aggregation."));
        }

        // -- 3b. Collect Skate (Game of S.K.A.T.E.) challenge specs ---------
        // Skate is its own pipeline (not OTS-shaped): SkateChallengeSpec,
        // SkateSpecBuilder, SkateLocalDataVltBuilder, SkateChallengeRowsBuilder.
        // Per-instance rows parent to base-game `s_k_a_t_e` family (always
        // loaded via base challengebanks/main.vlt), so no per-DLC family row
        // is needed.
        var skateSpecs = new List<(MapInput Map, DlcSpec Spec, Skate.SkateChallengeSpec Skate)>();
        foreach (var (mapInputS, mapSpecS) in input.Maps.Zip(manifest.MapSpecs))
        {
            foreach (var ch in mapInputS.Challenges)
            {
                if (ch.Kind != ChallengeKind.Skate) continue;
                var skateSpec = Skate.SkateSpecBuilder.FromChallengeInput(ch, mapInputS, mapSpecS, diagnostics);
                skateSpecs.Add((mapInputS, mapSpecS, skateSpec));
                diagnostics.Add(new(DiagnosticLevel.Info, "Skate",
                    $"[{skateSpec.ChallengeKey}] Resolved Skate spec: " +
                    $"{skateSpec.SpotVolumes.Count} spot vol(s), {skateSpec.VisualIndicators.Count} VI(s), " +
                    $"profile={(skateSpec.UseDwtn01Profile ? "dwtn_01" : "rest")}."));
            }
        }

        // -- 4a. Collect OTS challenge specs UP FRONT ------------------------
        // Built before the lang pack so the lang pack can use the same
        // canonical `spec.TitleHalId` / `spec.DescHalId` that OTS row D
        // references downstream — otherwise the row points at one HALID and
        // the lang pack registers a different one, lookup fails, OTS title
        // shows blank.
        var otsSpecs = new List<(MapInput Map, DlcSpec Spec, OtsChallengeSpec Ots)>();
        foreach (var (mapInput0, mapSpec0) in input.Maps.Zip(manifest.MapSpecs))
        {
            foreach (var ch in mapInput0.Challenges)
            {
                if (ch.Kind != ChallengeKind.Ots) continue;
                // Pull the user's authored scoring volume so its actual centre
                // AND half-extents drive the on-disk scoring boundary. Without
                // the half-extents the layout falls back to a 10m cube around
                // spawn → engine's geometric scoring check matches anywhere
                // inside that cube → "I can score anywhere on the map" symptom.
                (float sx, float sy, float sz, float yaw) = (0, 0, 0, 0);
                (float X, float Y, float Z)? scoringCenter = null;
                (float X, float Y, float Z)? scoringHalfExtents = null;
                if (ch.ScoringVolumeId is Guid svId)
                {
                    var sv = mapInput0.TriggerVolumes.FirstOrDefault(v => v.Id == svId);
                    if (sv != null)
                    {
                        sx = sv.Center.X; sy = sv.Center.Y; sz = sv.Center.Z;
                        yaw = sv.RotationDegrees.Y;
                        scoringCenter = (sv.Center.X, sv.Center.Y, sv.Center.Z);
                        scoringHalfExtents = (sv.HalfExtents.X, sv.HalfExtents.Y, sv.HalfExtents.Z);
                    }
                }
                // Pull the authored ChallengeStart locator so the per-OTS
                // `_startlocator` sub-locator (which `StartLocation` HAL
                // resolves through) carries the player's spawn position and
                // facing direction. Without this the engine teleports the
                // player to the scoring volume's centre facing yaw 0 — the
                // symptom: spawn offset + facing the wrong way.
                (float X, float Y, float Z)? startPos = null;
                float? startYaw = null;
                if (ch.StartLocatorId is Guid slId)
                {
                    var sl = mapInput0.Locators.FirstOrDefault(l => l.Id == slId);
                    // Safety: users can accidentally link StartLocatorId to a
                    // freeskate/world anchor. Only ChallengeStart locators are
                    // valid OTS spawn sources.
                    if (sl != null && sl.Kind == LocatorKind.ChallengeStart)
                    {
                        startPos = (sl.Position.X, sl.Position.Y, sl.Position.Z);
                        startYaw = sl.RotationDegrees.Y;
                    }
                }
                // Fallback when StartLocatorId is missing/mismatched:
                // pick the first authored ChallengeStart locator in this map.
                if (startPos is null)
                {
                    var fallbackStart = mapInput0.Locators.FirstOrDefault(l =>
                        l.Kind == LocatorKind.ChallengeStart);
                    if (fallbackStart != null)
                    {
                        startPos = (fallbackStart.Position.X, fallbackStart.Position.Y, fallbackStart.Position.Z);
                        startYaw = fallbackStart.RotationDegrees.Y;
                    }
                }

                bool startFromLinkedChallengeStart =
                    ch.StartLocatorId is Guid linkedStart
                    && mapInput0.Locators.FirstOrDefault(l => l.Id == linkedStart) is { } ls
                    && ls.Kind == LocatorKind.ChallengeStart;
                if (!startFromLinkedChallengeStart)
                {
                    diagnostics.Add(new(DiagnosticLevel.Warning, "OtsSpawn",
                        $"OTS «{ch.Name}»: start did not resolve from StartLocatorId to a Challenge-start locator; "
                        + "layout uses the first Challenge-start on the map (if any), else the scoring volume centre. "
                        + "At runtime, a bad MapStartLocation / missing mission PSG match still yields the engine’s not-found spawn."));
                }

                // Pull the user's authored ChallengeBoundary volume. The
                // OTS engine drives OOB tracking + signup detection from this
                // box, so without piping the actual centre + half-extents
                // through, the layout falls back to a 50×20×50m slab around
                // spawn — the engine then deactivates the challenge way
                // outside the authored area.
                (float X, float Y, float Z)? challengeBoundaryCenter = null;
                (float X, float Y, float Z)? challengeBoundaryHalfExtents = null;
                if (ch.ChallengeBoundaryId is Guid cbId)
                {
                    var cb = mapInput0.TriggerVolumes.FirstOrDefault(v => v.Id == cbId);
                    if (cb != null)
                    {
                        challengeBoundaryCenter = (cb.Center.X, cb.Center.Y, cb.Center.Z);
                        challengeBoundaryHalfExtents = (cb.HalfExtents.X, cb.HalfExtents.Y, cb.HalfExtents.Z);
                    }
                }
                // Optional visual locator placements.
                (float X, float Y, float Z)? visualSignupPosition = null;
                float? visualSignupYaw = null;
                if (ch.VisualSignupLocatorId is Guid vslId)
                {
                    var vsl = mapInput0.Locators.FirstOrDefault(l => l.Id == vslId);
                    if (vsl != null)
                    {
                        visualSignupPosition = (vsl.Position.X, vsl.Position.Y, vsl.Position.Z);
                        visualSignupYaw = vsl.RotationDegrees.Y;
                    }
                }

                List<(float X, float Y, float Z, System.Numerics.Vector3 RotationDegrees)>? authoredChevronTransforms = null;
                if (ch.ChevronLocatorIds.Count > 0)
                {
                    authoredChevronTransforms = new List<(float X, float Y, float Z, System.Numerics.Vector3 RotationDegrees)>();
                    foreach (Guid chevId in ch.ChevronLocatorIds)
                    {
                        if (mapInput0.Locators.FirstOrDefault(l => l.Id == chevId) is { } cloc)
                            authoredChevronTransforms.Add(
                                (cloc.Position.X, cloc.Position.Y, cloc.Position.Z, cloc.RotationDegrees));
                    }
                    if (authoredChevronTransforms.Count == 0)
                        authoredChevronTransforms = null;
                }

                List<(float X, float Y, float Z, float YawDeg)>? inChallengeRibbonTransforms = null;
                if (ch.InChallengeRibbonArrowLocatorIds.Count > 0)
                {
                    inChallengeRibbonTransforms = new List<(float X, float Y, float Z, float YawDeg)>();
                    foreach (Guid ribId in ch.InChallengeRibbonArrowLocatorIds)
                    {
                        if (mapInput0.Locators.FirstOrDefault(l => l.Id == ribId) is { } rloc)
                            inChallengeRibbonTransforms.Add(
                                (rloc.Position.X, rloc.Position.Y, rloc.Position.Z, rloc.RotationDegrees.Y));
                    }
                    if (inChallengeRibbonTransforms.Count == 0)
                        inChallengeRibbonTransforms = null;
                }
                // Optional discovery boundary. Falls back to scoring boundary if
                // not authored/invalid.
                (float X, float Y, float Z)? discoveryBoundaryCenter = null;
                (float X, float Y, float Z)? discoveryBoundaryHalfExtents = null;
                if (ch.DiscoveryBoundaryId is Guid dbId)
                {
                    var db = mapInput0.TriggerVolumes.FirstOrDefault(v => v.Id == dbId);
                    if (db != null)
                    {
                        discoveryBoundaryCenter = (db.Center.X, db.Center.Y, db.Center.Z);
                        discoveryBoundaryHalfExtents = (db.HalfExtents.X, db.HalfExtents.Y, db.HalfExtents.Z);
                    }
                }
                var spec = OtsPsgBuilder.FromChallengeInput(
                    ch, mapSpec0, sx, sy, sz, yaw,
                    scoringCenter: scoringCenter,
                    scoringHalfExtents: scoringHalfExtents,
                    startLocatorPosition: startPos,
                    startLocatorYawDegrees: startYaw,
                    visualSignupPosition: visualSignupPosition,
                    visualSignupYawDegrees: visualSignupYaw,
                    authoredChevronTransforms: authoredChevronTransforms,
                    inChallengeRibbonTransforms: inChallengeRibbonTransforms,
                    discoveryBoundaryCenter: discoveryBoundaryCenter,
                    discoveryBoundaryHalfExtents: discoveryBoundaryHalfExtents,
                    challengeBoundaryCenter: challengeBoundaryCenter,
                    challengeBoundaryHalfExtents: challengeBoundaryHalfExtents);
                otsSpecs.Add((mapInput0, mapSpec0, spec));
            }
        }

        // -- 5. FE language pack ----------------------------------------------
        // Use the canonical TitleHalId / DescHalId from each spec so the
        // HALIDs registered here match what the per-instance
        // challenge_global_data row points at. Race HALIDs follow the same
        // ID_CHALLENGE_<KEY>_TITLE / _DESC convention as OTS — the per-race
        // instance row's Title attribute (emitted by RaceChallengeRowsBuilder)
        // resolves through this table at runtime.
        var otsHalEntries = otsSpecs.Select(t => new FeEnglishLanguageBin.OtsChallengeEntry(
                TitleHalId: t.Ots.TitleHalId,
                DescHalId: t.Ots.DescHalId,
                DisplayTitle: t.Ots.DisplayTitle,
                Description: t.Ots.Description))
            .ToList();
        var raceHalEntries = raceSpecs.Select(t => new FeEnglishLanguageBin.RaceChallengeEntry(
                TitleHalId: t.Race.TitleHalId,
                DescHalId: t.Race.DescHalId,
                DisplayTitle: t.Race.DisplayTitle,
                // Per-race Description is empty by design — the
                // dlc_<framework>_races family row supplies
                // ID_MISSION_DEATHRACE_CHALLENGE_DESCRIPTION via inheritance.
                // Emit a placeholder so the FE lookup never returns NULL
                // (engine's lookup path derefs the result without a NULL
                // check on some FE menu transitions).
                Description: string.IsNullOrEmpty(t.Race.Description) ? " " : t.Race.Description))
            .ToList();

        try
        {
            byte[] langBin = FeEnglishLanguageBin.Build(input, manifest.MapSpecs, otsHalEntries, raceHalEntries);
            byte[] histBin = FeEnglishHistogramBin.Build();
            // Retail layout: `fe/languages/english/` (pluralized "languages") and
            // `LANGUAGE_English_<slug>_skate3ng-<package>.BIN` / `_HISTOGRAM_*.BIN`
            // (uppercase extension). Verified vs every shipping DLC.
            string feLangDir = Path.Combine(stagingDataDir, "fe", "languages", "english");
            Directory.CreateDirectory(feLangDir);
            string langPath = Path.Combine(feLangDir,
                $"LANGUAGE_English_{manifest.PackageSlug}_skate3ng-{packageFileName}.BIN");
            string histPath = Path.Combine(feLangDir,
                $"LANGUAGE_English_HISTOGRAM_skate3ng-{packageFileName}.BIN");
            File.WriteAllBytes(langPath, langBin);
            File.WriteAllBytes(histPath, histBin);
            written.Add(langPath);
            written.Add(histPath);
            diagnostics.Add(new(DiagnosticLevel.Info, "FeLang",
                $"Wrote LANGUAGE_English.bin ({langBin.Length}B) + Histogram ({histBin.Length}B)."));
        }
        catch (Exception ex)
        {
            diagnostics.Add(new(DiagnosticLevel.Error, "FeLang", $"Build failed: {ex.Message}"));
        }

        // -- 5a. Collect per-package OTS challenge specs ----------------------
        // Built up-front so challengebanks (section 5b) can append OTS family +
        // per-OTS rows, progressionbanks (5f) can register the achievement
        // chain, and section 8 can write per-OTS file artifacts. Each entry
        // pairs the OTS spec with its map's MapCategoryKey (needed for the
        // per-OTS challenge_global_data row's ClassRef extension).
        //
        // frameworkKey MUST be ≤ 8 chars total ("dlc_" + 4-char slug). The
        // engine's progressionbanks state-graph dispatch (sub_647758 →
        // sub_639874 → sub_6397D0 → sub_609530) does pointer arithmetic of
        // the form `name_ptr + 8` to skip the prefix and reach the suffix tag.
        // A longer key (e.g. `dlc_custom_maps`) lands `+8` mid-prefix at
        // ASCII bytes that get treated as a string ptr → unmapped read →
        // crash on KilledIt. The clamp keeps the engine-facing key short
        // while file-level naming + HALIDs keep using the full slug.
        string rawSlug = manifest.PackageSlug.ToLowerInvariant();
        string clampedSlug = rawSlug.Length <= 4 ? rawSlug : rawSlug[..4];
        string frameworkKey = "dlc_" + clampedSlug;

        // -- 5b. challengebanks/dlc_<framework>.vlt ---------------------------
        // Cross-references on freeskate row D and OTS rows reference the
        // PACKAGE-level `<slug>dlc` map_category, not each DIST's per-slug
        // key — DlcManifestVltBuilder only emits one map_category row per
        // package. Pass the package category key down so ClassRef16 and
        // RefSpec24 blobs hash the correct collection name. Mismatches are
        // silent at build time but resolve to NULL at runtime, dropping the
        // DLC from the online listing.
        string packageCategoryKey = manifest.PackageSlug + "dlc";
        try
        {
            var otsForChallengeBanks = otsSpecs
                .Select(t => (t.Ots, MapCategoryKey: packageCategoryKey))
                .ToList();
            // Race instance rows want a per-map MapCategory ref. Stock DW
            // races point this at the per-map ClassRefSpec_map_category
            // (matches the world the race lives on). The OTS pipeline uses
            // `packageCategoryKey` (one map category for the whole pack);
            // race follows the same since freeskate / OTS / race instance
            // rows all share the DLC's single map_category for now.
            var raceForChallengeBanks = raceSpecs
                .Select(t => (t.Race, MapCategoryKey: packageCategoryKey))
                .ToList();
            var skateForChallengeBanks = skateSpecs.Select(t => t.Skate).ToList();
            var cb = ChallengeBankFreeskateVltBuilder.Build(
                frameworkKey, manifest.MapSpecs,
                firstMapMapCategoryKey: packageCategoryKey,
                otsChallenges: otsForChallengeBanks,
                raceChallenges: raceForChallengeBanks,
                skateChallenges: skateForChallengeBanks);
            string cbDir = Path.Combine(dbDir, "challengebanks");
            Directory.CreateDirectory(cbDir);
            string cbVltPath = Path.Combine(cbDir, cb.FileName + ".vlt");
            string cbBinPath = Path.Combine(cbDir, cb.FileName + ".bin");
            File.WriteAllBytes(cbVltPath, cb.VltBytes);
            File.WriteAllBytes(cbBinPath, cb.BinBytes);
            written.Add(cbVltPath);
            written.Add(cbBinPath);
            diagnostics.Add(new(DiagnosticLevel.Info, "ChallengeBanks",
                $"Wrote freeskate challengebanks VLT ({cb.VltBytes.Length}B) + BIN ({cb.BinBytes.Length}B); {otsForChallengeBanks.Count} OTS challenge(s) appended."));
        }
        catch (Exception ex)
        {
            diagnostics.Add(new(DiagnosticLevel.Error, "ChallengeBanks", $"Build failed: {ex.Message}"));
        }

        // The 7 framework freeskate template VLTs (5c.1) + per-map stubs (5d)
        // each carry their own copy of the 45-attr challenge_local_data/default
        // override. The original tool never ships a separate `default.vlt` —
        // base game already provides it, and the per-template copies cover
        // the inheritance anchor we need. (We previously wrote one here; that
        // was an unnecessary duplicate that didn't exist in MinimalDlcBuilder.)
        string locDataDir = Path.Combine(dbDir, "challenge_local_data");
        Directory.CreateDirectory(locDataDir);

        // -- 5c. dlc_<framework>_local_data_framework.vlt ---------------------
        // Pack-wide framework VLT — declares parent rows the per-area stub VLTs
        // chain through (root + freeskate_locations + freeskate_activities +
        // 5 subtype rows). Lives at db/<framework>_local_data_framework.{vlt,bin}.
        try
        {
            var fw = LocalDataFrameworkVltBuilder.Build(
                frameworkKey,
                includeOtsRow: otsSpecs.Count > 0,
                includeRaceRow: raceSpecs.Count > 0);
            string fwVltPath = Path.Combine(dbDir, fw.FileName + ".vlt");
            string fwBinPath = Path.Combine(dbDir, fw.FileName + ".bin");
            File.WriteAllBytes(fwVltPath, fw.VltBytes);
            File.WriteAllBytes(fwBinPath, fw.BinBytes);
            written.Add(fwVltPath);
            written.Add(fwBinPath);
            diagnostics.Add(new(DiagnosticLevel.Info, "LocalDataFramework",
                $"Wrote framework VLT ({fw.VltBytes.Length}B) + BIN ({fw.BinBytes.Length}B)."));
        }
        catch (Exception ex)
        {
            diagnostics.Add(new(DiagnosticLevel.Error, "LocalDataFramework", $"Build failed: {ex.Message}"));
        }

        // -- 5c.1. Seven freeskate template VLTs ------------------------------
        // The engine's freeskate Activities walker
        // (Sk8::Challenge::ChallengeOnlineManager::GetOnlineFreeSkateActivities)
        // loads template VLTs by basename:
        //   <framework>_freeskate_locations
        //   <framework>_freeskate_activities (parent for all activity subtypes)
        //   <framework>_freeskate_activities_accumulation
        //   <framework>_freeskate_activities_gap_tag
        //   <framework>_freeskate_activities_simultrick
        //   <framework>_freeskate_activities_survival
        //   <framework>_freeskate_activities_tricklist
        // Each is a single 45-attr default/Hash_0 row — byte-identical content,
        // file basename is the only difference. Without these the activity
        // walker finds the locations slot empty and the engine refuses to
        // create the online-freeskate registration ("Cannot create Online
        // Freeskate in this Area"). Confirmed against DW's
        // db/challenge_local_data/dlc_dwgh_freeskate_*.vlt set (7 files).
        string[] freeskateTemplateBasenames =
        {
            frameworkKey + "_freeskate_locations",
            frameworkKey + "_freeskate_activities",
            frameworkKey + "_freeskate_activities_accumulation",
            frameworkKey + "_freeskate_activities_gap_tag",
            frameworkKey + "_freeskate_activities_simultrick",
            frameworkKey + "_freeskate_activities_survival",
            frameworkKey + "_freeskate_activities_tricklist",
        };
        foreach (string basename in freeskateTemplateBasenames)
        {
            try
            {
                var tmpl = FreeskateChallengeLocalDataTemplate.Build(basename);
                string fsTmplVltPath = Path.Combine(locDataDir, tmpl.FileName + ".vlt");
                string fsTmplBinPath = Path.Combine(locDataDir, tmpl.FileName + ".bin");
                File.WriteAllBytes(fsTmplVltPath, tmpl.VltBytes);
                File.WriteAllBytes(fsTmplBinPath, tmpl.BinBytes);
                written.Add(fsTmplVltPath);
                written.Add(fsTmplBinPath);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new(DiagnosticLevel.Error, "FreeskateTemplate",
                    $"[{basename}] Build failed: {ex.Message}"));
            }
        }
        diagnostics.Add(new(DiagnosticLevel.Info, "FreeskateTemplate",
            $"Wrote {freeskateTemplateBasenames.Length} freeskate template VLT(s)."));

        // -- 5d. Per-map freeskate_dlc_<area>.vlt stubs -----------------------
        // One single-row stub per map under db/challenge_local_data/. Each row
        // chains parent=<framework>_freeskate_locations and is what the per-area
        // freeskate location row in challengebanks resolves through.
        foreach (var mapSpec4 in manifest.MapSpecs)
        {
            try
            {
                var stub = FreeskateStubVltBuilder.Build(mapSpec4, frameworkKey);
                string stubVltPath = Path.Combine(locDataDir, stub.FileName + ".vlt");
                string stubBinPath = Path.Combine(locDataDir, stub.FileName + ".bin");
                File.WriteAllBytes(stubVltPath, stub.VltBytes);
                File.WriteAllBytes(stubBinPath, stub.BinBytes);
                written.Add(stubVltPath);
                written.Add(stubBinPath);
                diagnostics.Add(new(DiagnosticLevel.Info, "FreeskateStub",
                    $"[{mapSpec4.Slug}] Wrote freeskate stub VLT ({stub.VltBytes.Length}B) + BIN ({stub.BinBytes.Length}B)."));
            }
            catch (Exception ex)
            {
                diagnostics.Add(new(DiagnosticLevel.Error, "FreeskateStub",
                    $"[{mapSpec4.Slug}] Build failed: {ex.Message}"));
            }
        }

        // -- 5d.1. Per-OTS challenge_local_data VLTs --------------------------
        // db/challenge_local_data/<challengeKey>.vlt — one 2-row VLT per OTS
        // (45-attr default override + 6-attr per-instance row). The per-instance
        // row's parent=<framework>_own_the_spots inherits 45 family attrs that
        // the engine relies on during construction.
        foreach (var (_, _, ots) in otsSpecs)
        {
            try
            {
                var ld = OtsLocalDataVltBuilder.Build(ots, frameworkKey);
                string ldVltPath = Path.Combine(locDataDir, ld.FileName + ".vlt");
                string ldBinPath = Path.Combine(locDataDir, ld.FileName + ".bin");
                File.WriteAllBytes(ldVltPath, ld.VltBytes);
                File.WriteAllBytes(ldBinPath, ld.BinBytes);
                written.Add(ldVltPath);
                written.Add(ldBinPath);
                diagnostics.Add(new(DiagnosticLevel.Info, "OtsLocalData",
                    $"[{ots.ChallengeKey}] Wrote per-instance OTS VLT ({ld.VltBytes.Length}B) + BIN ({ld.BinBytes.Length}B)."));
            }
            catch (Exception ex)
            {
                diagnostics.Add(new(DiagnosticLevel.Error, "OtsLocalData",
                    $"[{ots.ChallengeKey}] Build failed: {ex.Message}"));
            }
        }

        // -- 5d.1b. Per-Race challenge_local_data VLTs ──────────────────────
        // db/challenge_local_data/<challengeKey>.vlt — one 19-row VLT per
        // race (or 17 for races without legs/heats, etc.). Mirrors the OTS
        // step above but emits the four-class hierarchy
        // (challenge_local_data + challenge_race_gates / _heats / _legs).
        //
        // NOTE: race challenges also need:
        //   • dlc_<framework>_races family rows in challengebanks/dlc_<framework>.vlt
        //     (the `challenges/dlc_<framework>_races` + `challenge_global_data/
        //     dlc_<framework>_races` rows that carry the death-race UI strings
        //     and inherit from the engine's `races` family)
        //   • <challengeKey>_stategraph row in progressionbanks/dlc_<framework>.vlt
        //   • FE language pack entries for ID_CHALLENGE_<KEY>_TITLE/_DESC
        // None of those are wired yet — see the Warning diagnostic emitted
        // after this loop.
        // -- 5d.1b. Per-Skate-spot local_data VLT -------------------------------
        // Per-spot challenge_local_data/skate_<key>.vlt — own pipeline,
        // parents to base-game `s_k_a_t_e` framework row.
        foreach (var (_, _, skate) in skateSpecs)
        {
            try
            {
                var sd = Skate.SkateLocalDataVltBuilder.Build(skate);
                string sdVltPath = Path.Combine(locDataDir, sd.FileName + ".vlt");
                string sdBinPath = Path.Combine(locDataDir, sd.FileName + ".bin");
                File.WriteAllBytes(sdVltPath, sd.VltBytes);
                File.WriteAllBytes(sdBinPath, sd.BinBytes);
                written.Add(sdVltPath);
                written.Add(sdBinPath);
                diagnostics.Add(new(DiagnosticLevel.Info, "SkateLocalData",
                    $"[{skate.ChallengeKey}] Wrote per-instance Skate VLT ({sd.VltBytes.Length}B) + BIN ({sd.BinBytes.Length}B)."));

                // Per-spot mission folder (Pres+Tex stubs + cSim_Global PSG).
                Skate.SkateMissionFolderWriter.Write(skate, stagingDataDir, written);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new(DiagnosticLevel.Error, "SkateLocalData",
                    $"[{skate.ChallengeKey}] Build failed: {ex.Message}"));
            }
        }

        foreach (var (_, _, race) in raceSpecs)
        {
            try
            {
                var rd = RaceLocalDataVltBuilder.Build(race, frameworkKey);
                string rdVltPath = Path.Combine(locDataDir, rd.FileName + ".vlt");
                string rdBinPath = Path.Combine(locDataDir, rd.FileName + ".bin");
                File.WriteAllBytes(rdVltPath, rd.VltBytes);
                File.WriteAllBytes(rdBinPath, rd.BinBytes);
                written.Add(rdVltPath);
                written.Add(rdBinPath);
                diagnostics.Add(new(DiagnosticLevel.Info, "RaceLocalData",
                    $"[{race.ChallengeKey}] Wrote per-instance Race VLT ({rd.VltBytes.Length}B) + BIN ({rd.BinBytes.Length}B)."));
            }
            catch (Exception ex)
            {
                diagnostics.Add(new(DiagnosticLevel.Error, "RaceLocalData",
                    $"[{race.ChallengeKey}] Build failed: {ex.Message}"));
            }
        }

        // -- 5d.1c. Per-Race mission folder (Pres + Tex stubs + cSim_Global PSG) ─
        // Mirrors the OTS step 5e below but trimmed to the file set races
        // actually need: 4 Pres stubs + 4 Tex stubs (no Sim — the
        // cSim_Global.psf supersedes them) + the gate-volume PSG inside
        // cSim_Global/. The existing OtsPsfPacker.PackAll sweep packs the
        // folder generically — it iterates every mission dir under
        // `data/content/missions/` and packs any with a cSim_Global subfolder,
        // so race folders are picked up without further wiring.
        foreach (var (_, _, race) in raceSpecs)
        {
            try
            {
                RaceMissionFolderWriter.Write(race, stagingDataDir, written);
                diagnostics.Add(new(DiagnosticLevel.Info, "RaceMission",
                    $"[{race.ChallengeKey}] Wrote Pres/Tex stubs + cSim_Global PSG ({race.TotalGateCount} gates)."));
            }
            catch (Exception ex)
            {
                diagnostics.Add(new(DiagnosticLevel.Error, "RaceMission",
                    $"[{race.ChallengeKey}] Mission folder write failed: {ex.Message}"));
            }
        }

        // -- 5d.1d. Per-Race boundary + stream XML ──────────────────────────
        // `boundary/<key>.xml` + `stream/<key>.xml` — stock retail ships
        // these for EVERY race (cf. `StockGameData/.../boundary/race_dwtn_01.xml`).
        // Engine's challenge-launch path opens both files at race start; if
        // either is missing the FE surfaces "you don't have the current DLC
        // installed to play it" even though the race is listed in the menu.
        // Both files derive from the race's gate AABB; reuses freeskate
        // stream-tile XML helpers so the format matches base-game tiles.
        foreach (var (raceMapInput, _, race) in raceSpecs)
        {
            try
            {
                RaceXmlWriter.Write(race, raceMapInput, stagingDataDir, written);
                diagnostics.Add(new(DiagnosticLevel.Info, "RaceXml",
                    $"[{race.ChallengeKey}] Wrote boundary + stream XML."));
            }
            catch (Exception ex)
            {
                diagnostics.Add(new(DiagnosticLevel.Error, "RaceXml",
                    $"[{race.ChallengeKey}] Boundary/stream XML write failed: {ex.Message}"));
            }
        }

        // -- 5d.2. db/challenge_local_data/dlc_<framework>_own_the_spots.vlt ─
        // Standalone challenge_local_data VLT for the OTS family. Single-row
        // inheritance anchor — same shape as the freeskate template files but
        // with the OTS family basename. Engine resolves OTS family lookups
        // against this file when per-instance rows climb the parent chain.
        if (otsSpecs.Count > 0)
        {
            try
            {
                string familyShimName = $"{frameworkKey}_own_the_spots";
                var shim = FreeskateChallengeLocalDataTemplate.Build(familyShimName);
                string shimVltPath = Path.Combine(locDataDir, shim.FileName + ".vlt");
                string shimBinPath = Path.Combine(locDataDir, shim.FileName + ".bin");
                File.WriteAllBytes(shimVltPath, shim.VltBytes);
                File.WriteAllBytes(shimBinPath, shim.BinBytes);
                written.Add(shimVltPath);
                written.Add(shimBinPath);
                diagnostics.Add(new(DiagnosticLevel.Info, "OtsFamilyShim",
                    $"Wrote OTS family shim VLT ({shim.VltBytes.Length}B) + BIN ({shim.BinBytes.Length}B)."));
            }
            catch (Exception ex)
            {
                diagnostics.Add(new(DiagnosticLevel.Error, "OtsFamilyShim", $"Build failed: {ex.Message}"));
            }
        }

        // -- 5e. progressionbanks/dlc_<framework>.vlt -------------------------
        // 16+ progression rows: 6 class defaults + 6 DLC anchors + 4 OTS reward
        // chain rows + per-OTS state machine + DLC-wide bridge + 3-row
        // achievement chain. Required for the OTS KilledIt completion path —
        // without these the engine has no progression class hashes registered
        // and crashes when looking up the OTS unlock reward by hash.
        try
        {
            var otsForProgression = otsSpecs.Select(t => t.Ots).ToList();
            var raceForProgression = raceSpecs.Select(t => t.Race).ToList();
            var pb = ProgressionBanksVltBuilder.Build(
                frameworkKey, otsForProgression, raceForProgression);
            string pbDir = Path.Combine(dbDir, "progressionbanks");
            Directory.CreateDirectory(pbDir);
            string pbVltPath = Path.Combine(pbDir, pb.FileName + ".vlt");
            string pbBinPath = Path.Combine(pbDir, pb.FileName + ".bin");
            File.WriteAllBytes(pbVltPath, pb.VltBytes);
            File.WriteAllBytes(pbBinPath, pb.BinBytes);
            written.Add(pbVltPath);
            written.Add(pbBinPath);
            diagnostics.Add(new(DiagnosticLevel.Info, "ProgressionBanks",
                $"Wrote progressionbanks VLT ({pb.VltBytes.Length}B) + BIN ({pb.BinBytes.Length}B); " +
                $"{otsForProgression.Count} OTS + {raceForProgression.Count} Race challenge(s) wired."));
        }
        catch (Exception ex)
        {
            diagnostics.Add(new(DiagnosticLevel.Error, "ProgressionBanks", $"Build failed: {ex.Message}"));
        }

        // -- 6. vaultlist.xml --------------------------------------------------
        try
        {
            // Retail filename: `<PackageFileName>.vaultlist.xml` where
            // PackageFileName is the synthesized `dlc_<slug>_minimal` token,
            // not the user's display name. Verified across DW/Maloof/Creator/AG.
            string vlistPath = Path.Combine(dbDir, packageFileName + ".vaultlist.xml");
            // VaultFile entries MUST reference files that ACTUALLY EXIST on
            // disk. The framework / challengebanks / progressionbanks /
            // own_the_spots VLTs are all named with the CLAMPED 4-char slug
            // (`frameworkKey`) — passing the unclamped `dlc_<full>` here
            // points the engine at non-existent files, so it silently skips
            // those loads at boot. Result: no per-DLC challenge rows in the
            // vault → world drops out of online listing AND OTS rows have no
            // parent chain to resolve through. Always pass `frameworkKey`.
            string vlistContent = VaultListXmlBuilder.Build(
                packageFileName,
                frameworkKey,
                includeOtsAnchor: otsSpecs.Count > 0);
            File.WriteAllText(vlistPath, vlistContent);
            written.Add(vlistPath);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new(DiagnosticLevel.Error, "VaultList", $"Build failed: {ex.Message}"));
        }

        // -- 7. Per-map freeskate area + world stream/boundary XMLs -----------
        // Copy each source DIST folder into the staging world-stream tree:
        //   data/content/world/stream/<WorldStreamName>/
        // Without this, BIG packing can succeed but ship an effectively empty
        // world payload (only generated XML/mission artifacts, no DIST assets).
        // Multiple MapInputs can share one DIST; copy each world stream once.
        var copiedWorldStreams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (mapInput2, mapSpec2) in input.Maps.Zip(manifest.MapSpecs))
        {
            if (!copiedWorldStreams.Add(mapSpec2.WorldStreamName))
                continue;
            try
            {
                string srcDist = mapInput2.DistFolderPath.Trim().Trim('"');
                string dstDist = Path.Combine(stagingDataDir, "content", "world", "stream", mapSpec2.WorldStreamName);
                int copiedFiles = CopyDirectoryRecursive(srcDist, dstDist);
                written.Add(dstDist);
                diagnostics.Add(new(DiagnosticLevel.Info, "DistCopy",
                    $"[{mapSpec2.Slug}] Copied DIST '{mapSpec2.WorldStreamName}' to staging ({copiedFiles} file(s))."));
            }
            catch (Exception ex)
            {
                diagnostics.Add(new(DiagnosticLevel.Warning, "DistCopy",
                    $"[{mapSpec2.Slug}] DIST copy failed: {ex.Message}"));
            }
        }

        // -- 7a. Per-map freeskate area + world stream/boundary XMLs ----------
        foreach (var (mapInput3, mapSpec3) in input.Maps.Zip(manifest.MapSpecs))
        {
            try
            {
                var centers = FreeskateAreaXmlBuilder.ScanDistTileCenters(mapInput3.DistFolderPath);
                var (cx, cy) = FreeskateAreaXmlBuilder.ResolveFreeskateStreamTileCenter(
                    centers, mapSpec3.SpawnX, mapSpec3.SpawnZ);

                string boundaryDir = Path.Combine(stagingDataDir, "boundary");
                string streamDir = Path.Combine(stagingDataDir, "stream");
                Directory.CreateDirectory(boundaryDir);
                Directory.CreateDirectory(streamDir);

                string fsKey = $"freeskate_dlc_{mapSpec3.Slug}";
                string fsAreaStreamPath = Path.Combine(streamDir, fsKey + ".xml");
                string fsBoundaryPath = Path.Combine(boundaryDir, fsKey + ".xml");
                File.WriteAllText(fsAreaStreamPath,
                    FreeskateAreaXmlBuilder.BuildFreeskateAreaStreamXml(centers, cx, cy));
                File.WriteAllText(fsBoundaryPath,
                    FreeskateAreaXmlBuilder.BuildFreeskateBoundaryXml(centers));
                written.Add(fsAreaStreamPath);
                written.Add(fsBoundaryPath);

                string presPath = Path.Combine(streamDir, $"{mapSpec3.WorldStreamName}_Pres.xml");
                string simPath = Path.Combine(streamDir, $"{mapSpec3.WorldStreamName}_Sim.xml");
                File.WriteAllText(presPath, FreeskateAreaXmlBuilder.BuildDistWorldPresStreamTilesXml(centers));
                File.WriteAllText(simPath, FreeskateAreaXmlBuilder.BuildDistWorldSimStreamTilesXml(centers));
                written.Add(presPath);
                written.Add(simPath);

                diagnostics.Add(new(DiagnosticLevel.Info, "FreeskateArea",
                    $"[{mapSpec3.Slug}] Scanned {centers.Count} tile center(s); spawn=({cx},{cy})."));
            }
            catch (Exception ex)
            {
                diagnostics.Add(new(DiagnosticLevel.Warning, "FreeskateArea",
                    $"[{mapSpec3.Slug}] XML build failed: {ex.Message}"));
            }

            // -- 7b. Per-map world locator files + world Sim.loc --------------
            // freeskate_<slug>_locator + Z_<slug>_Start + per-OTS locators +
            // the gating <world>_Sim.loc that LocationManager registers on
            // world join. Without these the online-freeskate registration
            // fails ("Cannot create Online Freeskate in this Area") and OTS
            // location HALs resolve to nothing.
            try
            {
                var mapOts = otsSpecs.Where(o => o.Spec.DistKey == mapSpec3.DistKey)
                                     .Select(o => o.Ots).ToList();
                // Skip online variants — they share the offline variant's
                // startlocator (stock retail confirms: race_dwtn_01_ol.bin
                // references the same `race_dwtn_01_startlocator_01` name as
                // One global_locator PSG per race.
                var mapRaces = raceSpecs.Where(r => r.Spec.DistKey == mapSpec3.DistKey)
                                        .Select(r => r.Race).ToList();
                WorldLocatorFilesWriter.Write(mapSpec3, stagingDataDir, mapOts, mapRaces, written);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new(DiagnosticLevel.Warning, "WorldLocators",
                    $"[{mapSpec3.Slug}] Locator file build failed: {ex.Message}"));
            }

            // -- 7c. Per-map mission stubs (freeskate_<slug>) ----------------
            // 12-file placeholder asset stubs under content/missions/freeskate_<slug>/.
            // Without these the engine's mission asset loader fails when the
            // freeskate location starts.
            try
            {
                MissionStubsWriter.Write(mapSpec3, stagingDataDir, written);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new(DiagnosticLevel.Warning, "MissionStubs",
                    $"[{mapSpec3.Slug}] Mission stub copy failed: {ex.Message}"));
            }

            // -- 7d. FE menu location image (rps3) ---------------------------
            // 512×256 DXT5 single-mip Pegasus arena referenced by the
            // fe_locations.Image attribute. Skipped silently when no source
            // image is configured on the DlcSpec.
            if (!string.IsNullOrWhiteSpace(mapSpec3.FeImageSourcePath))
            {
                try
                {
                    string src = mapSpec3.FeImageSourcePath.Trim().Trim('"');
                    if (!File.Exists(src))
                    {
                        diagnostics.Add(new(DiagnosticLevel.Warning, "FeImage",
                            $"[{mapSpec3.Slug}] FE menu image not found: {src}"));
                    }
                    else
                    {
                        string feLocationsDir = Path.Combine(stagingDataDir, "fe", "source", "images", "locations");
                        Directory.CreateDirectory(feLocationsDir);
                        string feBase = FeLocationNaming.FeLocationAssetBaseName(mapSpec3.Slug);
                        string feDest = Path.Combine(feLocationsDir, feBase + ".rps3");
                        FeLocationImageWriter.WriteFromImageFile(src, feDest, feBase);
                        written.Add(feDest);
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add(new(DiagnosticLevel.Warning, "FeImage",
                        $"[{mapSpec3.Slug}] FE image build failed: {ex.Message}"));
                }
            }
        }

        // -- 7e. World _Sim.loc aggregation ----------------------------------
        // Multiple freeskate locations within a single DIST share the same
        // WorldStreamName, which means they share the same on-disk
        // `<World>_Sim.loc` file. Per-MapInput writes would clobber each
        // other (the symptom: the second freeskate location's MapInput
        // overwrites map[0]'s Sim.loc, dropping every OTS sub-locator that
        // was registered through map[0]). Aggregate now: group MapInputs by
        // WorldStreamName, collect the freeskate locators + Z_Start + every
        // OTS challenge anchored in the DIST, write ONE Sim.loc per DIST.
        try
        {
            foreach (var grp in manifest.MapSpecs.GroupBy(s => s.WorldStreamName, StringComparer.Ordinal))
            {
                var freeskateEntries = grp.Select(s => new WorldLocatorFilesWriter.FreeskateEntry(
                    LocatorName: $"freeskate_{s.Slug.ToLowerInvariant()}_locator",
                    ZStartName:  $"Z_{s.Slug}_Start",
                    Transform:   Builders.Transform44.YawAt(s.SpawnX, s.SpawnY, s.SpawnZ, s.SpawnYaw))).ToList();

                // Every OTS / Race authored in any MapInput sharing this DIST
                // contributes its anchor + start locator to the world's
                // LocationManager registration set. Without race entries the
                // engine can't resolve `<race_key>_startlocator` at world-join
                // time → minimap icon at 0,0, signup trigger missing, vault
                // construction crashes when launching the race.
                var distOts = otsSpecs
                    .Where(o => grp.Any(s => s.DistKey == o.Spec.DistKey))
                    .Select(o => o.Ots)
                    .ToList();
                // Skip online variants — same startlocator name as offline.
                // Registering twice would silently fail (RegArena rejects
                // duplicates) and bloats the Sim.loc.
                var distRaces = raceSpecs
                    .Where(r => grp.Any(s => s.DistKey == r.Spec.DistKey))
                    .Select(r => r.Race)
                    .ToList();

                WorldLocatorFilesWriter.WriteWorldSimLoc(
                    grp.Key, freeskateEntries, distOts, distRaces, stagingDataDir, written);
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add(new(DiagnosticLevel.Error, "WorldSimLoc",
                $"World Sim.loc aggregation failed: {ex.Message}"));
        }

        // -- 8. OTS challenges — write per-OTS file artifacts ----------------
        // Boundary + stream XMLs go in the SAME `data/boundary/` and
        // `data/stream/` directories as the freeskate-area XMLs (NOT in the
        // mission folder). Filename = `<challengeKey>.xml` matching retail
        // (MinimalDlcBuilder/DlcBuilder.cs lines 3017 + 3029). Engine looks
        // them up at those package-root paths; without them OTS challenges
        // never resolve and never appear in-world.
        //
        // Stream XML uses the same `BuildFreeskateAreaStreamXml` form as
        // freeskate areas (one tile per scanned center), centered on the
        // OTS polygon's AABB centroid — NOT a single-tile placeholder.
        string otsBoundaryDir = Path.Combine(stagingDataDir, "boundary");
        string otsStreamDir = Path.Combine(stagingDataDir, "stream");
        Directory.CreateDirectory(otsBoundaryDir);
        Directory.CreateDirectory(otsStreamDir);

        int otsCount = 0;
        foreach (var (mapInputForOts, mapSpecForOts, spec) in otsSpecs)
        {
            try
            {
                // Per-OTS boundary XML — polygon footprint of the challenge.
                string otsBoundaryPath = Path.Combine(otsBoundaryDir, spec.ChallengeKey + ".xml");
                File.WriteAllText(otsBoundaryPath, OtsPsgBuilder.BuildBoundaryXml(spec));
                written.Add(otsBoundaryPath);

                // Per-OTS stream XML — built like a freeskate area: scan the
                // parent map's DIST tiles, pick the one nearest the polygon's
                // AABB centroid as the center, emit StreamTile rows. This is
                // what retail expects (see line 3022-3031 of original).
                var aabb = spec.WorldAabbXZ();
                float aabbCx = (aabb.MinX + aabb.MaxX) * 0.5f;
                float aabbCz = (aabb.MinZ + aabb.MaxZ) * 0.5f;
                var distCenters = FreeskateAreaXmlBuilder.ScanDistTileCenters(mapInputForOts.DistFolderPath);
                var (otsTileCx, otsTileCy) = spec.StreamTileCenter
                    ?? FreeskateAreaXmlBuilder.PositionToTileCenter(aabbCx, aabbCz);
                string otsStreamPath = Path.Combine(otsStreamDir, spec.ChallengeKey + ".xml");
                File.WriteAllText(otsStreamPath,
                    FreeskateAreaXmlBuilder.BuildFreeskateAreaStreamXml(distCenters, otsTileCx, otsTileCy));
                written.Add(otsStreamPath);

                // Mission folder writer handles the Sim.loc, loose .loc, PSG
                // body, and 12 manifest stubs.
                OtsMissionFolderWriter.Write(spec, stagingDataDir, written);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new(DiagnosticLevel.Error, "OtsMission",
                    $"[{spec.ChallengeKey}] Mission folder build failed: {ex.Message}"));
            }
            otsCount++;
        }
        diagnostics.Add(new(DiagnosticLevel.Info, "Orchestrator",
            $"Processed {otsCount} OTS challenge(s) across {input.Maps.Count} map(s)."));

        // -- 8a. Unlock files (entitlement registry) --------------------------
        // `data/unlocks/<slug>.unlock` and `<slug>0000_product00000000.unlock`.
        // The engine's `DlcSlot_IsInstalledOrEntitled_GATE` gates online
        // visibility on the DLC_PRODUCT registration in the second file —
        // without it the DLC may load offline but its entries are filtered
        // out of the online menu.
        try
        {
            var otsForUnlocks = otsSpecs.Select(t => t.Ots).ToList();
            var raceForUnlocks = raceSpecs.Select(t => t.Race).ToList();
            UnlockFilesWriter.Write(
                manifest.PackageSlug,
                manifest.MapSpecs,
                otsForUnlocks,
                raceForUnlocks,
                stagingDataDir,
                written);
            diagnostics.Add(new(DiagnosticLevel.Info, "Unlocks",
                $"Wrote unlock files (DLC_PRODUCT entitlement + {manifest.MapSpecs.Count} world(s) + " +
                $"{otsForUnlocks.Count} OTS + {raceForUnlocks.Count} Race challenge(s))."));
        }
        catch (Exception ex)
        {
            diagnostics.Add(new(DiagnosticLevel.Error, "Unlocks", $"Build failed: {ex.Message}"));
        }

        // -- 8b. .opt sibling stubs ------------------------------------------
        // 16-byte EndC stub next to every .vlt in data/db/. Vault-package
        // validation requires the sibling to exist on the engine's loader path.
        try
        {
            OptSiblingWriter.WriteSiblings(dbDir, written);
            diagnostics.Add(new(DiagnosticLevel.Info, "OptSiblings",
                "Wrote .opt sibling stubs next to every VLT."));
        }
        catch (Exception ex)
        {
            diagnostics.Add(new(DiagnosticLevel.Error, "OptSiblings", $"Build failed: {ex.Message}"));
        }

        // -- 9. Pack cSim_Global → cSim_Global.psf ---------------------------
        // Stream File Tool walks every per-challenge mission folder and packs
        // its cSim_Global subfolder into cSim_Global.psf. Engine streams the
        // .psf (NOT the unpacked folder) when the challenge loads — without
        // packing the asset stream system gets a NULL pointer for the trigger
        // volume PSG and crashes inside the arena fixup chain.
        //
        // Both OTS AND race challenges emit a per-mission cSim_Global subfolder
        // (RaceMissionFolderWriter ships gate trigger volumes the same way OTS
        // ships discovery/scoring boundaries). Race-only builds used to skip
        // this step — the loose .psg shipped but the .psf didn't, so the engine
        // saw NULL for race gates and crashed downstream in the Lua-VM race
        // state-graph (sub_24E56C strncasecmp 0x8 = stale raw bin offset).
        int psfMissionFolderCount = otsCount + raceSpecs.Count;
        if (options.PackOtsPsf && psfMissionFolderCount > 0)
        {
            string missionsRoot = Path.Combine(stagingDataDir, "content", "missions");
            var psfResult = OtsPsfPacker.PackAll(missionsRoot);
            foreach (string d in psfResult.Diagnostics)
                diagnostics.Add(new(DiagnosticLevel.Info, "OtsPsfPacker", d));
            if (psfResult.Failed > 0)
                diagnostics.Add(new(DiagnosticLevel.Error, "OtsPsfPacker",
                    $"{psfResult.Failed} mission folder(s) failed to pack — engine WILL crash on those challenges."));
            else
                diagnostics.Add(new(DiagnosticLevel.Info, "OtsPsfPacker",
                    $"Packed {psfResult.Packed} challenge cSim_Global folder(s)."));
        }

        // -- 10. Pack BIG with bigfile.exe ------------------------------------
        // Walks the staging tree at `<exeFolder>/data/` and produces the
        // final `<outputDirectory>/<DlcFolder>/custom_<slug>.big.edat`.
        // The user's chosen output folder receives ONLY the packed .big.edat —
        // no loose `data/` tree gets dumped into it.
        if (options.PackBig)
        {
            var bigResult = BigFilePacker.Pack(stagingRoot, outputDirectory, manifest.PackageSlug);
            foreach (string d in bigResult.Diagnostics)
                diagnostics.Add(new(DiagnosticLevel.Info, "BigFilePacker", d));

            if (bigResult.Success && bigResult.OutputBigEdatPath is string bigPath)
            {
                written.Add(bigPath);

                if (options.CleanStagingAfterPack)
                {
                    try
                    {
                        Directory.Delete(stagingDataDir, recursive: true);
                        diagnostics.Add(new(DiagnosticLevel.Info, "BigFilePacker",
                            $"Removed staging tree at {stagingDataDir} (CleanStagingAfterPack=true)."));
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add(new(DiagnosticLevel.Warning, "BigFilePacker",
                            $"Staging cleanup failed: {ex.Message}"));
                    }
                }
            }
            else
            {
                diagnostics.Add(new(DiagnosticLevel.Error, "BigFilePacker",
                    "BIG pack failed — see diagnostics above. The loose staging tree is intact for debugging."));
            }
        }

        // -- 11. Manifest staging summary -------------------------------------
        // Always written at the outputDirectory root (outside data/) so users
        // have one artifact to inspect alongside the staged tree / packed BIG.
        string summaryPath = Path.Combine(outputDirectory, "build-summary.txt");
        File.WriteAllText(summaryPath, BuildSummaryText(input, manifest, otsCount, diagnostics));
        written.Add(summaryPath);

        if (!options.PackBig)
        {
            diagnostics.Add(new(DiagnosticLevel.Info, "Orchestrator",
                $"Loose staging tree written to {stagingDataDir}. Pass BuildOptions {{ PackBig = true }} to also produce a .big.edat."));
        }

        return new BuildResult
        {
            Status = diagnostics.Any(d => d.Level == DiagnosticLevel.Error) ? BuildStatus.Failed
                   : diagnostics.Any(d => d.Level == DiagnosticLevel.Warning) ? BuildStatus.SucceededWithWarnings
                   : BuildStatus.Succeeded,
            OutputDirectory = outputDirectory,
            WrittenFiles = written,
            Diagnostics = diagnostics,
            Elapsed = sw.Elapsed,
        };
    }

    private static BuildResult Fail(string outDir, List<Diagnostic> diags, Stopwatch sw) => new()
    {
        Status = BuildStatus.Failed,
        OutputDirectory = outDir,
        WrittenFiles = Array.Empty<string>(),
        Diagnostics = diags,
        Elapsed = sw.Elapsed,
    };

    private static int CopyDirectoryRecursive(string sourceDir, string destinationDir)
    {
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Source DIST folder not found: {sourceDir}");

        Directory.CreateDirectory(destinationDir);
        int copied = 0;

        foreach (string srcFile in Directory.GetFiles(sourceDir))
        {
            string fileName = Path.GetFileName(srcFile);
            string dstFile = Path.Combine(destinationDir, fileName);
            File.Copy(srcFile, dstFile, overwrite: true);
            copied++;
        }

        foreach (string srcSubDir in Directory.GetDirectories(sourceDir))
        {
            string subName = Path.GetFileName(srcSubDir);
            string dstSubDir = Path.Combine(destinationDir, subName);
            copied += CopyDirectoryRecursive(srcSubDir, dstSubDir);
        }

        return copied;
    }

    private static string BuildSummaryText(
        PackageInput input,
        ManifestArtifacts manifest,
        int otsCount,
        List<Diagnostic> diags)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Package:    {input.PackageName}  (slug={manifest.PackageSlug})");
        sb.AppendLine($"Maps:       {manifest.MapSpecs.Count}");
        foreach (var s in manifest.MapSpecs)
            sb.AppendLine($"   • {s.DisplayName}  → {s.Slug}  (WorldStream={s.WorldStreamName})");
        sb.AppendLine();
        sb.AppendLine($"Derived rows:");
        sb.AppendLine($"   Manifest VLT: {manifest.MapSpecs.Count} world + fe_locations + supporting rows");
        sb.AppendLine($"   OTS:          {otsCount} challenge(s)");
        sb.AppendLine();
        sb.AppendLine($"Diagnostics ({diags.Count}):");
        foreach (var d in diags)
            sb.AppendLine($"   [{d.Level}] {d.Source}: {d.Message}");
        return sb.ToString();
    }
}
