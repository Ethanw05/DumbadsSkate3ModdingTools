using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using ArenaBuilder.Collision;
using ArenaBuilder.Collision.Validation;
using ArenaBuilder.Core;
using ArenaBuilder.Core.Psg;
using ArenaBuilder.Core.Platforms.Common.Pegasus.AIPath;
using ArenaBuilder.Core.Platforms.Common.Pegasus.Irradiance;
using ArenaBuilder.Core.Platforms.Common.Pegasus.Mesh;
using ArenaBuilder.Core.Platforms.Common.Pegasus.WorldPainter;
using ArenaBuilder.Core.Platforms.PS3.Pegasus.AIPath;
using ArenaBuilder.Core.Platforms.PS3.Pegasus.Irradiance;
using ArenaBuilder.Core.Platforms.PS3.Pegasus.WorldPainter;
using ArenaBuilder.Glb;
using ArenaBuilder.Mesh;
using ArenaBuilder.NavPower;
using ArenaBuilder.Texture;
using ArenaBuilder.WorldPainter;

using ArenaBuilder.Core.Platforms.Common.PsgFormat;

namespace ArenaBuilder.Build;

/// <summary>
/// Two-phase tile-based build: accumulate mesh/collision per tile from GLBs, then emit PSGs to
/// <c>cPres_U_V_high</c> (mesh + textures) and <c>cSim_U_V_high</c> (collision). Every
/// full-resolution texture lives either in <c>cPres_Global</c> (shared by 2+ cPres tiles, picked
/// by <see cref="TexturePlacementPlanner"/>) or in the owning cPres tile (unique-to-tile).
///
/// <para><see cref="TileBuildOptions.GlobalOnly"/> writes everything into a single
/// <c>cPres_Global</c> collection.</para>
///
/// cSim tile collision uses per-tile folders; <see cref="TileBuildOptions.GlobalOnly"/> also uses <c>cSim_Global</c> for collision.
/// WorldPainter: emit at most one WP PSG per 128 m GenTileId cell. Multiple stream tiles can map to the same
/// cell when tile size is under 128 m; only the lexicographically first (U,V) tile ("owner") emits WP. RegisterStreamedTile (82ADFB60)
/// computes GenTileId(center-half, half*2) from the WPQUAD root; duplicates overwrite TileHandle layer slots
/// and UnRegisterStreamedTile clears them on unload. See documentation/WorldPainter_GUID_Findings.md §9.
/// </summary>
public static class TileBuildPipeline
{
    public const string CPresGlobalFolder = TileBuildOptions.CPresGlobalFolder;
    private static readonly JsonSerializerOptions WpDebugJsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Runs the full tile build for a folder of GLBs, writing output PSGs into the same folder.
    /// </summary>
    public static void Build(
        string folder,
        string[] glbPaths,
        TileBuildOptions options,
        float meshScale,
        Action<string> log,
        CancellationToken cancellationToken = default)
        => Build(folder, folder, glbPaths, options, meshScale, log, cancellationToken);

    /// <summary>
    /// Runs the full tile build for a folder of GLBs, writing output PSGs into <paramref name="outputFolder"/>.
    /// GLBs, JSONs, and splines are read from <paramref name="inputFolder"/>; all cPres*/cSim* go to <paramref name="outputFolder"/>.
    /// </summary>
    public static void Build(
        string inputFolder,
        string outputFolder,
        string[] glbPaths,
        TileBuildOptions options,
        float meshScale,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        var inputDir = Path.GetFullPath(inputFolder);
        var baseDir = Path.GetFullPath(outputFolder);

        // BlenRose sidecar JSON + global splines — both are GLB-pipeline conventions
        // tied to the input folder layout (Blender exporter writes them).
        string materialsJsonPath = Path.Combine(inputDir, "blenrose_materials.json");
        BlenroseMaterialsDb? materialsDb = null;
        if (File.Exists(materialsJsonPath))
        {
            try { materialsDb = BlenroseMaterialsDb.Load(materialsJsonPath); }
            catch (Exception ex)
            { log($"[WARN] Could not load blenrose_materials.json: {ex.Message}. Collision and material lookup will be skipped."); }
        }

        IReadOnlyList<IReadOnlyList<Vector3>>? globalSplines = LoadGlobalSplines(inputDir);
        if (globalSplines != null && globalSplines.Count > 0)
            log($"[Splines] Loaded {globalSplines.Count} spline(s) from splines.json; will split on tile boundaries and assign segments to tiles.");

        // Wrap each .glb path as an IBuildSource so the source-agnostic
        // BuildFromSources core handles the rest.
        var normalSynth = ResolveNormalSynthSettings(options);
        string? sidecarMaterialsJsonPath = File.Exists(materialsJsonPath) ? materialsJsonPath : null;
        var sources = new List<IBuildSource>(glbPaths.Length);
        foreach (var p in glbPaths)
            sources.Add(new GlbBuildSource(p, sidecarMaterialsJsonPath, normalSynth));

        BuildFromSources(baseDir, sources, materialsDb, sidecarMaterialsJsonPath, globalSplines, wpDataFolder: inputDir, options, meshScale, log, cancellationToken);
    }

    private static void ValidateOptions(TileBuildOptions options)
    {
        if (options.CPresOnly && options.CSimOnly)
            throw new InvalidOperationException("CPresOnly and CSimOnly cannot both be enabled.");
        if (options.MaxOutputPsgFiles < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxOutputPsgFiles must be >= 0 (0 = unlimited).");
        if (options.MaxAbsoluteTileIndex <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxAbsoluteTileIndex must be > 0.");
    }

    /// <summary>
    /// Shared core of the tile build: source-agnostic. <see cref="Build"/>
    /// wraps .glb paths as <see cref="GlbBuildSource"/> and calls this. The
    /// IBuildSource abstraction keeps the core decoupled from the input form.
    /// Run Phase 1 (per-source mesh / collision accumulation) → Phase 2 (texture →
    /// mesh PSG → collision/WP/NavPower PSG) → post-build cleanup.
    /// </summary>
    private static void BuildFromSources(
        string baseDir,
        IReadOnlyList<IBuildSource> sources,
        BlenroseMaterialsDb? materialsDb,
        string? sidecarMaterialsJsonPath,
        IReadOnlyList<IReadOnlyList<Vector3>>? globalSplines,
        string? wpDataFolder,
        TileBuildOptions options,
        float meshScale,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var tileOptions = options;
        var emptyTextureBuild = new GlbTextureAutoBuilder.GlbTextureAutoBuildResult(
            DiffuseGuid: null,
            NormalGuid: null,
            LightmapGuid: null,
            SpecularGuid: null,
            BuiltTextures: Array.Empty<GlbTextureAutoBuilder.BuiltTexturePsg>(),
            Warnings: Array.Empty<string>());

        // Phase 1: Accumulate per-tile (multiple sources can contribute to the same tile).
        var meshByTile = new ConcurrentDictionary<WorldTileGrid.TileKey, TileMeshAccumulator>();
        var collisionByTile = new ConcurrentDictionary<WorldTileGrid.TileKey, TileCollisionAccumulator>();
        // Accumulator for primitives whose BlenRose material has `include_in_cpres_global = true`.
        // These primitives bypass tile splitting and end up in a single cPres_Global mesh PSG —
        // intended for assets visible from many tiles (sky, distant LODs, big landmarks) so
        // they aren't duplicated per tile.
        var globalMeshAcc = new TileMeshAccumulator(meshScale);
        var globalMeshAccUsedByMaterial = new ConcurrentDictionary<(string GlbPath, string MaterialName), byte>();
        var glbInfoByPath = new ConcurrentDictionary<string, (string GlbStem, string? JsonPath)>(StringComparer.OrdinalIgnoreCase);

        var sourceByKey = new Dictionary<string, IBuildSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sources) sourceByKey[s.SourceKey] = s;

        int maxDegree = Math.Max(1, Environment.ProcessorCount - 1);
        Parallel.ForEach(
            sources,
            new ParallelOptions { MaxDegreeOfParallelism = maxDegree, CancellationToken = cancellationToken },
            source =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sourceKey = source.SourceKey;
                string glbStem = source.SourceStem;
                string dominantMaterialName = glbStem;

                // Mesh: flatten and split geometry by tile boundaries in C#.
                // GLB sources load + flatten the .glb; direct sources hand back
                // their caller-provided pre-flattened results.
                var meshResults = source.LoadMeshes(cancellationToken);
                var tilesUsed = new HashSet<WorldTileGrid.TileKey>();

                if (meshResults.Count > 0)
                {
                    dominantMaterialName = GlbUtilities.PickDominantMaterial(
                        meshResults,
                        r => r.MaterialName,
                        r => r.Indices.Count / 3);
                }

                foreach (var r in meshResults)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Per-primitive material: optional collision/pres exclusion and surface id (see blenrose_materials.json).
                    var primMat = materialsDb?.TryGetMaterial(r.MaterialName);
                    bool skipCollisionForMaterial = primMat?.ExcludeCollision == true;
                    bool skipPresForMaterial = primMat?.ExcludePres == true;
                    bool routeToGlobalForMaterial = primMat?.IncludeInCpresGlobal == true;
                    int surfaceId = 0;
                    if (!skipCollisionForMaterial && materialsDb != null)
                    {
                        var matForSurface = primMat
                            ?? materialsDb.TryGetMaterial(dominantMaterialName)
                            ?? materialsDb.TryGetMaterial(glbStem);
                        if (matForSurface != null)
                            surfaceId = SurfaceIdHelper.EncodeSurfaceId(
                                matForSurface.Collision.AudioSurface,
                                matForSurface.Collision.PhysicsSurface,
                                matForSurface.Collision.SurfacePattern);
                    }

                    // cPres_Global routing: the primitive's full mesh goes into the global
                    // accumulator with no tile split. Collision still flows through the
                    // per-tile path so a global landmark still contributes to local cSim
                    // when not also marked ExcludeCollision.
                    if (routeToGlobalForMaterial && !tileOptions.CSimOnly && !skipPresForMaterial)
                    {
                        globalMeshAcc.AddPart(r, sourceKey, glbStem);
                        globalMeshAccUsedByMaterial.TryAdd((sourceKey, r.MaterialName), 0);
                    }

                    var splitByTile = SplitMeshResultIntoTiles(r, tileOptions, cancellationToken);
                    foreach (var (tile, splitChunks) in splitByTile)
                    {
                        ValidateTileKey(tile, tileOptions);
                        if (!tileOptions.CSimOnly && !skipPresForMaterial && !routeToGlobalForMaterial)
                            tilesUsed.Add(tile);

                        if (!tileOptions.CPresOnly && !skipCollisionForMaterial)
                        {
                            var colAcc = collisionByTile.GetOrAdd(tile, _ => new TileCollisionAccumulator());
                            foreach (var splitChunk in splitChunks)
                                colAcc.AddChunk(splitChunk.Positions, IndicesToFaces(splitChunk.Indices), surfaceId, null);
                        }
                        // Per-tile mesh accumulation is skipped when this primitive is routed
                        // to cPres_Global — it must live in exactly one place.
                        if (!tileOptions.CSimOnly && !skipPresForMaterial && !routeToGlobalForMaterial)
                        {
                            var meshAcc = meshByTile.GetOrAdd(tile, _ => new TileMeshAccumulator(meshScale));
                            foreach (var splitChunk in splitChunks)
                                meshAcc.AddPart(splitChunk, sourceKey, glbStem);
                        }
                    }
                }

                if (tilesUsed.Count > 0 && meshResults.Count > 0)
                {
                    glbInfoByPath[sourceKey] = (glbStem, sidecarMaterialsJsonPath);
                }
            }
            catch (InvalidOperationException ex)
            {
                // Per-source safety: if the mesh is malformed or unsupported, log and skip this source instead of failing the whole build.
                log($"[WARN] Skipping source '{source.SourceKey}': {ex.Message}");
            }
        });

        // Split global splines on tile boundaries and assign segments to tiles (no duplication).
        if (globalSplines != null && globalSplines.Count > 0 && !tileOptions.CPresOnly)
        {
            foreach (var spline in globalSplines)
            {
                AddSplineSegmentsToTiles(spline, tileOptions, collisionByTile);
            }
        }

        // Build texture contexts per (GLB, material) — a single GLB may carry many
        // materials (BlenRose's single-scene.glb export). Each material gets its own
        // texture resolve + emit; the per-tile texture build is keyed by
        // (tile, glbPath, materialName) so a tile's mesh PSG can wire each primitive
        // to its own material's textures.
        var textureContextsByGlbMaterial = new Dictionary<(string GlbPath, string MaterialName), TextureBuildContext>();
        var glbMaterialToTiles = new Dictionary<(string GlbPath, string MaterialName), HashSet<WorldTileGrid.TileKey>>();
        foreach (var (tile, acc) in meshByTile)
        {
            foreach (var (r, glbPath, _) in acc.Parts)
            {
                var key = (glbPath, r.MaterialName);
                if (!glbMaterialToTiles.TryGetValue(key, out var set))
                {
                    set = new HashSet<WorldTileGrid.TileKey>();
                    glbMaterialToTiles[key] = set;
                }
                set.Add(tile);
            }
        }
        foreach (var (key, tilesUsed) in glbMaterialToTiles)
        {
            if (glbInfoByPath.TryGetValue(key.GlbPath, out var info))
            {
                textureContextsByGlbMaterial[key] = new TextureBuildContext(
                    key.GlbPath,
                    info.GlbStem,
                    info.JsonPath,
                    key.MaterialName,
                    tilesUsed);
            }
        }

        // Phase 2A: Emit textures.
        //
        //   1) Shared (≥2 cPres tiles) → one full-res copy in cPres_Global.
        //   2) Unique-to-tile → one full-res copy in the owning cPres tile.
        //   3) Lightmaps → always full-res in every owning cPres tile.
        //   4) GlobalOnly: everything full-res in cPres_Global.
        //
        // Mesh PSGs reference the FULL GUID G. The engine resolves G via cPres_Global
        // (always resident) or the local cPres tile (always resident in the 3×3 window).
        var textureBuildByTileGlb = new ConcurrentDictionary<(WorldTileGrid.TileKey Tile, string GlbPath, string MaterialName), GlbTextureAutoBuilder.GlbTextureAutoBuildResult>();
        var tileFoldersCreated = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        int createdCount = 0;
        var textureDeduper = new TextureDeduplicationRegistry(log, tileOptions.TargetPlatform);

        string folderSuffix = tileOptions.FolderSuffix ?? "";
        string cPresGlobalDir = Path.Combine(baseDir, CPresGlobalFolder + folderSuffix);

        int textureMaxDegree = Math.Max(1, Environment.ProcessorCount - 1);
        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = textureMaxDegree, CancellationToken = cancellationToken };

        void LogBuildOutputs(GlbTextureAutoBuilder.GlbTextureAutoBuildResult build)
        {
            foreach (var tex in build.BuiltTextures)
            {
                log($"[Texture PSG] {tex.ChannelName}: {tex.SourceImageName} {tex.Width}x{tex.Height} -> {tex.PsgPath} (GUID 0x{tex.Guid:X16})");
                int newCount = Interlocked.Increment(ref createdCount);
                ThrowIfTooManyOutputs(newCount, tileOptions);
            }
            foreach (var w in build.Warnings)
                log($"[Texture WARN] {w}");
        }

        GlbTextureAutoBuilder.ResolvedGlbTextureSources? ResolveFor(TextureBuildContext tc)
        {
            string guidNamespace = $"{baseDir}|{Path.GetFileName(tc.GlbPath)}";
            try
            {
                IBuildSource src = sourceByKey[tc.GlbPath];
                return src.ResolveTextures(tc.MaterialName, guidNamespace, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log($"[Texture ERR] resolve failed for {tc.GlbPath}: {ex.Message}");
                return null;
            }
        }

        if (tileOptions.GlobalOnly)
        {
            // Single self-contained collection: full-res textures live next to
            // mesh PSGs under their FULL GUID (bit 62 clear). No small variants
            // or budget plan — the entire map is resident, nothing to stream.
            Parallel.ForEach(textureContextsByGlbMaterial.Values, parallelOpts, textureContext =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolvedSources = ResolveFor(textureContext);
                if (resolvedSources == null) return;

                if (tileFoldersCreated.TryAdd(cPresGlobalDir, 0))
                    Directory.CreateDirectory(cPresGlobalDir);

                var textureBuild = GlbTextureAutoBuilder.EmitFullToTile(
                    resolvedSources, cPresGlobalDir, textureDeduper,
                    cancellationToken: cancellationToken);

                foreach (var presTile in textureContext.TilesUsed)
                    textureBuildByTileGlb[(presTile, textureContext.GlbPath, textureContext.MaterialName)] = textureBuild;
                LogBuildOutputs(textureBuild);
            });
        }
        else
        {
            // ── Stage 2: budget-driven tier placement (port of BlenRose) ──
            // Pass A — resolve + measure (parallel). The full encode each
            // MeasureChannels triggers is cached, so the emit pass reuses it.
            var resolvedByGlbMaterial = new ConcurrentDictionary<(string GlbPath, string MaterialName), GlbTextureAutoBuilder.ResolvedGlbTextureSources>();
            var logicalByGuid = new ConcurrentDictionary<ulong, TexturePlacementPlanner.LogicalTexture>();

            Parallel.ForEach(textureContextsByGlbMaterial.Values, parallelOpts, textureContext =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolvedSources = ResolveFor(textureContext);
                if (resolvedSources == null) return;
                resolvedByGlbMaterial[(textureContext.GlbPath, textureContext.MaterialName)] = resolvedSources;

                foreach (var (_, fullGuid, payloadSize, isLightmap) in
                         GlbTextureAutoBuilder.MeasureChannels(resolvedSources, textureDeduper, cancellationToken))
                {
                    var lt = logicalByGuid.GetOrAdd(fullGuid, g => new TexturePlacementPlanner.LogicalTexture { FullGuid = g });
                    lock (lt)
                    {
                        lt.PayloadSize = Math.Max(lt.PayloadSize, payloadSize);
                        lt.IsLightmap |= isLightmap;
                        foreach (var pt in textureContext.TilesUsed) lt.CPresTiles.Add(pt);
                    }
                }
            });

            // Pass B — plan (single-threaded, pure).
            var plan = TexturePlacementPlanner.Build(logicalByGuid.Values.ToList(), log);

            // Pass C — emit per plan (parallel over GLB+material contexts).
            Parallel.ForEach(textureContextsByGlbMaterial.Values, parallelOpts, textureContext =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!resolvedByGlbMaterial.TryGetValue((textureContext.GlbPath, textureContext.MaterialName), out var resolvedSources))
                    return; // resolve failed in pass A; already logged

                // (1) Promoted textures → one shared cPres_Global copy
                //     (full-res, full GUID). Dedup registry's
                //     first-writer-per-path collapses repeats across GLBs.
                GlbTextureAutoBuilder.ChannelEmitDecision PromotedResolver(ulong g, bool isLm)
                    => (!isLm && plan.Promoted.Contains(g))
                        ? GlbTextureAutoBuilder.ChannelEmitDecision.Full
                        : GlbTextureAutoBuilder.ChannelEmitDecision.Skip;

                if (tileFoldersCreated.TryAdd(cPresGlobalDir, 0))
                    Directory.CreateDirectory(cPresGlobalDir);
                var promotedBuild = GlbTextureAutoBuilder.EmitPlanned(
                    resolvedSources, cPresGlobalDir, textureDeduper, PromotedResolver,
                    cancellationToken: cancellationToken);
                LogBuildOutputs(promotedBuild);

                // (2) cPres tiles — full-res for lightmaps + non-promoted textures.
                //     Promoted textures live exclusively in cPres_Global. The build
                //     result carries the FULL GUIDs for mesh material wiring.
                foreach (var presTile in textureContext.TilesUsed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string presTileDir = Path.Combine(baseDir,
                        WorldTileGrid.BuildFolderName(TileBuildOptions.CPresPrefix, presTile,
                            tileOptions.TileSize, tileOptions.OriginX, tileOptions.OriginY,
                            tileOptions.TileSuffix) + folderSuffix);
                    if (tileFoldersCreated.TryAdd(presTileDir, 0))
                        Directory.CreateDirectory(presTileDir);

                    GlbTextureAutoBuilder.ChannelEmitDecision CPresResolver(ulong g, bool isLm)
                    {
                        if (isLm) return GlbTextureAutoBuilder.ChannelEmitDecision.Full;
                        if (plan.Promoted.Contains(g)) return GlbTextureAutoBuilder.ChannelEmitDecision.Skip;
                        return GlbTextureAutoBuilder.ChannelEmitDecision.Full;
                    }

                    var perTileBuild = GlbTextureAutoBuilder.EmitPlanned(
                        resolvedSources, presTileDir, textureDeduper, CPresResolver,
                        cancellationToken: cancellationToken);

                    textureBuildByTileGlb[(presTile, textureContext.GlbPath, textureContext.MaterialName)] = perTileBuild;
                    LogBuildOutputs(perTileBuild);
                }
            });
        }

        if (textureDeduper.UniqueCount > 0)
            log($"[TextureDeduper] {textureDeduper.UniqueCount} unique texture(s) after per-tile deduplication.");
        if (textureDeduper.EncodeCacheHits > 0 || textureDeduper.EncodeCacheMisses > 0)
        {
            long total = textureDeduper.EncodeCacheHits + textureDeduper.EncodeCacheMisses;
            double hitRate = total > 0 ? 100.0 * textureDeduper.EncodeCacheHits / total : 0.0;
            log($"[TextureEncodeCache] {textureDeduper.EncodeCacheHits} hits / {total} lookups ({hitRate:F1}% hit-rate; {textureDeduper.EncodeCacheMisses} unique BCn encodes).");
        }
        if (textureDeduper.LogicalWritesAttempted > 0)
            log($"[TextureLogicalWrites] {textureDeduper.LogicalWritesAttempted - textureDeduper.LogicalWritesSkipped} PSG(s) written, {textureDeduper.LogicalWritesSkipped} skipped (already written this build).");

        // Ensure every (tile, glbPath, materialName) that appears in mesh parts has a texture build (or empty).
        foreach (var (tile, acc) in meshByTile)
        {
            foreach (var (r, glbPath, _) in acc.Parts)
            {
                var key = (tile, glbPath, r.MaterialName);
                if (!textureBuildByTileGlb.ContainsKey(key))
                    textureBuildByTileGlb[key] = emptyTextureBuild;
            }
        }

        // Phase 2B: Emit mesh PSG per tile, chunked to MaxMeshesPerPsg (multiple GLBs can share one PSG).
        int maxMeshesPerPsg = Math.Max(1, Math.Min(tileOptions.MaxMeshesPerPsg, 30));
        int maxVerticesPerMeshPsg = tileOptions.MaxVerticesPerMeshPsg <= 0
            ? int.MaxValue
            : tileOptions.MaxVerticesPerMeshPsg;
        bool globalOnly = tileOptions.GlobalOnly;
        if (globalOnly && !tileOptions.CSimOnly)
        {
            Directory.CreateDirectory(cPresGlobalDir);
            log("[GlobalOnly] Writing mesh + texture output to cPres_Global as a single self-contained collection; tile folders will be empty.");
        }
        if (!tileOptions.CSimOnly)
        {
            // Parallelize across tiles. Each tile's mesh-PSG emission is
            // independent: it reads its own MeshAccumulator + tile-scoped
            // texture build map, writes to its own folder, and only touches
            // shared state via thread-safe primitives (`Interlocked` counter,
            // `ConcurrentDictionary` of created folders, the shared `log`
            // delegate which is already invoked from the parallel texture
            // phase above). The previous serial loop left ~93% of CPU idle
            // during this phase on multi-core boxes.
            int meshEmitDegree = Math.Max(1, Environment.ProcessorCount - 1);
            Parallel.ForEach(
                meshByTile,
                new ParallelOptions { MaxDegreeOfParallelism = meshEmitDegree, CancellationToken = cancellationToken },
                kv =>
            {
                var (tile, acc) = kv;
                cancellationToken.ThrowIfCancellationRequested();
                ValidateTileKey(tile, tileOptions);
                string tileFolderName = WorldTileGrid.BuildFolderName(
                    TileBuildOptions.CPresPrefix,
                    tile,
                    tileOptions.TileSize,
                    tileOptions.OriginX,
                    tileOptions.OriginY,
                    tileOptions.TileSuffix) + folderSuffix;
                string tileDir = Path.Combine(baseDir, tileFolderName);
                if (tileFoldersCreated.TryAdd(tileDir, 0))
                    Directory.CreateDirectory(tileDir);

                // When GlobalOnly, write mesh PSGs to cPres_Global only (tile dirs stay empty).
                string meshWriteDir = globalOnly ? cPresGlobalDir : tileDir;
                string meshHashPrefix = globalOnly ? "mesh_global_" + tile.U + "_" + tile.V + "_" : "mesh_" + tile.U + "_" + tile.V + "_";

                int chunkIndex = 0;
                string? materialOverride = (folderSuffix == "_proxy") ? "proxyworld.default" : null;
                foreach (var input in acc.BuildChunkedMeshInputs(tile, textureBuildByTileGlb, maxMeshesPerPsg, maxVerticesPerMeshPsg, materialOverride, log))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (input == null) continue;

                    string meshHash = Lookup8Hash.HashStringToHex(meshHashPrefix + chunkIndex);
                    string meshOutPath = Path.Combine(meshWriteDir, meshHash + tileOptions.PsgExtension);

                    var spec = tileOptions.TargetPlatform == ArenaPlatform.Xbox360
                        ? MeshRX2Composer.Compose(input)
                        : MeshPsgComposer.Compose(input);
                    using (var fs = File.Create(meshOutPath))
                        GeneralArenaBuilder.Write(spec, fs, tileOptions.TargetPlatform);
                    log($"[Mesh PSG] {meshOutPath} ({input.Parts.Count} part(s))");
                    Interlocked.Increment(ref createdCount);
                    ThrowIfTooManyOutputs(createdCount, tileOptions);
                    chunkIndex++;
                }
            });

            // Phase 2B-global: emit primitives marked "Include in cPres_Global" as one
            // (possibly chunked) mesh PSG batch under cPres_Global. These never appear in
            // per-tile cPres folders — they sit in the global folder so the engine resolves
            // them once regardless of the active tile.
            if (globalMeshAcc.Parts.Count > 0)
            {
                Directory.CreateDirectory(cPresGlobalDir);

                // Sentinel TileKey for the global bucket (will not collide with any real tile,
                // which uses small signed grid coordinates near origin).
                var globalSentinelTile = new WorldTileGrid.TileKey(int.MinValue, int.MinValue);

                // Build + emit full-res textures for every (glb, material) used by the
                // global mesh, writing them next to the mesh PSGs in cPres_Global. The
                // mesh composer keys its texture lookup by (tile, glb, material); without
                // this emit step the lookup falls back to the empty texture build and
                // the global mesh ends up untextured.
                Parallel.ForEach(globalMeshAccUsedByMaterial, parallelOpts, kv =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (glbPath, matName) = kv.Key;
                    var key = (globalSentinelTile, glbPath, matName);
                    if (textureBuildByTileGlb.ContainsKey(key))
                        return;

                    if (!glbInfoByPath.TryGetValue(glbPath, out var info))
                        return;

                    var tc = new TextureBuildContext(
                        glbPath,
                        info.GlbStem,
                        info.JsonPath,
                        matName,
                        new HashSet<WorldTileGrid.TileKey> { globalSentinelTile });

                    var resolvedSources = ResolveFor(tc);
                    if (resolvedSources == null)
                    {
                        textureBuildByTileGlb[key] = emptyTextureBuild;
                        return;
                    }

                    if (tileFoldersCreated.TryAdd(cPresGlobalDir, 0))
                        Directory.CreateDirectory(cPresGlobalDir);

                    var textureBuild = GlbTextureAutoBuilder.EmitFullToTile(
                        resolvedSources, cPresGlobalDir, textureDeduper,
                        cancellationToken: cancellationToken);

                    textureBuildByTileGlb[key] = textureBuild;
                    LogBuildOutputs(textureBuild);
                });

                int globalChunkIndex = 0;
                string globalHashPrefix = "mesh_cpres_global_";
                string? globalMaterialOverride = (folderSuffix == "_proxy") ? "proxyworld.default" : null;
                foreach (var input in globalMeshAcc.BuildChunkedMeshInputs(
                    globalSentinelTile, textureBuildByTileGlb,
                    maxMeshesPerPsg, maxVerticesPerMeshPsg, globalMaterialOverride, log))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (input == null) continue;

                    string meshHash = Lookup8Hash.HashStringToHex(globalHashPrefix + globalChunkIndex);
                    string meshOutPath = Path.Combine(cPresGlobalDir, meshHash + tileOptions.PsgExtension);

                    var spec = tileOptions.TargetPlatform == ArenaPlatform.Xbox360
                        ? MeshRX2Composer.Compose(input)
                        : MeshPsgComposer.Compose(input);
                    using (var fs = File.Create(meshOutPath))
                        GeneralArenaBuilder.Write(spec, fs, tileOptions.TargetPlatform);
                    log($"[Mesh PSG cPres_Global] {meshOutPath} ({input.Parts.Count} part(s))");
                    Interlocked.Increment(ref createdCount);
                    ThrowIfTooManyOutputs(createdCount, tileOptions);
                    globalChunkIndex++;
                }
            }
        }

        // Phase 2: Emit collision (skip when cPres Only; output to cSim_Global when Global only, but create empty tile folders).
        if (!tileOptions.CPresOnly)
        {
            string cSimGlobalDir = Path.Combine(baseDir, TileBuildOptions.CSimGlobalFolder + folderSuffix);
            if (globalOnly)
                Directory.CreateDirectory(cSimGlobalDir);

            // ── WorldPainter: load paint data and pre-compute 128 m cell ownership ──
            // wpDataFolder is null for direct-from-memory sources (BuildFromMeshes);
            // WorldPainter is GLB-pipeline-only for now.
            string? wpBinPath = wpDataFolder != null ? Path.Combine(wpDataFolder, WpSimpleFile.FileName) : null;
            bool hasWpBin = wpBinPath != null && File.Exists(wpBinPath);
            string? wpLoadError = null;
            var wpDoc = hasWpBin ? WpSimpleFile.TryLoad(wpDataFolder!, out wpLoadError) : null;
            bool canEmitWorldPainterPsg = hasWpBin && wpDoc != null;
            if (wpDoc != null)
                log($"[WorldPainter] Loaded {wpDoc.Layers.Count} layer(s), {wpDoc.Cols}×{wpDoc.Rows} grid from {WpSimpleFile.FileName}.");
            else if (hasWpBin && wpLoadError != null)
                log($"[WARN] WorldPainter: {wpLoadError} — tiles will use DefaultLayers.");
            else if (!hasWpBin)
                log($"[WorldPainter] {WpSimpleFile.FileName} not found; skipping WorldPainter PSG emission.");

            // Dense grids from sparse paint data (one array per layer guid).
            Dictionary<ulong, WpCell[]>? wpGrids = null;
            if (wpDoc is { Cols: > 0, Rows: > 0 })
            {
                wpGrids = new Dictionary<ulong, WpCell[]>(wpDoc.Layers.Count);
                int wpN = wpDoc.Cols * wpDoc.Rows;
                foreach (var layer in wpDoc.Layers)
                {
                    var arr = new WpCell[wpN];
                    foreach (var s in layer.Painted)
                        if (s.Idx < (uint)wpN)
                            arr[s.Idx] = new WpCell(s.Lo, s.Hi);
                    wpGrids[layer.Guid] = arr;
                }
            }

            const float wpCellHalf = 64f;

            // One WP PSG per unique 128 m GenTileId cell; the "owner" tile is whichever
            // stream tile contains the WP root center (or nearest by tile index as tiebreak).
            var wpCellTiles = new Dictionary<uint, List<WorldTileGrid.TileKey>>();
            var wpUnionBounds = new Dictionary<uint, (float MinX, float MaxX, float MinZ, float MaxZ)>();
            foreach (var tile in collisionByTile.Keys.OrderBy(t => t.U).ThenBy(t => t.V))
            {
                Vector2 probe = WorldTileGrid.GetWorldPainterQuadRootCenter(
                    tile, tileOptions.TileSize, tileOptions.OriginX, tileOptions.OriginY);
                uint cellId = WorldTileGrid.GenTileIdSkate(probe.X, probe.Y);
                if (!wpCellTiles.TryGetValue(cellId, out var list))
                {
                    list = new List<WorldTileGrid.TileKey>(4);
                    wpCellTiles[cellId] = list;
                }
                list.Add(tile);
                var b = WorldTileGrid.GetTileBoundsXY(tile, tileOptions.TileSize, tileOptions.OriginX, tileOptions.OriginY);
                if (!wpUnionBounds.TryGetValue(cellId, out var u))
                    wpUnionBounds[cellId] = (b.MinX, b.MaxX, b.MinZ, b.MaxZ);
                else
                    wpUnionBounds[cellId] = (
                        Math.Min(u.MinX, b.MinX), Math.Max(u.MaxX, b.MaxX),
                        Math.Min(u.MinZ, b.MinZ), Math.Max(u.MaxZ, b.MaxZ));
            }

            var wpCellOwner = new Dictionary<uint, WorldTileGrid.TileKey>(wpCellTiles.Count);
            var wpBakeRootByCell = new Dictionary<uint, Vector2>(wpUnionBounds.Count);
            foreach (var (cellId, ub) in wpUnionBounds)
            {
                Vector2 bakeRoot = WorldTileGrid.GetWorldPainterQuadRootCenterForBounds(
                    ub.MinX, ub.MaxX, ub.MinZ, ub.MaxZ);
                if (WorldTileGrid.GenTileIdSkate(bakeRoot.X, bakeRoot.Y) != cellId)
                    bakeRoot = WorldTileGrid.GetWorldPainterQuadRootCenterForGenTileId(cellId);
                wpBakeRootByCell[cellId] = bakeRoot;

                if (!wpCellTiles.TryGetValue(cellId, out var tilesForCell) || tilesForCell.Count == 0)
                    continue;
                var bestTile = tilesForCell[0];
                bool bestHasRoot = false;
                float bestDist = float.MaxValue;
                foreach (var t in tilesForCell)
                {
                    var tb = WorldTileGrid.GetTileBoundsXY(t, tileOptions.TileSize, tileOptions.OriginX, tileOptions.OriginY);
                    bool hasRoot = bakeRoot.X >= tb.MinX && bakeRoot.X < tb.MaxX &&
                                   bakeRoot.Y >= tb.MinZ && bakeRoot.Y < tb.MaxZ;
                    Vector2 tc = WorldTileGrid.GetTileCenter(t, tileOptions.TileSize, tileOptions.OriginX, tileOptions.OriginY);
                    float dx = tc.X - bakeRoot.X, dz = tc.Y - bakeRoot.Y, d2 = dx * dx + dz * dz;
                    bool wins = hasRoot && !bestHasRoot
                        || hasRoot == bestHasRoot && (d2 < bestDist - 1e-4f
                            || MathF.Abs(d2 - bestDist) < 1e-4f && (t.U < bestTile.U || t.U == bestTile.U && t.V < bestTile.V));
                    if (wins) { bestTile = t; bestHasRoot = hasRoot; bestDist = d2; }
                }
                wpCellOwner[cellId] = bestTile;
            }

            // `int` so we can use `Interlocked.Increment(ref ...)` from the
            // parallel per-tile loop below.
            int wpEmitted = 0, wpSkipped = 0;

            // ── Global NavPower mesh: build once from all collision, clip per tile ──
            GlobalNavMesh? globalNavMesh = null;
            NavPowerBuildOptions? navOpts = null;
            float navWorldMinX = 0f, navWorldMaxX = 0f, navWorldMinZ = 0f, navWorldMaxZ = 0f;
            bool navUsePerTileWithSeams = false;
            Dictionary<WorldTileGrid.TileKey, (List<Vector3> Verts, List<(int A, int B, int C)> Faces, List<int>? SurfaceIds, IReadOnlyList<IReadOnlyList<Vector3>>? Splines)>? navCollisionCache = null;
            if (tileOptions.EmitNavPower)
            {
                navCollisionCache = new Dictionary<WorldTileGrid.TileKey, (List<Vector3>, List<(int, int, int)>, List<int>?, IReadOnlyList<IReadOnlyList<Vector3>>?)>();
                var allNavVerts = new List<Vector3>();
                var allNavFaces = new List<(int A, int B, int C)>();
                float worldMinX = float.MaxValue, worldMaxX = float.MinValue;
                float worldMinZ = float.MaxValue, worldMaxZ = float.MinValue;

                foreach (var kvPre in collisionByTile)
                {
                    var built = kvPre.Value.Build(out int preWeldVerts, out int postWeldVerts);
                    navCollisionCache[kvPre.Key] = built;
                    log($"[Collision Weld] tile ({kvPre.Key.U},{kvPre.Key.V}): {preWeldVerts} -> {postWeldVerts} (merged {Math.Max(0, preWeldVerts - postWeldVerts)})");

                    int baseIdx = allNavVerts.Count;
                    allNavVerts.AddRange(built.Verts);
                    foreach (var (fa, fb, fc) in built.Faces)
                        allNavFaces.Add((fa + baseIdx, fb + baseIdx, fc + baseIdx));

                    var tb = WorldTileGrid.GetTileBoundsXY(
                        kvPre.Key, tileOptions.TileSize, tileOptions.OriginX, tileOptions.OriginY);
                    if (tb.MinX < worldMinX) worldMinX = tb.MinX;
                    if (tb.MaxX > worldMaxX) worldMaxX = tb.MaxX;
                    if (tb.MinZ < worldMinZ) worldMinZ = tb.MinZ;
                    if (tb.MaxZ > worldMaxZ) worldMaxZ = tb.MaxZ;
                }

                navWorldMinX = worldMinX;
                navWorldMaxX = worldMaxX;
                navWorldMinZ = worldMinZ;
                navWorldMaxZ = worldMaxZ;

                navOpts = tileOptions.NavPower;

                string? globalDumpPrefix = null;
                if (tileOptions.NavPower.DumpObjDir != null)
                {
                    Directory.CreateDirectory(tileOptions.NavPower.DumpObjDir);
                    globalDumpPrefix = Path.Combine(tileOptions.NavPower.DumpObjDir, "navdebug_global");
                }
                float navCs = Math.Max(1e-4f, tileOptions.NavPower.VoxelSize);
                double worldW = Math.Max(0.0, worldMaxX - worldMinX);
                double worldH = Math.Max(0.0, worldMaxZ - worldMinZ);
                double estimatedVoxels = (worldW * worldH) / (navCs * navCs);
                double globalVoxelCap = Math.Max(1d, tileOptions.NavPower.GlobalMaxEstimatedVoxels);
                bool allowGlobal = estimatedVoxels <= globalVoxelCap;
                navUsePerTileWithSeams = !allowGlobal;

                if (allowGlobal)
                {
                    log($"[NavPower] Building global Recast mesh from {allNavVerts.Count} verts, {allNavFaces.Count} tris " +
                        $"(world XZ [{worldMinX:F1}..{worldMaxX:F1}] x [{worldMinZ:F1}..{worldMaxZ:F1}], estVox={estimatedVoxels:N0})...");
                    try
                    {
                        globalNavMesh = NavPowerPsgWriter.BuildGlobalMesh(
                            allNavVerts, allNavFaces,
                            worldMinX, worldMaxX, worldMinZ, worldMaxZ,
                            navOpts, globalDumpPrefix);

                        log(globalNavMesh != null
                            ? $"[NavPower] Global mesh built ({globalNavMesh.PolygonCount} polygons). Clipping per tile..."
                            : "[NavPower] Global mesh produced zero polygons — all tiles will use fallback.");
                    }
                    catch (Exception ex) when (
                        ex.Message.Contains("rcBuildPolyMesh: Too many vertices", StringComparison.OrdinalIgnoreCase) ||
                        ex.Message.Contains("rcBuildPolyMesh", StringComparison.OrdinalIgnoreCase))
                    {
                        globalNavMesh = null;
                        navUsePerTileWithSeams = true;
                        log($"[NavPower] Global Recast mesh exceeded contour vertex limits ({ex.Message}). " +
                            "Falling back to per-tile nav build with seam context to preserve detail.");
                    }
                }
                else
                {
                    log($"[NavPower] Skipping global Recast build: estVox={estimatedVoxels:N0} exceeds cap={globalVoxelCap:N0}. " +
                        "Using per-tile nav build with neighbor seam context to reduce peak RAM.");
                }
            }

            // Parallelize per-tile NavPower / collision PSG emission.
            // Each iteration is independent: per-tile collision accumulator,
            // per-tile output folder + filename, with the only shared state
            // being thread-safe primitives (`Interlocked` counter,
            // `ConcurrentDictionary` of created folders, the `log` delegate
            // already invoked from the parallel texture phase above).
            // `wpCellOwner` is read-only during the loop. The previous
            // serial loop did Recast voxelization + 4-plane clipping + KD
            // build + image serialization per tile sequentially on one
            // core — by far the slowest phase of a tiled build on multi-core
            // machines. Near-linear speedup with core count.
            int navMaxDegree = Math.Max(1, Environment.ProcessorCount - 1);
            Parallel.ForEach(
                collisionByTile,
                new ParallelOptions { MaxDegreeOfParallelism = navMaxDegree, CancellationToken = cancellationToken },
                kv =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (tile, acc) = kv;
                ValidateTileKey(tile, tileOptions);
                string tileFolderName = WorldTileGrid.BuildFolderName(
                    TileBuildOptions.CSimPrefix,
                    tile,
                    tileOptions.TileSize,
                    tileOptions.OriginX,
                    tileOptions.OriginY,
                    tileOptions.TileSuffix) + folderSuffix;
                string tileDir = Path.Combine(baseDir, tileFolderName);
                Directory.CreateDirectory(tileDir);

                // When GlobalOnly, write collision PSGs to cSim_Global only (tile dirs stay empty).
                string collisionWriteDir = globalOnly ? cSimGlobalDir : tileDir;
                (List<Vector3> verts, List<(int, int, int)> faces, List<int>? surfaceIds, IReadOnlyList<IReadOnlyList<Vector3>>? splines) builtCollision;
                if (navCollisionCache != null)
                {
                    builtCollision = navCollisionCache[tile];
                }
                else
                {
                    builtCollision = acc.Build(out int collisionPreWeldVerts, out int collisionPostWeldVerts);
                    log($"[Collision Weld] tile ({tile.U},{tile.V}): {collisionPreWeldVerts} -> {collisionPostWeldVerts} (merged {Math.Max(0, collisionPreWeldVerts - collisionPostWeldVerts)})");
                }
                var (verts, faces, surfaceIds, splines) = builtCollision;

                // Guard: spline-only tiles (added via AddSplineSegmentsToTiles) and any tile whose
                // mesh welds down to zero geometry cannot produce a valid collision PSG — CollisionPsgComposer
                // requires at least one triangle. Skip emission (and the dependent NavPower/WP PSGs) with a
                // warning instead of aborting the whole build with "Input has no vertices or faces.".
                if (verts == null || verts.Count == 0 || faces == null || faces.Count == 0)
                {
                    int splineCount = splines?.Count ?? 0;
                    log($"[Collision PSG] SKIP tile ({tile.U},{tile.V}): no mesh geometry after welding " +
                        $"(verts={verts?.Count ?? 0}, faces={faces?.Count ?? 0}, splines={splineCount}). " +
                        "Tile likely received only splines or degenerate/empty chunks.");
                    return; // Parallel.ForEach lambda — `return` is "skip this iteration"
                }

                var collisionInput = new CollisionInputFromGlb(verts, faces, splines, surfaceIds)
                {
                    InstanceDisplayName = $"tile_{tile.U}_{tile.V}"
                };

                string collisionHash = Lookup8Hash.HashStringToHex(globalOnly ? "collision_global_" + tile.U + "_" + tile.V : "collision_" + tile.U + "_" + tile.V);
                string collisionOutPath = Path.Combine(collisionWriteDir, collisionHash + tileOptions.PsgExtension);

                var builder = new CollisionPsgBuilder
                {
                    ForceUncompressed = true,
                    EnableVertexSmoothing = true,
                    Granularity = 0.001f,
                    // TileCollisionAccumulator.Build already welds; second pass would only duplicate work.
                    WeldVerticesBeforeClustering = false
                };

                // Build into memory first, then only touch disk on success. This avoids leaving a zero-byte
                // .psg on disk if the composer ever short-circuits (belt-and-suspenders with the upstream
                // empty-tile skip above) and is the second line of defense for long batch exports.
                bool wrote;
                using (var mem = new MemoryStream())
                {
                    wrote = builder.Build(collisionInput, mem, tileOptions.TargetPlatform);
                    if (wrote)
                    {
                        using var fs = File.Create(collisionOutPath);
                        mem.Position = 0;
                        mem.CopyTo(fs);
                    }
                }
                if (!wrote)
                {
                    log($"[Collision PSG] SKIP tile ({tile.U},{tile.V}): composer produced no output (empty input reached builder).");
                    return; // Parallel.ForEach lambda — `return` skips remaining work for this tile
                }
                log($"[Collision PSG] {collisionOutPath}");
                Interlocked.Increment(ref createdCount);
                ThrowIfTooManyOutputs(createdCount, tileOptions);

                // ── WorldPainter PSG ──────────────────────────────────────────────────
                if (canEmitWorldPainterPsg)
                {
                    Vector2 wpProbe = WorldTileGrid.GetWorldPainterQuadRootCenter(
                        tile, tileOptions.TileSize, tileOptions.OriginX, tileOptions.OriginY);
                    uint wpTileId = WorldTileGrid.GenTileIdSkate(wpProbe.X, wpProbe.Y);
                    Vector2 wpRoot = wpBakeRootByCell.TryGetValue(wpTileId, out var br) ? br : wpProbe;

                    if (wpCellOwner.TryGetValue(wpTileId, out var wpOwner) && wpOwner == tile)
                    {
                        double tMinX = wpRoot.X - wpCellHalf, tMaxX = wpRoot.X + wpCellHalf;
                        double tMinZ = wpRoot.Y - wpCellHalf, tMaxZ = wpRoot.Y + wpCellHalf;

                        var layerTrees = new List<WorldPainterPsgBuilder.WorldPainterLayerTreeSpec>();
                        var wpDebugLayers = new List<object>();
                        if (wpGrids != null && wpDoc != null)
                        {
                            foreach (var (layerGuid, cells) in wpGrids)
                            {
                                var built = WorldPainterCellQuadTreeBuilder.Build(
                                    wpDoc.Cols, wpDoc.Rows,
                                    wpDoc.MinX, wpDoc.MinZ, wpDoc.MaxX, wpDoc.MaxZ,
                                    cells, tMinX, tMaxX, tMinZ, tMaxZ);
                                if (built is { } r)
                                {
                                    WorldPainterQuadTreeDataBuilder.CountQuadNodeKinds(r.Nodes, out int iNodes, out int lNodes);
                                    log($"  [WP] Layer 0x{layerGuid:X16}: nodes={r.Nodes.Count} (internal={iNodes}, leaves={lNodes}), slots={r.Slots.Count}");
                                    layerTrees.Add(new WorldPainterPsgBuilder.WorldPainterLayerTreeSpec(layerGuid, r.Nodes, r.Slots));
                                    wpDebugLayers.Add(new
                                    {
                                        layerGuidHex = $"0x{layerGuid:X16}",
                                        nodeCount = r.Nodes.Count,
                                        slotCount = r.Slots.Count,
                                        leaves = BuildWpDebugLeaves(r.Nodes, r.Slots, tMinX, tMaxX, tMinZ, tMaxZ)
                                    });
                                }
                            }
                        }

                        bool hasPaint = layerTrees.Count > 0;
                        var wpOptions = new WorldPainterPsgBuilder.WorldPainterPsgBuildOptions
                        {
                            ArenaId = 0x5750474Cu,
                            RootCenterX = wpRoot.X,
                            RootCenterY = wpRoot.Y,
                            RootHalfX = wpCellHalf,
                            RootHalfY = wpCellHalf,
                            LayerTrees = hasPaint ? layerTrees : null,
                            OmitDefaultLayerSeedFallback = hasPaint,
                            OmitUnpaintedDefaultLayerSeeds = hasPaint,
                            TocGuidSalt = FormattableString.Invariant(
                                $"{tile.U}_{tile.V}_{wpRoot.X:R}_{wpRoot.Y:R}")
                        };
                        string wpHash = Lookup8Hash.HashStringToHex(
                            globalOnly ? "worldpainter_global_" + tile.U + "_" + tile.V
                                       : "worldpainter_" + tile.U + "_" + tile.V);
                        string wpOutPath = Path.Combine(collisionWriteDir, wpHash + tileOptions.PsgExtension);
                        WorldPainterPsgBuilder.WriteMinimal(wpOutPath, wpOptions, tileOptions.TargetPlatform);
                        WriteWpQuadDebugJson(baseDir, tile.U, tile.V, wpRoot, tMinX, tMaxX, tMinZ, tMaxZ, wpDebugLayers, log);
                        log($"[WorldPainter PSG] {wpOutPath} · tile ({tile.U},{tile.V}) · root ({wpRoot.X:F1},{wpRoot.Y:F1}) · " +
                            (hasPaint
                                ? $"{layerTrees.Count} layer(s)"
                                : "DefaultLayers — no painted cells fall in this 128 m cell (expand world bounds in the UI or paint that region)"));
                        if (!hasPaint && wpDoc != null)
                        {
                            bool docOverlapsCell = wpDoc.MaxX > tMinX && wpDoc.MinX < tMaxX &&
                                                   wpDoc.MaxZ > tMinZ && wpDoc.MinZ < tMaxZ;
                            log("  [WP hint] worldpainter.bin X[" + wpDoc.MinX.ToString("F1") + ".." + wpDoc.MaxX.ToString("F1") + "] Z[" +
                                wpDoc.MinZ.ToString("F1") + ".." + wpDoc.MaxZ.ToString("F1") + "] · 128 m cell X[" +
                                tMinX.ToString("F1") + ".." + tMaxX.ToString("F1") + "] Z[" +
                                tMinZ.ToString("F1") + ".." + tMaxZ.ToString("F1") + "] · AABB overlap=" +
                                (docOverlapsCell ? "yes (you did not brush here; paint that strip or ignore)" : "no — set World bounds Min/Max to cover the whole map, e.g. match Nav mesh extent"));
                        }
                        Interlocked.Increment(ref createdCount);
                        ThrowIfTooManyOutputs(createdCount, tileOptions);
                        Interlocked.Increment(ref wpEmitted);
                    }
                    else
                    {
                        if (wpCellOwner.TryGetValue(wpTileId, out var skipOwner))
                            log($"[WorldPainter] Skipping tile ({tile.U},{tile.V}) — GenTileId 0x{wpTileId:X8} covered by ({skipOwner.U},{skipOwner.V}).");
                        Interlocked.Increment(ref wpSkipped);
                    }
                }

                if (tileOptions.EmitNavPower)
                {
                    var (navMinX, navMaxX, navMinZ, navMaxZ) = WorldTileGrid.GetTileBoundsXY(
                        tile,
                        tileOptions.TileSize,
                        tileOptions.OriginX,
                        tileOptions.OriginY);
                    string navHash = Lookup8Hash.HashStringToHex(
                        globalOnly ? "navpower_global_" + tile.U + "_" + tile.V : "navpower_" + tile.U + "_" + tile.V);
                    string navOutPath = Path.Combine(collisionWriteDir, navHash + tileOptions.PsgExtension);
                    string? dumpObjPrefix = null;
                    if (tileOptions.NavPower.DumpObjDir != null)
                        dumpObjPrefix = Path.Combine(tileOptions.NavPower.DumpObjDir, $"navdebug_{tile.U}_{tile.V}");

                    if (globalNavMesh != null)
                    {
                        NavPowerPsgWriter.WriteTilePsgFromGlobalMesh(
                            navOutPath,
                            globalNavMesh,
                            navMinX, navMaxX, navMinZ, navMaxZ,
                            navOpts,
                            fallbackVerts: verts,
                            fallbackFaces: faces,
                            dumpObjPrefix: dumpObjPrefix,
                            platform: tileOptions.TargetPlatform);
                    }
                    else if (navUsePerTileWithSeams && navCollisionCache != null && tileOptions.NavPower.IncludeNeighborSeams)
                    {
                        float seamPad = navOpts?.ComputeNavSeamPadWorld() ?? 0f;
                        float expandedMinX = navMinX - seamPad;
                        float expandedMaxX = navMaxX + seamPad;
                        float expandedMinZ = navMinZ - seamPad;
                        float expandedMaxZ = navMaxZ + seamPad;

                        // Keep recast bounds finite and within world nav extents gathered from collision tiles.
                        expandedMinX = Math.Max(expandedMinX, navWorldMinX);
                        expandedMaxX = Math.Min(expandedMaxX, navWorldMaxX);
                        expandedMinZ = Math.Max(expandedMinZ, navWorldMinZ);
                        expandedMaxZ = Math.Min(expandedMaxZ, navWorldMaxZ);

                        var (seamVerts, seamFaces) = MergeCollisionForNavPowerSeams(
                            tile,
                            expandedMinX,
                            expandedMaxX,
                            expandedMinZ,
                            expandedMaxZ,
                            navCollisionCache);

                        NavPowerPsgWriter.WriteTilePsg(
                            navOutPath,
                            seamVerts,
                            seamFaces,
                            expandedMinX,
                            expandedMaxX,
                            expandedMinZ,
                            expandedMaxZ,
                            navOpts,
                            cropNavPolygonsMinX: navMinX,
                            cropNavPolygonsMaxX: navMaxX,
                            cropNavPolygonsMinZ: navMinZ,
                            cropNavPolygonsMaxZ: navMaxZ,
                            fallbackBucketMinX: navMinX,
                            fallbackBucketMaxX: navMaxX,
                            fallbackBucketMinZ: navMinZ,
                            fallbackBucketMaxZ: navMaxZ,
                            dumpObjPrefix: dumpObjPrefix,
                            platform: tileOptions.TargetPlatform);
                    }
                    else
                    {
                        NavPowerPsgWriter.WriteTilePsg(
                            navOutPath,
                            verts,
                            faces,
                            navMinX,
                            navMaxX,
                            navMinZ,
                            navMaxZ,
                            navOpts,
                            dumpObjPrefix: dumpObjPrefix,
                            platform: tileOptions.TargetPlatform);
                    }

                    log($"[NavPower PSG] {navOutPath}");
                    Interlocked.Increment(ref createdCount);
                    ThrowIfTooManyOutputs(createdCount, tileOptions);
                }
            });

            if (canEmitWorldPainterPsg && collisionByTile.Count > 0)
                log($"[WorldPainter] {wpEmitted} WP PSG(s) emitted, {wpSkipped} skipped ({wpCellOwner.Count} unique 128 m cell(s)).");
        }

        // AIPath: scan the input folder for AIPNODE3 recording .bin files
        // (produced by AIPathRecorder/recorder.py), detect them by magic, and
        // emit one AIPath PSG per overlapping cSim tile -- same folder naming
        // as the collision pipeline so the packing step picks them up
        // automatically. .bin files that aren't AIPNODE3 (WorldPainter etc.)
        // are silently skipped here.
        int aipathPsgsWritten = 0;
        if (!string.IsNullOrEmpty(wpDataFolder) && Directory.Exists(wpDataFolder))
        {
            aipathPsgsWritten = EmitAiPathPsgsFromBinFiles(
                wpDataFolder, baseDir, tileOptions, folderSuffix,
                log, cancellationToken);
            Interlocked.Add(ref createdCount, aipathPsgsWritten);
        }

        // Irradiance: scan input folder for *.probes.json manifests produced by
        // Documentation/skate3_irradiance_addon.py, bucket per cPres_X_Y_high tile,
        // auto-thin to engine's 41×41 grid, emit one PSG per non-empty tile.
        int irradiancePsgsWritten = 0;
        if (!string.IsNullOrEmpty(wpDataFolder) && Directory.Exists(wpDataFolder))
        {
            irradiancePsgsWritten = EmitIrradiancePsgsFromProbeManifests(
                wpDataFolder, baseDir, tileOptions, folderSuffix,
                log, cancellationToken);
            Interlocked.Add(ref createdCount, irradiancePsgsWritten);
        }

        // Do not run TryCompactManagedHeap here: aggressive LOH compact can block the worker for
        // many minutes on huge maps and delays returning to the WinForms "Packing" step (user sees
        // "done" in spirit only after GC). Release caches only; call TryCompact at end of DIST flow.
        BuildMemory.ReleaseBuildWorkingSet(textureDeduper);
        log($"Done. Created {createdCount} PSG(s) in tile folders.");
    }

    /// <summary>
    /// Scan <paramref name="inputDir"/> for AIPNODE3 .bin recordings and emit
    /// one AIPath PSG per overlapping <c>cSim_X_Y_high</c> tile under
    /// <paramref name="baseDir"/>. Non-AIPNODE3 .bin files (WorldPainter etc.)
    /// are silently skipped after the magic-byte check.
    /// </summary>
    private static int EmitAiPathPsgsFromBinFiles(
        string inputDir,
        string baseDir,
        TileBuildOptions tileOptions,
        string folderSuffix,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        string[] binPaths;
        try
        {
            binPaths = Directory.GetFiles(inputDir, "*.bin", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            log($"[AIPath] Skipped .bin scan: {ex.Message}");
            return 0;
        }
        if (binPaths.Length == 0)
            return 0;

        ReadOnlySpan<byte> AipnodeMagic = stackalloc byte[]
            { (byte)'A', (byte)'I', (byte)'P', (byte)'N', (byte)'O', (byte)'D', (byte)'E', (byte)'3' };

        int totalPsgs = 0;
        foreach (var binPath in binPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Magic-byte sniff: skip anything that isn't AIPNODE3 (WorldPainter,
            // legacy formats, anything else that just happens to use .bin).
            byte[] head;
            try
            {
                using var fs = File.OpenRead(binPath);
                head = new byte[8];
                int got = fs.Read(head, 0, 8);
                if (got != 8) continue;
            }
            catch (Exception ex)
            {
                log($"[AIPath] {Path.GetFileName(binPath)}: cannot open ({ex.Message})");
                continue;
            }
            if (!head.AsSpan().SequenceEqual(AipnodeMagic))
                continue;

            AiPathBinFile.File bin;
            try
            {
                bin = AiPathBinFile.Read(binPath);
            }
            catch (Exception ex)
            {
                log($"[AIPath] {Path.GetFileName(binPath)}: read failed ({ex.Message})");
                continue;
            }

            if (bin.Paths.Count == 0)
            {
                log($"[AIPath] {Path.GetFileName(binPath)}: zero paths -- skipped");
                continue;
            }

            // Per-path world-XZ bboxes for tile bucketing.
            var bboxes = new AiPathTileBucketer.Bbox[bin.Paths.Count];
            for (int i = 0; i < bin.Paths.Count; i++)
            {
                var nodes = bin.Paths[i].Nodes;
                bboxes[i] = AiPathTileBucketer.Bbox.FromPositions(
                    Enumerable.Range(0, nodes.Count).Select(n => AiPathBinFile.ReadPos(nodes[n])));
            }

            // Use the same tile-size / origin the rest of the pipeline uses so
            // AIPath cSim folder names line up byte-for-byte with the collision
            // pipeline's cSim folders. Tile-center offset = origin + 0.5*size
            // (Skate stock convention).
            float tileSize         = tileOptions.TileSize;
            float tileCenterOffset = tileOptions.OriginX + 0.5f * tileSize;
            var buckets = AiPathTileBucketer.Bucket(bboxes, tileSize, tileCenterOffset);

            int writtenForThisBin = 0;
            foreach (var kv in buckets.OrderBy(k => k.Key.X).ThenBy(k => k.Key.Y))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tileKey = kv.Key;
                var indices = kv.Value;

                // Convert AiPathTileBucketer's world-XZ tile center back to the
                // pipeline's (U, V) tile index for matching folder names.
                int u = (int)Math.Round((tileKey.X - tileOptions.OriginX) / tileSize - 0.5);
                int v = (int)Math.Round((tileKey.Y - tileOptions.OriginY) / tileSize - 0.5);
                string folderName = WorldTileGrid.BuildFolderName(
                    TileBuildOptions.CSimPrefix,
                    new WorldTileGrid.TileKey(u, v),
                    tileOptions.TileSize, tileOptions.OriginX, tileOptions.OriginY,
                    tileOptions.TileSuffix) + folderSuffix;

                string tileDir = Path.Combine(baseDir, folderName);
                Directory.CreateDirectory(tileDir);

                // Subset the input file down to the paths in this tile.
                var subset = new AiPathBinFile.File(
                    bin.AllowedSkaters, bin.SkillLevel, bin.IsLoop, bin.NameStem,
                    indices.Select(i => bin.Paths[i]).ToList().AsReadOnly());
                // Tile key + source stem feed the TOC GUID so the per-tile PSGs
                // each get a unique GUID even when path subsets overlap.
                string tocSalt = $"{Path.GetFileNameWithoutExtension(binPath)}_{folderName}";
                byte[] psg = AiPathPsgBuilder.BuildFromBin(subset, tocGuidSalt: tocSalt, platform: tileOptions.TargetPlatform);

                // Content-hash filename per stock convention. Include the source
                // .bin stem + tile key so each .bin gets its own per-tile PSG
                // (multiple .bin recordings can coexist in the same tile dir).
                string seed = $"aipath_{Path.GetFileNameWithoutExtension(binPath)}_{folderName}";
                ulong hash = Lookup8Hash.HashString(seed);
                string outPath = Path.Combine(tileDir, $"{hash:X16}{tileOptions.PsgExtension}");
                File.WriteAllBytes(outPath, psg);
                writtenForThisBin++;
            }

            log($"[AIPath] {Path.GetFileName(binPath)}: {bin.Paths.Count} path(s)  ·  "
                + $"{buckets.Count} tile(s)  ·  wrote {writtenForThisBin} PSG(s)");
            totalPsgs += writtenForThisBin;
        }

        return totalPsgs;
    }

    /// <summary>
    /// Scan <paramref name="inputDir"/> for <c>*.probes.json</c> ProbeManifests and emit
    /// one IrradianceData PSG per overlapping <c>cPres_X_Y_high</c> tile under
    /// <paramref name="baseDir"/>. Tiles whose probe count exceeds the engine 1681 cap
    /// are auto-thinned via <see cref="IrradianceProbeBucketer.DownsampleToEngineGrid"/>
    /// (binned into the runtime 41×41 XZ SpatialGrid). Folder naming uses the same
    /// (u,v) → world conversion as the rest of the pipeline so cPres folders line up
    /// byte-for-byte with the mesh/texture pipeline.
    /// </summary>
    private static int EmitIrradiancePsgsFromProbeManifests(
        string inputDir,
        string baseDir,
        TileBuildOptions tileOptions,
        string folderSuffix,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        string[] jsonPaths;
        try
        {
            jsonPaths = Directory.GetFiles(inputDir, "*.probes.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            log($"[Irradiance] Skipped probes.json scan: {ex.Message}");
            return 0;
        }
        if (jsonPaths.Length == 0)
            return 0;

        float tileSize         = tileOptions.TileSize;
        float tileCenterOffset = tileOptions.OriginX + 0.5f * tileSize;

        int totalPsgs = 0;
        foreach (var jsonPath in jsonPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ProbeManifest manifest;
            try
            {
                manifest = ProbeManifest.Load(jsonPath);
            }
            catch (Exception ex)
            {
                log($"[Irradiance] {Path.GetFileName(jsonPath)}: load failed ({ex.Message})");
                continue;
            }

            var probes = manifest.ToProbes();
            if (probes.Count == 0)
            {
                log($"[Irradiance] {Path.GetFileName(jsonPath)}: zero probes -- skipped");
                continue;
            }

            // Bucket per pipeline tile (matches AIPath/mesh tile convention so cPres
            // folder names line up byte-for-byte with the mesh pipeline output).
            var buckets = new Dictionary<(int X, int Z), List<int>>(64);
            for (int i = 0; i < probes.Count; i++)
            {
                int kx = (int)Math.Floor((probes[i].X - tileCenterOffset) / tileSize + 0.5f);
                int kz = (int)Math.Floor((probes[i].Z - tileCenterOffset) / tileSize + 0.5f);
                int tileX = (int)Math.Round(tileCenterOffset + kx * tileSize);
                int tileZ = (int)Math.Round(tileCenterOffset + kz * tileSize);
                var key = (tileX, tileZ);
                if (!buckets.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    buckets[key] = list;
                }
                list.Add(i);
            }

            string stem = Path.GetFileNameWithoutExtension(jsonPath);
            if (stem.EndsWith(".probes", StringComparison.OrdinalIgnoreCase))
                stem = stem.Substring(0, stem.Length - ".probes".Length);

            int writtenForThisJson = 0;
            int downsampledCount  = 0;
            foreach (var kv in buckets.OrderBy(k => k.Key.X).ThenBy(k => k.Key.Z))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (tileX, tileZ) = kv.Key;
                var indices = kv.Value;

                var rawProbes = new List<Probe>(indices.Count);
                for (int i = 0; i < indices.Count; i++) rawProbes.Add(probes[indices[i]]);

                IReadOnlyList<Probe> tileProbes = rawProbes;
                if (rawProbes.Count > IrradianceDataBuilder.MaxProbesPerAsset)
                {
                    tileProbes = IrradianceProbeBucketer.DownsampleToEngineGrid(
                        rawProbes,
                        new IrradianceProbeBucketer.TileKey(tileX, tileZ),
                        IrradianceDataBuilder.MaxProbesPerAsset);
                    downsampledCount++;
                }

                int u = (int)Math.Round((tileX - tileOptions.OriginX) / tileSize - 0.5);
                int v = (int)Math.Round((tileZ - tileOptions.OriginY) / tileSize - 0.5);
                string folderName = WorldTileGrid.BuildFolderName(
                    TileBuildOptions.CPresPrefix,
                    new WorldTileGrid.TileKey(u, v),
                    tileOptions.TileSize, tileOptions.OriginX, tileOptions.OriginY,
                    tileOptions.TileSuffix) + folderSuffix;

                string tileDir = Path.Combine(baseDir, folderName);
                Directory.CreateDirectory(tileDir);

                string seed = $"irradiance_{stem}_{folderName}_n{tileProbes.Count}";
                ulong tocGuid = Lookup8Hash.HashString(seed);
                if (tocGuid == 0) tocGuid = 0x1000000000000001ul;

                byte[] psg;
                try
                {
                    psg = IrradiancePsgBuilder.Build(tileProbes, tocGuid, platform: tileOptions.TargetPlatform);
                }
                catch (Exception ex)
                {
                    log($"[Irradiance] {folderName}: build failed ({ex.Message})");
                    continue;
                }

                string outPath = Path.Combine(tileDir, $"{tocGuid:X16}{tileOptions.PsgExtension}");
                File.WriteAllBytes(outPath, psg);
                writtenForThisJson++;
            }

            string suffix = downsampledCount > 0
                ? $"  ·  {downsampledCount} tile(s) auto-thinned to 41×41 grid"
                : "";
            log($"[Irradiance] {Path.GetFileName(jsonPath)}: {probes.Count} probe(s)  ·  "
                + $"{buckets.Count} tile(s)  ·  wrote {writtenForThisJson} PSG(s){suffix}");
            totalPsgs += writtenForThisJson;
        }

        return totalPsgs;
    }

    private static void WriteWpQuadDebugJson(
        string baseDir, int tileU, int tileV, Vector2 wpRoot,
        double tileMinX, double tileMaxX, double tileMinZ, double tileMaxZ,
        List<object> wpDebugLayers,
        Action<string> log)
    {
        try
        {
            string debugDir = Path.Combine(baseDir, "_worldpainter_debug");
            Directory.CreateDirectory(debugDir);
            string path = Path.Combine(debugDir, $"worldpainter_{tileU}_{tileV}_quadtree_debug.json");
            var doc = new
            {
                schemaVersion = 1,
                source = "ArenaBuilder.TileBuildPipeline",
                tileU,
                tileV,
                rootCenterX = wpRoot.X,
                rootCenterZ = wpRoot.Y,
                rootHalfX = 64.0,
                rootHalfZ = 64.0,
                tileMinX,
                tileMaxX,
                tileMinZ,
                tileMaxZ,
                layers = wpDebugLayers
            };
            File.WriteAllText(path, JsonSerializer.Serialize(doc, WpDebugJsonOptions));
            log($"[WorldPainter Debug] {path}");
        }
        catch (Exception ex)
        {
            log($"[WARN] Could not write WP quadtree debug JSON for tile ({tileU},{tileV}): {ex.Message}");
        }
    }

    private static List<object> BuildWpDebugLeaves(
        IReadOnlyList<WorldPainterQuadTreeDataBuilder.WorldPainterQuadNode> nodes,
        IReadOnlyList<IReadOnlyList<uint>> slots,
        double tileMinX, double tileMaxX, double tileMinZ, double tileMaxZ)
    {
        var leaves = new List<object>(Math.Max(8, nodes.Count / 2));
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
            if (n.Child0 == -1)
            {
                bool isVoid = n.DictionaryLookup == 0xFFFF;
                uint lo = 0, hi = 0;
                if (!isVoid && n.DictionaryLookup < slots.Count)
                {
                    var slot = slots[n.DictionaryLookup];
                    if (slot.Count >= 2) { lo = slot[0]; hi = slot[1]; }
                }
                leaves.Add(new { minX = x0, maxX = x1, minZ = z0, maxZ = z1, @void = isVoid, lo, hi, depth });
                continue;
            }

            double mx = (x0 + x1) * 0.5;
            double mz = (z0 + z1) * 0.5;
            // Reverse push so traversal pop order is SW, NW, SE, NE (engine child order).
            stack.Push((n.Child3, mx, x1, mz, z1, depth + 1)); // NE
            stack.Push((n.Child2, mx, x1, z0, mz, depth + 1)); // SE
            stack.Push((n.Child1, x0, mx, mz, z1, depth + 1)); // NW
            stack.Push((n.Child0, x0, mx, z0, mz, depth + 1)); // SW
        }

        return leaves;
    }

    private sealed class TileMeshAccumulator
    {
        private readonly float _scale;
        private readonly List<(MeshVertexFlattener.Result Result, string GlbPath, string GlbStem)> _parts = new();
        private readonly object _sync = new();

        public IReadOnlyList<(MeshVertexFlattener.Result Result, string GlbPath, string GlbStem)> Parts => _parts;

        public TileMeshAccumulator(float scale)
        {
            _scale = scale;
        }

        /// <summary>Thread-safe: multiple GLBs can add to the same tile in parallel (Parallel.ForEach over glbPaths).</summary>
        public void AddPart(MeshVertexFlattener.Result r, string glbPath, string glbStem)
        {
            lock (_sync)
                _parts.Add((r, glbPath, glbStem));
        }

        /// <summary>
        /// Yields one IMeshPsgInput per chunk of at most maxMeshesPerPsg parts (multi-material from multiple GLBs).
        /// When <paramref name="materialOverride"/> is set (e.g. "proxyworld.default" for proxy builds), every part uses that material name.
        /// </summary>
        public IEnumerable<IMeshPsgInput?> BuildChunkedMeshInputs(
            WorldTileGrid.TileKey tile,
            IReadOnlyDictionary<(WorldTileGrid.TileKey Tile, string GlbPath, string MaterialName), GlbTextureAutoBuilder.GlbTextureAutoBuildResult> textureBuildByTileGlb,
            int maxMeshesPerPsg,
            int maxVerticesPerPsg,
            string? materialOverride = null,
            Action<string>? log = null)
        {
            if (_parts.Count == 0)
                yield break;

            var emptyTextureBuild = new GlbTextureAutoBuilder.GlbTextureAutoBuildResult(
                DiffuseGuid: null,
                NormalGuid: null,
                LightmapGuid: null,
                SpecularGuid: null,
                BuiltTextures: Array.Empty<GlbTextureAutoBuilder.BuiltTexturePsg>(),
                Warnings: Array.Empty<string>());

            // Deterministic part order so part index ↔ material index is stable across runs (avoids
            // scattered black triangles when parallel AddPart order would otherwise vary).
            var orderedParts = _parts
                .Select((p, index) => (Part: p, Index: index))
                .OrderBy(x => x.Part.GlbPath)
                .ThenBy(x => x.Part.GlbStem)
                .ThenBy(x => x.Index)
                .Select(x => x.Part)
                .ToList();

            if (maxVerticesPerPsg <= 0)
                maxVerticesPerPsg = int.MaxValue;

            var budgetedParts = new List<(MeshVertexFlattener.Result Result, string GlbPath, string GlbStem)>(orderedParts.Count);
            foreach (var part in orderedParts)
            {
                foreach (var split in MeshVertexFlattener.SplitByVertexBudget(part.Result, maxVerticesPerPsg))
                    budgetedParts.Add((split, part.GlbPath, part.GlbStem));
            }

            int chunkIndex = 0;
            for (int start = 0; start < budgetedParts.Count;)
            {
                var chunk = new List<(MeshVertexFlattener.Result Result, string GlbPath, string GlbStem)>();
                int chunkVertexCount = 0;
                while (start < budgetedParts.Count && chunk.Count < maxMeshesPerPsg)
                {
                    var candidate = budgetedParts[start];
                    int candidateVertices = candidate.Result.Positions.Count;
                    bool wouldExceedVertexBudget =
                        chunk.Count > 0 &&
                        chunkVertexCount > 0 &&
                        chunkVertexCount + candidateVertices > maxVerticesPerPsg;
                    if (wouldExceedVertexBudget)
                        break;

                    chunk.Add(candidate);
                    chunkVertexCount += candidateVertices;
                    start++;
                }

                if (chunk.Count == 0)
                {
                    chunk.Add(budgetedParts[start]);
                    start++;
                }

                var meshParts = new List<MeshPart>();
                var perPartMaterials = new List<PerPartMaterial>();
                var globalMin = new Vector3(float.MaxValue);
                var globalMax = new Vector3(float.MinValue);

                for (int i = 0; i < chunk.Count; i++)
                {
                    var (r, glbPath, glbStem) = chunk[i];
                    var vertexData = MeshVertexPacker.PackVertices(r.Positions, r.Normals, r.Uvs, r.Indices, _scale, r.Uvs1);
                    var indexData = MeshIndexPacker.PackIndices(r.Indices, false);
                    meshParts.Add(new MeshPart(vertexData, indexData, i));
                    globalMin = Vector3.Min(globalMin, r.Bounds.Min);
                    globalMax = Vector3.Max(globalMax, r.Bounds.Max);

                    string materialName = materialOverride ?? r.MaterialName;
                    var textureBuild = textureBuildByTileGlb.TryGetValue((tile, glbPath, r.MaterialName), out var tb) ? tb : emptyTextureBuild;
                    var overrides = textureBuild.HasOverrides
                        ? new RenderMaterialDataBuilder.MaterialTextureOverrides(
                            NameChannelGuid: textureBuild.DiffuseGuid,
                            DiffuseGuid: textureBuild.DiffuseGuid,
                            NormalGuid: textureBuild.NormalGuid,
                            LightmapGuid: textureBuild.LightmapGuid,
                            SpecularGuid: textureBuild.SpecularGuid)
                        : null;
                    string attributorStream = materialOverride != null ? materialOverride : (textureBuild.AttributorMaterialStream ?? "");
                    perPartMaterials.Add(new PerPartMaterial(materialName, overrides, attributorStream, textureBuild.ChannelConfig));
                }

                string instanceName = chunk.Count == 1 && chunk[0].GlbStem != null
                    ? chunk[0].GlbStem
                    : $"tile_{tile.U}_{tile.V}_chunk_{chunkIndex}";
                string? instanceGuidNamespace = materialOverride != null ? "mesh_proxy" : null;
                yield return new MeshInputFromTileParts(
                    (globalMin.X * _scale, globalMin.Y * _scale, globalMin.Z * _scale),
                    (globalMax.X * _scale, globalMax.Y * _scale, globalMax.Z * _scale),
                    meshParts,
                    perPartMaterials,
                    instanceName,
                    instanceGuidNamespace);
                chunkIndex++;
            }
        }
    }

    private sealed record TextureBuildContext(
        string GlbPath,
        string GlbStem,
        string? JsonPath,
        string MaterialName,
        HashSet<WorldTileGrid.TileKey> TilesUsed);

    private readonly record struct SplitVertex(Vector3 Pos, Vector3 Normal, Vector2 Uv, Vector2 Uv1);

    private sealed class TileGeometryBuffer
    {
        private readonly List<Vector3> _positions = new();
        private readonly List<Vector3> _normals = new();
        private readonly List<Vector2> _uvs = new();
        private readonly List<Vector2> _uvs1 = new();
        private readonly List<int> _indices = new();
        private readonly Dictionary<SplitVertex, int> _vertexMap = new();
        private Vector3 _min = new(float.MaxValue, float.MaxValue, float.MaxValue);
        private Vector3 _max = new(float.MinValue, float.MinValue, float.MinValue);

        private int AddVertex(in SplitVertex v)
        {
            if (_vertexMap.TryGetValue(v, out int existingIndex))
                return existingIndex;
            int idx = _positions.Count;
            _vertexMap[v] = idx;
            _positions.Add(v.Pos);
            _normals.Add(v.Normal);
            _uvs.Add(v.Uv);
            _uvs1.Add(v.Uv1);
            _min = Vector3.Min(_min, v.Pos);
            _max = Vector3.Max(_max, v.Pos);
            return idx;
        }

        public void AddTriangle(in SplitVertex a, in SplitVertex b, in SplitVertex c)
        {
            int i0 = AddVertex(a);
            int i1 = AddVertex(b);
            int i2 = AddVertex(c);
            _indices.Add(i0);
            _indices.Add(i1);
            _indices.Add(i2);
        }

        public MeshVertexFlattener.Result ToResult(string materialName)
        {
            return new MeshVertexFlattener.Result(
                _positions.ToArray(),
                _normals.ToArray(),
                _uvs.ToArray(),
                _uvs1.Count > 0 ? _uvs1.ToArray() : null,
                _indices.ToArray(),
                materialName,
                (_min, _max));
        }
    }

    private static Dictionary<WorldTileGrid.TileKey, List<MeshVertexFlattener.Result>> SplitMeshResultIntoTiles(
        MeshVertexFlattener.Result source,
        TileBuildOptions options,
        CancellationToken cancellationToken)
    {
        if (options.MaxTilesPerTriangle <= 0)
            throw new InvalidOperationException($"Invalid MaxTilesPerTriangle: {options.MaxTilesPerTriangle}. Must be > 0.");

        var tileBuffers = new Dictionary<WorldTileGrid.TileKey, TileGeometryBuffer>();
        var indices = source.Indices;
        var positions = source.Positions;
        var normals = source.Normals;
        var uvs = source.Uvs;
        var uvs1 = source.Uvs1;

        static Vector2 GetUv(IReadOnlyList<Vector2> list, int i) =>
            i < list.Count ? list[i] : Vector2.Zero;
        static Vector2 GetUv1(IReadOnlyList<Vector2>? list, IReadOnlyList<Vector2> fallback, int i) =>
            list != null && i < list.Count ? list[i] : GetUv(fallback, i);

        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            if ((i & 0x3FF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            int i0 = indices[i];
            int i1 = indices[i + 1];
            int i2 = indices[i + 2];
            // Clamp indices so no triangle is dropped; out-of-range indices become degenerate but still emit.
            int nPos = positions.Count;
            if (nPos == 0) continue;
            i0 = Math.Clamp(i0, 0, nPos - 1);
            i1 = Math.Clamp(i1, 0, nPos - 1);
            i2 = Math.Clamp(i2, 0, nPos - 1);

            var a = new SplitVertex(
                positions[i0],
                i0 < normals.Count ? normals[i0] : Vector3.UnitY,
                GetUv(uvs, i0),
                GetUv1(uvs1, uvs, i0));
            var b = new SplitVertex(
                positions[i1],
                i1 < normals.Count ? normals[i1] : Vector3.UnitY,
                GetUv(uvs, i1),
                GetUv1(uvs1, uvs, i1));
            var c = new SplitVertex(
                positions[i2],
                i2 < normals.Count ? normals[i2] : Vector3.UnitY,
                GetUv(uvs, i2),
                GetUv1(uvs1, uvs, i2));

            // Match collision pipeline: TriangleValidator::IsTriangleValid / TriangleValidation.IsTriangleValid
            if (!TriangleValidation.IsTriangleValid(a.Pos, b.Pos, c.Pos))
                continue;

            float minX = MathF.Min(a.Pos.X, MathF.Min(b.Pos.X, c.Pos.X));
            float maxX = MathF.Max(a.Pos.X, MathF.Max(b.Pos.X, c.Pos.X));
            float minZ = MathF.Min(a.Pos.Z, MathF.Min(b.Pos.Z, c.Pos.Z));
            float maxZ = MathF.Max(a.Pos.Z, MathF.Max(b.Pos.Z, c.Pos.Z));

            if (!IsFinite(minX) || !IsFinite(maxX) || !IsFinite(minZ) || !IsFinite(maxZ))
            {
                throw new InvalidOperationException(
                    $"Non-finite triangle bounds encountered during tile split. " +
                    $"minX={minX}, maxX={maxX}, minZ={minZ}, maxZ={maxZ}.");
            }

            var (uMin, uMax, vMin, vMax) = WorldTileGrid.GetTileRangeForBoundsXY(
                minX, maxX, minZ, maxZ,
                options.TileSize, options.OriginX, options.OriginY);

            long spanU = (long)uMax - uMin + 1;
            long spanV = (long)vMax - vMin + 1;
            long tileCoverage = spanU * spanV;
            if (tileCoverage > options.MaxTilesPerTriangle)
            {
                throw new InvalidOperationException(
                    $"Triangle spans too many tiles ({tileCoverage}) and was blocked by safety cap ({options.MaxTilesPerTriangle}). " +
                    $"Bounds XZ=({minX},{minZ})..({maxX},{maxZ}), tile range U[{uMin}..{uMax}] V[{vMin}..{vMax}], " +
                    $"tileSize={options.TileSize}, origin=({options.OriginX},{options.OriginY}).");
            }

            // Hot-path: triangle fully contained in one tile, no work needed.
            if (uMin == uMax && vMin == vMax)
            {
                var key = new WorldTileGrid.TileKey(uMin, vMin);
                if (!tileBuffers.TryGetValue(key, out var buffer))
                {
                    buffer = new TileGeometryBuffer();
                    tileBuffers[key] = buffer;
                }
                buffer.AddTriangle(a, b, c);
                continue;
            }

            // Multi-tile triangle: assign to ONE tile by XZ centroid (no clipping).
            //
            // The previous implementation clipped every multi-tile triangle at each tile boundary it
            // crossed, emitting interpolated fragments to each tile's buffer. That copies original
            // vertices into both adjacent tiles AND adds new interpolated boundary vertices, inflating
            // total per-tile vertex count to 2-3x the source. Measured on a 150k-vert source map:
            // tile (-1,-1) = 117k, tile (0,-1) = 122k, sum ~315k. Stock engine handles 5-20k verts/tile
            // comfortably; 120k blows the RSX command FIFO during streaming and hangs render_thread
            // on cellGcmSys_F80196C1's command-buffer-drain wait loop (see infinite-load investigation).
            //
            // Centroid assignment puts the whole triangle in one tile. A triangle whose centroid sits
            // in tile A but extends into tile B is rendered only when A is loaded. Sk3 streams a 3x3
            // tile window around the player so adjacent tiles are always co-resident; the small overhang
            // is visible whenever the centroid tile is in view, which is the only time it matters.
            // For exotic huge triangles spanning many tiles, MaxTilesPerTriangle (checked above) still
            // catches pathological input.
            float cX = (a.Pos.X + b.Pos.X + c.Pos.X) / 3f;
            float cZ = (a.Pos.Z + b.Pos.Z + c.Pos.Z) / 3f;
            var centroidTile = WorldTileGrid.GetTileForPoint(
                new Vector3(cX, 0f, cZ),
                options.TileSize,
                options.OriginX,
                options.OriginY);
            if (!tileBuffers.TryGetValue(centroidTile, out var centroidBuffer))
            {
                centroidBuffer = new TileGeometryBuffer();
                tileBuffers[centroidTile] = centroidBuffer;
            }
            centroidBuffer.AddTriangle(a, b, c);
        }

        if (tileBuffers.Count == 0)
        {
            var fallbackTile = WorldTileGrid.GetTileForBounds(
                (source.Bounds.Min, source.Bounds.Max),
                options.TileSize,
                options.OriginX,
                options.OriginY);
            return new Dictionary<WorldTileGrid.TileKey, List<MeshVertexFlattener.Result>>
            {
                [fallbackTile] = MeshVertexFlattener.ChunkResultIfOverflow(source).ToList()
            };
        }

        var result = new Dictionary<WorldTileGrid.TileKey, List<MeshVertexFlattener.Result>>();
        foreach (var (tile, buffer) in tileBuffers)
        {
            var raw = buffer.ToResult(source.MaterialName);
            // Defensive: skip only empty/degenerate tile buffers (in practice each buffer has >= 1 triangle).
            if (raw.Positions.Count == 0 || raw.Indices.Count < 3)
                continue;
            result[tile] = MeshVertexFlattener.ChunkResultIfOverflow(raw).ToList();
        }

        if (result.Count == 0)
        {
            var fallbackTile = WorldTileGrid.GetTileForBounds(
                (source.Bounds.Min, source.Bounds.Max),
                options.TileSize,
                options.OriginX,
                options.OriginY);
            return new Dictionary<WorldTileGrid.TileKey, List<MeshVertexFlattener.Result>>
            {
                [fallbackTile] = MeshVertexFlattener.ChunkResultIfOverflow(source).ToList()
            };
        }

        return result;
    }

    private static List<SplitVertex> ClipTriangleToTile(
        in SplitVertex a,
        in SplitVertex b,
        in SplitVertex c,
        float minX,
        float maxX,
        float minZ,
        float maxZ)
    {
        var poly = new List<SplitVertex>(3) { a, b, c };
        poly = ClipPolygon(poly, p => p.Pos.X >= minX, (s, e) => IntersectAtX(s, e, minX));
        if (poly.Count == 0) return poly;
        poly = ClipPolygon(poly, p => p.Pos.X <= maxX, (s, e) => IntersectAtX(s, e, maxX));
        if (poly.Count == 0) return poly;
        poly = ClipPolygon(poly, p => p.Pos.Z >= minZ, (s, e) => IntersectAtZ(s, e, minZ));
        if (poly.Count == 0) return poly;
        poly = ClipPolygon(poly, p => p.Pos.Z <= maxZ, (s, e) => IntersectAtZ(s, e, maxZ));
        return poly;
    }

    private static List<SplitVertex> ClipPolygon(
        IReadOnlyList<SplitVertex> input,
        Func<SplitVertex, bool> isInside,
        Func<SplitVertex, SplitVertex, SplitVertex> intersect)
    {
        if (input.Count == 0)
            return new List<SplitVertex>();

        var output = new List<SplitVertex>(input.Count + 2);
        var prev = input[input.Count - 1];
        bool prevInside = isInside(prev);

        for (int i = 0; i < input.Count; i++)
        {
            var cur = input[i];
            bool curInside = isInside(cur);

            if (curInside)
            {
                if (!prevInside)
                    output.Add(intersect(prev, cur));
                output.Add(cur);
            }
            else if (prevInside)
            {
                output.Add(intersect(prev, cur));
            }

            prev = cur;
            prevInside = curInside;
        }

        return output;
    }

    private static SplitVertex IntersectAtX(in SplitVertex a, in SplitVertex b, float xPlane)
    {
        float denom = b.Pos.X - a.Pos.X;
        float t = MathF.Abs(denom) < 1e-8f ? 0f : (xPlane - a.Pos.X) / denom;
        t = Math.Clamp(t, 0f, 1f);
        return LerpVertex(a, b, t);
    }

    private static SplitVertex IntersectAtZ(in SplitVertex a, in SplitVertex b, float zPlane)
    {
        float denom = b.Pos.Z - a.Pos.Z;
        float t = MathF.Abs(denom) < 1e-8f ? 0f : (zPlane - a.Pos.Z) / denom;
        t = Math.Clamp(t, 0f, 1f);
        return LerpVertex(a, b, t);
    }

    private static SplitVertex LerpVertex(in SplitVertex a, in SplitVertex b, float t)
    {
        var n = Vector3.Lerp(a.Normal, b.Normal, t);
        if (n.LengthSquared() > 1e-12f)
            n = Vector3.Normalize(n);
        else
            n = a.Normal;
        return new SplitVertex(
            Vector3.Lerp(a.Pos, b.Pos, t),
            n,
            Vector2.Lerp(a.Uv, b.Uv, t),
            Vector2.Lerp(a.Uv1, b.Uv1, t));
    }

    private static bool IsFinite(float v) => !(float.IsNaN(v) || float.IsInfinity(v));

    private static void ValidateTileKey(WorldTileGrid.TileKey tile, TileBuildOptions options)
    {
        int max = options.MaxAbsoluteTileIndex;
        if (Math.Abs(tile.U) > max || Math.Abs(tile.V) > max)
        {
            throw new InvalidOperationException(
                $"Tile index out of safety bounds: ({tile.U},{tile.V}) exceeds MaxAbsoluteTileIndex={max}. " +
                "Check mesh scale/transforms/origin/tile size.");
        }
    }

    /// <summary>
    /// Loads all splines from splines.json in the build folder (BlenRose export).
    /// Returns null if file is missing or empty. Format: { "splines": [ { "points": [[x,y,z],...] }, ... ] }.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<Vector3>>? LoadGlobalSplines(string baseDir)
    {
        string path = Path.Combine(baseDir, "splines.json");
        if (!File.Exists(path))
            return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            if (!root.TryGetProperty("splines", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<IReadOnlyList<Vector3>>();
            foreach (var el in arr.EnumerateArray())
            {
                if (!el.TryGetProperty("points", out var pts) || pts.ValueKind != JsonValueKind.Array)
                    continue;
                var points = new List<Vector3>();
                foreach (var p in pts.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 3)
                        continue;
                    float x = (float)p[0].GetDouble();
                    float y = (float)p[1].GetDouble();
                    float z = (float)p[2].GetDouble();
                    points.Add(new Vector3(x, y, z));
                }
                if (points.Count >= 2)
                    list.Add(points);
            }
            return list.Count > 0 ? list : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Splits a spline polyline on tile boundaries and assigns each subsegment to exactly one tile.
    /// Contiguous subsegments that remain in the same tile are merged back into one tile-local spline
    /// so we preserve rail continuity inside the tile instead of emitting one 2-point spline per piece.
    /// </summary>
    private static void AddSplineSegmentsToTiles(
        IReadOnlyList<Vector3> points,
        TileBuildOptions options,
        ConcurrentDictionary<WorldTileGrid.TileKey, TileCollisionAccumulator> collisionByTile)
    {
        if (points.Count < 2)
            return;

        const float pointJoinEpsilonSq = 1e-10f;
        bool hasCurrentTile = false;
        WorldTileGrid.TileKey currentTile = default;
        List<Vector3>? currentSpline = null;

        static bool PointsNearlyEqual(Vector3 a, Vector3 b, float epsilonSq)
            => Vector3.DistanceSquared(a, b) <= epsilonSq;

        void FlushCurrentSpline()
        {
            if (!hasCurrentTile || currentSpline == null || currentSpline.Count < 2)
                return;

            ValidateTileKey(currentTile, options);
            var acc = collisionByTile.GetOrAdd(currentTile, _ => new TileCollisionAccumulator());
            acc.AddSplines(new[]
            {
                (IReadOnlyList<Vector3>)currentSpline
            });

            currentSpline = null;
            hasCurrentTile = false;
        }

        for (int i = 0; i < points.Count - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];

            // Degenerate segment.
            if (a.X == b.X && a.Z == b.Z)
                continue;

            var segmentPoints = new List<Vector3>();
            segmentPoints.Add(a);

            var ts = CollectSegmentGridIntersections(a, b, options);
            if (ts.Count > 0)
            {
                ts.Sort();
                foreach (var t in ts)
                {
                    float x = a.X + (b.X - a.X) * t;
                    float y = a.Y + (b.Y - a.Y) * t;
                    float z = a.Z + (b.Z - a.Z) * t;
                    segmentPoints.Add(new Vector3(x, y, z));
                }
            }

            segmentPoints.Add(b);

            for (int j = 0; j < segmentPoints.Count - 1; j++)
            {
                var p0 = segmentPoints[j];
                var p1 = segmentPoints[j + 1];

                var mid = new Vector3(
                    0.5f * (p0.X + p1.X),
                    0.5f * (p0.Y + p1.Y),
                    0.5f * (p0.Z + p1.Z));
                var tile = WorldTileGrid.GetTileForPoint(
                    new Vector3(mid.X, 0f, mid.Z),
                    options.TileSize,
                    options.OriginX,
                    options.OriginY);

                bool continuesCurrentSpline =
                    hasCurrentTile
                    && tile.Equals(currentTile)
                    && currentSpline != null
                    && PointsNearlyEqual(currentSpline[^1], p0, pointJoinEpsilonSq);

                if (!continuesCurrentSpline)
                {
                    FlushCurrentSpline();
                    currentTile = tile;
                    hasCurrentTile = true;
                    currentSpline = new List<Vector3> { p0, p1 };
                    continue;
                }

                if (!PointsNearlyEqual(currentSpline![^1], p1, pointJoinEpsilonSq))
                    currentSpline.Add(p1);
            }
        }

        FlushCurrentSpline();
    }

    /// <summary>
    /// Returns parametric positions t in (0,1) where segment AB crosses tile grid lines in XZ.
    /// </summary>
    private static List<float> CollectSegmentGridIntersections(
        Vector3 a,
        Vector3 b,
        TileBuildOptions options)
    {
        var ts = new List<float>();

        float originX = options.OriginX;
        float originZ = options.OriginY;
        float tileSize = options.TileSize;

        float minX = MathF.Min(a.X, b.X);
        float maxX = MathF.Max(a.X, b.X);
        float minZ = MathF.Min(a.Z, b.Z);
        float maxZ = MathF.Max(a.Z, b.Z);

        const float epsilon = 1e-5f;

        float dx = b.X - a.X;
        float dz = b.Z - a.Z;

        if (MathF.Abs(dx) > epsilon)
        {
            // Vertical grid lines x = originX + k * tileSize.
            float kStart = MathF.Floor((minX - originX) / tileSize) + 1f;
            float kEnd = MathF.Floor((maxX - originX) / tileSize);
            for (float k = kStart; k <= kEnd; k += 1f)
            {
                float x = originX + k * tileSize;
                float t = (x - a.X) / dx;
                if (t > epsilon && t < 1f - epsilon)
                    AddUniqueT(ts, t, epsilon);
            }
        }

        if (MathF.Abs(dz) > epsilon)
        {
            // Horizontal grid lines z = originZ + k * tileSize.
            float kStart = MathF.Floor((minZ - originZ) / tileSize) + 1f;
            float kEnd = MathF.Floor((maxZ - originZ) / tileSize);
            for (float k = kStart; k <= kEnd; k += 1f)
            {
                float z = originZ + k * tileSize;
                float t = (z - a.Z) / dz;
                if (t > epsilon && t < 1f - epsilon)
                    AddUniqueT(ts, t, epsilon);
            }
        }

        return ts;
    }

    private static void AddUniqueT(List<float> ts, float t, float epsilon)
    {
        for (int i = 0; i < ts.Count; i++)
        {
            if (MathF.Abs(ts[i] - t) <= epsilon)
                return;
        }
        ts.Add(t);
    }

    private static void ThrowIfTooManyOutputs(int createdCount, TileBuildOptions options)
    {
        if (options.MaxOutputPsgFiles > 0 && createdCount > options.MaxOutputPsgFiles)
            throw new InvalidOperationException(
                $"Aborted build: output PSG count ({createdCount}) exceeded safety cap MaxOutputPsgFiles={options.MaxOutputPsgFiles} (0 = unlimited).");
    }

    private static DerivedTextureGenerator.NormalSynthSettings ResolveNormalSynthSettings(TileBuildOptions options)
    {
        var defaults = DerivedTextureGenerator.DefaultNormalSettings;
        return new DerivedTextureGenerator.NormalSynthSettings(
            Strength: options.NormalSynthStrength ?? defaults.Strength,
            Level: options.NormalSynthLevel ?? defaults.Level,
            BlurSharp: options.NormalSynthBlurSharp ?? defaults.BlurSharp,
            MaxWidth: defaults.MaxWidth,
            MaxHeight: defaults.MaxHeight,
            MinTangentSpaceZ: defaults.MinTangentSpaceZ);
    }

    private static IReadOnlyList<(int V0, int V1, int V2)> IndicesToFaces(IReadOnlyList<int> indices)
    {
        if (indices.Count < 3)
            return Array.Empty<(int, int, int)>();
        var faceCount = indices.Count / 3;
        var faces = new List<(int, int, int)>(faceCount);
        for (int i = 0; i + 2 < indices.Count; i += 3)
            faces.Add((indices[i], indices[i + 1], indices[i + 2]));
        return faces;
    }

    private sealed class MeshInputFromTileParts : IMeshPsgInput
    {
        private readonly GlbTextureAutoBuilder.GlbTextureAutoBuildResult? _textureBuild;

        public (float X, float Y, float Z) BoundsMin { get; }
        public (float X, float Y, float Z) BoundsMax { get; }
        public IReadOnlyList<MeshPart> Parts { get; }
        public string MaterialName { get; }
        public RenderMaterialDataBuilder.MaterialTextureOverrides? TextureChannelOverrides { get; }
        public string? AttributorMaterialPath { get; }
        public RenderMaterialDataBuilder.BlenroseChannelConfig? ChannelConfig { get; }
        public string? InstanceDisplayName { get; }
        public string? InstanceGuidNamespace { get; }
        public IReadOnlyList<PerPartMaterial>? PerPartMaterials { get; }

        /// <summary>Single-material (one GLB): one material name and one texture build for all parts.</summary>
        public MeshInputFromTileParts(
            (float, float, float) boundsMin,
            (float, float, float) boundsMax,
            IReadOnlyList<MeshPart> parts,
            string materialName,
            GlbTextureAutoBuilder.GlbTextureAutoBuildResult textureBuild,
            string instanceDisplayName)
        {
            BoundsMin = boundsMin;
            BoundsMax = boundsMax;
            Parts = parts;
            MaterialName = materialName;
            _textureBuild = textureBuild;
            InstanceDisplayName = instanceDisplayName;
            InstanceGuidNamespace = null;
            PerPartMaterials = null;

            TextureChannelOverrides = textureBuild.HasOverrides
                ? new RenderMaterialDataBuilder.MaterialTextureOverrides(
                    NameChannelGuid: textureBuild.DiffuseGuid,
                    DiffuseGuid: textureBuild.DiffuseGuid,
                    NormalGuid: textureBuild.NormalGuid,
                    LightmapGuid: textureBuild.LightmapGuid,
                    SpecularGuid: textureBuild.SpecularGuid)
                : null;
            AttributorMaterialPath = textureBuild.AttributorMaterialStream;
            ChannelConfig = textureBuild.ChannelConfig;
        }

        /// <summary>Multi-material (multiple GLBs): one material and texture overrides per part.</summary>
        public MeshInputFromTileParts(
            (float, float, float) boundsMin,
            (float, float, float) boundsMax,
            IReadOnlyList<MeshPart> parts,
            IReadOnlyList<PerPartMaterial> perPartMaterials,
            string instanceDisplayName,
            string? instanceGuidNamespace = null)
        {
            if (parts.Count != perPartMaterials.Count)
                throw new ArgumentException("Parts and perPartMaterials must have the same count.");
            BoundsMin = boundsMin;
            BoundsMax = boundsMax;
            Parts = parts;
            PerPartMaterials = perPartMaterials;
            InstanceDisplayName = instanceDisplayName;
            InstanceGuidNamespace = instanceGuidNamespace;
            _textureBuild = null;
            MaterialName = perPartMaterials[0].MaterialName;
            TextureChannelOverrides = perPartMaterials[0].TextureOverrides;
            AttributorMaterialPath = perPartMaterials[0].AttributorMaterialPath;
            ChannelConfig = null; // Multi-material uses PerPartMaterials[].ChannelConfig
        }
    }

    private sealed class TileCollisionAccumulator
    {
        private readonly object _sync = new();
        private readonly List<List<Vector3>> _vertChunks = new();
        private readonly List<List<(int, int, int)>> _faceChunks = new();
        private readonly List<int> _surfaceIds = new();
        private readonly List<IReadOnlyList<Vector3>> _splines = new();

        public void AddChunk(
            IReadOnlyList<Vector3> verts,
            IReadOnlyList<(int V0, int V1, int V2)> faces,
            int surfaceId,
            IReadOnlyList<IReadOnlyList<Vector3>>? splines)
        {
            // Degenerate/empty chunks must not create lockstep entries — they would inflate chunk counts
            // without contributing geometry, and could yield a tile that welds to 0 verts/0 faces.
            bool hasMesh = verts != null && verts.Count > 0 && faces != null && faces.Count > 0;

            // Parallel GLB processing can hit the same tile from multiple threads.
            // Keep all per-chunk arrays in lockstep.
            lock (_sync)
            {
                if (hasMesh)
                {
                    _vertChunks.Add(verts!.ToList());
                    _faceChunks.Add(faces!.Select(f => (f.V0, f.V1, f.V2)).ToList());
                    _surfaceIds.Add(surfaceId);
                }
                if (splines != null)
                    _splines.AddRange(splines);
            }
        }

        public void AddSplines(IReadOnlyList<IReadOnlyList<Vector3>> splines)
        {
            if (splines.Count == 0)
                return;
            lock (_sync)
                _splines.AddRange(splines);
        }

        public (List<Vector3> Verts, List<(int, int, int)> Faces, List<int>? SurfaceIds, IReadOnlyList<IReadOnlyList<Vector3>>? Splines) Build()
            => Build(out _, out _);

        public (List<Vector3> Verts, List<(int, int, int)> Faces, List<int>? SurfaceIds, IReadOnlyList<IReadOnlyList<Vector3>>? Splines) Build(
            out int preWeldVertexCount,
            out int postWeldVertexCount)
        {
            List<List<Vector3>> vertChunks;
            List<List<(int, int, int)>> faceChunks;
            List<int> surfaceIds;
            List<IReadOnlyList<Vector3>> splines;
            lock (_sync)
            {
                vertChunks = new List<List<Vector3>>(_vertChunks);
                faceChunks = new List<List<(int, int, int)>>(_faceChunks);
                surfaceIds = new List<int>(_surfaceIds);
                splines = new List<IReadOnlyList<Vector3>>(_splines);
            }

            if (vertChunks.Count != faceChunks.Count || vertChunks.Count != surfaceIds.Count)
            {
                throw new InvalidOperationException(
                    $"TileCollisionAccumulator chunk mismatch: verts={vertChunks.Count}, faces={faceChunks.Count}, surfaceIds={surfaceIds.Count}.");
            }

            var outVerts = new List<Vector3>();
            var outFaces = new List<(int, int, int)>();
            var outSurfaceIds = new List<int>();

            for (int i = 0; i < vertChunks.Count; i++)
            {
                var verts = vertChunks[i];
                var faces = faceChunks[i];
                int baseIdx = outVerts.Count;
                outVerts.AddRange(verts);
                foreach (var (v0, v1, v2) in faces)
                    outFaces.Add((v0 + baseIdx, v1 + baseIdx, v2 + baseIdx));
                for (int j = 0; j < faces.Count; j++)
                    outSurfaceIds.Add(surfaceIds[i]);
            }

            // IMPORTANT:
            // Tile splitting/clipping produces "triangle soup" where adjacent triangles often do not share vertex indices
            // (even when positions are identical). RW collision builds typically merge/weld vertices before computing
            // triangle neighbors/edge codes. Without welding, most edges become "unmatched" and edge-angle data becomes
            // useless, which can manifest as intermittent edge phasing.
            //
            // Weld vertices by position so shared edges are discoverable by index-based neighbor finding.
            // EDGEFLAG_ANGLEMASK: low angle byte = sharp; unmatched edges use extended cosine -1 → angle 0 (very sharp).
            // A fixed 1e-4 left many interior edges unmatched across GLB/chunk seams; scale epsilon slightly with mesh extent.
            preWeldVertexCount = outVerts.Count;
            float weldEpsilon = CollisionVertexWelder.ComputeAdaptiveEpsilon(outVerts);
            (outVerts, outFaces) = CollisionVertexWelder.WeldInPlace(outVerts, outFaces, weldEpsilon);
            postWeldVertexCount = outVerts.Count;

            return (outVerts, outFaces, outSurfaceIds, splines.Count > 0 ? splines : null);
        }
    }

    /// <summary>
    /// Merge this tile's welded collision with neighbor tiles' triangles overlapping an XZ expansion pad so Recast erosion
    /// and height sampling match across cSim tile seams (avoids thin unwalkable gaps / height pops at boundaries).
    /// </summary>
    private static (List<Vector3> Verts, List<(int A, int B, int C)> Faces) MergeCollisionForNavPowerSeams(
        WorldTileGrid.TileKey center,
        float expandedMinX,
        float expandedMaxX,
        float expandedMinZ,
        float expandedMaxZ,
        IReadOnlyDictionary<WorldTileGrid.TileKey, (List<Vector3> Verts, List<(int A, int B, int C)> Faces, List<int>? SurfaceIds, IReadOnlyList<IReadOnlyList<Vector3>>? Splines)> builtByTile)
    {
        var outVerts = new List<Vector3>();
        var outFaces = new List<(int A, int B, int C)>();

        void AppendWholeTile(WorldTileGrid.TileKey key)
        {
            if (!builtByTile.TryGetValue(key, out var t))
                return;
            int @base = outVerts.Count;
            outVerts.AddRange(t.Verts);
            foreach (var (fa, fb, fc) in t.Faces)
                outFaces.Add((fa + @base, fb + @base, fc + @base));
        }

        void AppendTileFacesInBounds(WorldTileGrid.TileKey key)
        {
            if (!builtByTile.TryGetValue(key, out var t))
                return;
            foreach (var (fa, fb, fc) in t.Faces)
            {
                var va = t.Verts[fa];
                var vb = t.Verts[fb];
                var vc = t.Verts[fc];
                if (!TriangleIntersectsBoundsXZ(va, vb, vc, expandedMinX, expandedMaxX, expandedMinZ, expandedMaxZ))
                    continue;
                int ia = outVerts.Count;
                outVerts.Add(va);
                int ib = outVerts.Count;
                outVerts.Add(vb);
                int ic = outVerts.Count;
                outVerts.Add(vc);
                outFaces.Add((ia, ib, ic));
            }
        }

        AppendWholeTile(center);
        AppendTileFacesInBounds(new WorldTileGrid.TileKey(center.U - 1, center.V - 1));
        AppendTileFacesInBounds(new WorldTileGrid.TileKey(center.U - 1, center.V));
        AppendTileFacesInBounds(new WorldTileGrid.TileKey(center.U - 1, center.V + 1));
        AppendTileFacesInBounds(new WorldTileGrid.TileKey(center.U, center.V - 1));
        AppendTileFacesInBounds(new WorldTileGrid.TileKey(center.U, center.V + 1));
        AppendTileFacesInBounds(new WorldTileGrid.TileKey(center.U + 1, center.V - 1));
        AppendTileFacesInBounds(new WorldTileGrid.TileKey(center.U + 1, center.V));
        AppendTileFacesInBounds(new WorldTileGrid.TileKey(center.U + 1, center.V + 1));

        return (outVerts, outFaces);
    }

    private static bool TriangleIntersectsBoundsXZ(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        float minX,
        float maxX,
        float minZ,
        float maxZ)
    {
        float tminX = MathF.Min(a.X, MathF.Min(b.X, c.X));
        float tmaxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
        float tminZ = MathF.Min(a.Z, MathF.Min(b.Z, c.Z));
        float tmaxZ = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
        return tmaxX >= minX && tminX <= maxX && tmaxZ >= minZ && tminZ <= maxZ;
    }
}
