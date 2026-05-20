using ArenaBuilder.Glb;
using System.Diagnostics;
using System.Text;

namespace ArenaBuilder.Build;

/// <summary>
/// After a tile build: copy DONOTREMOVE/cpres into cPres_Global (engine boilerplate), run Stream File Tool
/// to pack cPres / cSim / cTex tile folders, then write stream XML manifests for Pres and Tex.
///
/// <para>
/// Texture layout (dual-tier, see <see cref="TileBuildPipeline"/> docs):
/// </para>
/// <list type="bullet">
///   <item>cPres_U_V_high — mesh PSGs + a SMALL 16×16 fallback copy of every texture, keyed by logical GUID.</item>
///   <item>cTex_X_Y_high — full-resolution copy of every texture whose using-geometry overlaps the cTex
///   tile's footprint, keyed by the SAME logical GUID. Engine streams it on top of the cPres fallback.</item>
/// </list>
///
/// <para>
/// The Tex pack call processes whatever cTex_*_high folders the build emitted. Optionally deletes
/// unpacked folders after pack.
/// </para>
/// </summary>
public static class DistPackRunner
{
    public const string StreamToolExeName = "Stream File Tool.exe";

    /// <summary>
    /// Returns true when <paramref name="folderName"/> belongs to a stream tile folder we copy/pack/clean
    /// (cPres_*, cSim_*, cTex_*). Matches the three engine stream types exposed via
    /// <c>AssetPaths::tStreamType</c> (kStreamType_Pres / kStreamType_Sim / kStreamType_Texture).
    /// </summary>
    private static bool IsStreamTileFolderName(string folderName) =>
        folderName.StartsWith(TileBuildOptions.CPresPrefix, StringComparison.OrdinalIgnoreCase) ||
        folderName.StartsWith(TileBuildOptions.CSimPrefix, StringComparison.OrdinalIgnoreCase) ||
        folderName.StartsWith(TileBuildOptions.CTexPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copy build output (cPres_*, cSim_*, cTex_*, plus *_Global variants) from buildFolder to distRoot.
    /// Does not overwrite existing files in distRoot (overwrite: false); merges so DIST can be incrementally updated.
    /// </summary>
    public static void CopyBuildOutputToDist(string buildFolder, string distRoot, Action<string> log)
    {
        string src = Path.GetFullPath(buildFolder);
        string dst = Path.GetFullPath(distRoot);
        if (!Directory.Exists(src))
        {
            log($"[DistPack] Build folder not found: {src}");
            return;
        }
        Directory.CreateDirectory(dst);

        foreach (var entry in Directory.EnumerateDirectories(src))
        {
            string name = Path.GetFileName(entry);
            if (!IsStreamTileFolderName(name))
                continue;
            string targetDir = Path.Combine(dst, name);
            CopyDirectory(entry, targetDir, log, overwrite: false);
            log($"[DistPack] Copied {name} -> DIST");
        }
    }

    /// <summary>
    /// Copy template files from DONOTREMOVE/ctex into the DIST root, renaming them to match the
    /// DIST folder name with a "_Tex" suffix. With the dual-tier scheme the build now produces real
    /// per-tile cTex output, so this method only fills in any <c>&lt;DIST&gt;_Tex.*</c> file that was
    /// NOT produced by <see cref="RunStreamPack"/> (it skips when the file already exists).
    ///
    /// Critical: must NOT overwrite the freshly packed cTex output — the engine drives texture
    /// streaming off the world-level <c>&lt;world&gt;_Tex.xsf</c> manifest
    /// (<c>cGameAssetManager::UpdateGameStreamFileLoaded</c>, 0x82400d30); clobbering it with a
    /// static template would un-do all the per-tile streaming work.
    /// </summary>
    public static void CopyCtexIntoDistRoot(string donotRemoveDir, string distRoot, Action<string> log)
    {
        string ctexSource = Path.Combine(donotRemoveDir, "ctex");
        if (!Directory.Exists(ctexSource))
        {
            log($"[DistPack] DONOTREMOVE/ctex not found: {ctexSource}");
            return;
        }

        string distFull = Path.GetFullPath(distRoot);
        Directory.CreateDirectory(distFull);

        string distFolderName = Path.GetFileName(distFull);
        if (string.IsNullOrEmpty(distFolderName))
        {
            log("[DistPack] Could not determine DIST folder name for CTEX copy.");
            return;
        }

        string baseName = distFolderName + "_Tex";

        foreach (var srcFile in Directory.EnumerateFiles(ctexSource))
        {
            string ext = Path.GetExtension(srcFile);
            string dstName = baseName + ext;
            string dstPath = Path.Combine(distFull, dstName);
            if (File.Exists(dstPath))
            {
                log($"[DistPack] Keeping packed CTEX (skipping DONOTREMOVE template): {dstName}");
                continue;
            }
            try
            {
                File.Copy(srcFile, dstPath, overwrite: false);
                log($"[DistPack] Copied CTEX template -> {dstName}");
            }
            catch (Exception ex)
            {
                log($"[DistPack] Failed to copy CTEX file {Path.GetFileName(srcFile)}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Copy all files from DONOTREMOVE/cpres into distRoot/cPres_Global (or cPres_Global{suffix} when tileOptions.FolderSuffix is set).
    /// Keeps existing build output; adds template PSGs from DONOTREMOVE.
    /// </summary>
    /// <param name="donotRemoveDir">Path to the DONOTREMOVE folder (contains a 'cpres' subfolder).</param>
    /// <param name="tileOptions">Optional; when FolderSuffix is set, merge into cPres_Global{FolderSuffix}.</param>
    public static void MergeDonotRemoveIntoCpresGlobal(string donotRemoveDir, string distRoot, Action<string> log, TileBuildOptions? tileOptions = null)
    {
        string cpresSource = Path.Combine(donotRemoveDir, "cpres");
        if (!Directory.Exists(cpresSource))
        {
            log($"[DistPack] DONOTREMOVE/cpres not found: {cpresSource}");
            return;
        }
        string folderSuffix = tileOptions?.FolderSuffix ?? "";
        string cpresGlobal = Path.Combine(distRoot, TileBuildOptions.CPresGlobalFolder + folderSuffix);
        Directory.CreateDirectory(cpresGlobal);

        foreach (var file in Directory.EnumerateFiles(cpresSource))
        {
            string fileName = Path.GetFileName(file);
            string targetPath = Path.Combine(cpresGlobal, fileName);
            try
            {
                File.Copy(file, targetPath, overwrite: false);
                log($"[DistPack] Added to cPres_Global: {fileName}");
            }
            catch (IOException ex) when (ex.Message.Contains("already exists"))
            {
                log($"[DistPack] Skipped (already in cPres_Global): {fileName}");
            }
            catch (Exception ex)
            {
                log($"[DistPack] Failed to copy {fileName}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Run Stream File Tool.exe to pack PRES, SIM and TEX content under distRoot using the folder-based CLI:
    ///   Stream File Tool.exe pack --folder="DIST_..." --type=pres --platform=p
    ///   Stream File Tool.exe pack --folder="DIST_..." --type=sim  --platform=p
    ///   Stream File Tool.exe pack --folder="DIST_..." --type=tex  --platform=p
    /// The three tokens mirror <c>AssetPaths::tStreamType</c> (kStreamType_Pres / kStreamType_Sim / kStreamType_Texture)
    /// in the Skate 2 binary; the suffix table at 0x82d5cad8 maps them to "Pres" / "Sim" / "Tex".
    /// </summary>
    public static void RunStreamPack(string distRoot, string streamToolExePath, Action<string> log, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(streamToolExePath))
        {
            log($"[DistPack] Stream File Tool not found: {streamToolExePath}");
            return;
        }
        string distFull = Path.GetFullPath(distRoot);
        if (!Directory.Exists(distFull))
        {
            log($"[DistPack] DIST root not found: {distFull}");
            return;
        }

        string toolDir = Path.GetDirectoryName(streamToolExePath) ?? distFull;

        PackStreamTypeIfPresent(distFull, streamToolExePath, toolDir, "Pres", TileBuildOptions.CPresPrefix, "pres", log, cancellationToken);
        PackStreamTypeIfPresent(distFull, streamToolExePath, toolDir, "Sim",  TileBuildOptions.CSimPrefix,  "sim",  log, cancellationToken);
        PackStreamTypeIfPresent(distFull, streamToolExePath, toolDir, "Tex",  TileBuildOptions.CTexPrefix,  "tex",  log, cancellationToken);
    }

    /// <summary>
    /// Invoke Stream File Tool for a single stream type if any matching <paramref name="folderPrefix"/> folder
    /// or *.psf is present under <paramref name="distFull"/>. <paramref name="toolType"/> is the lowercase
    /// CLI token (pres / sim / tex).
    /// </summary>
    private static void PackStreamTypeIfPresent(
        string distFull,
        string streamToolExePath,
        string toolDir,
        string label,
        string folderPrefix,
        string toolType,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        bool hasPsf = Directory.EnumerateFiles(distFull, folderPrefix + "_*.psf").Any();
        bool hasFolder = Directory.EnumerateDirectories(distFull)
            .Any(d => Path.GetFileName(d).StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase));
        if (!hasPsf && !hasFolder)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        log($"[DistPack] Running Stream File Tool for {label} (folder-based)...");
        RunProcess(streamToolExePath, "pack", distFull, toolType, toolDir, log, cancellationToken);
    }

    /// <summary>
    /// Delete all cPres* / cSim* / cTex* directories under distRoot (after packing).
    /// </summary>
    public static void DeleteUnpackedFolders(string distRoot, Action<string> log)
    {
        string distFull = Path.GetFullPath(distRoot);
        if (!Directory.Exists(distFull)) return;

        foreach (var entry in Directory.EnumerateDirectories(distFull).ToList())
        {
            string name = Path.GetFileName(entry);
            if (!IsStreamTileFolderName(name))
                continue;
            try
            {
                Directory.Delete(entry, recursive: true);
                log($"[DistPack] Deleted {name}");
            }
            catch (Exception ex)
            {
                log($"[DistPack] Failed to delete {name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Write stream XML for Pres and Tex tiles. Each StreamTile has a Center (cx, cy) and Tile entries
    /// for the 8 neighbors at ±tileSize. Centers are inferred from packed PSF filenames in
    /// <paramref name="distRoot"/>:
    /// <list type="bullet">
    ///   <item><c>cPres_&lt;cx&gt;_&lt;cy&gt;_high.psf</c> -> Pres centers (cPres_Global ignored).</item>
    ///   <item><c>cTex_&lt;cx&gt;_&lt;cy&gt;_high.psf</c> -> Tex centers.</item>
    /// </list>
    /// Output XMLs match the format the engine consumes via <c>cAssetStreamSystem::ParseXmlStreamTile</c>
    /// (0x824031a0): each StreamTile drives a 3×3 (center + 8 neighbor) load region for its stream type.
    /// </summary>
    /// <param name="distRoot">DIST root containing cPres_* / cTex_* PSFs (before or after pack).</param>
    /// <param name="mapName">Map name for the output filename. Used directly as the basename:
    /// <c>&lt;mapName&gt;_Pres.xml</c>, <c>&lt;mapName&gt;_Sim.xml</c>, <c>&lt;mapName&gt;_Tex.xml</c>.
    /// The engine reads <c>data/stream/&lt;WorldStream&gt;_Pres.xml</c> + <c>_Sim.xml</c> at FE-time
    /// to discover world content (including locator PSGs) — pass <c>WorldStream</c> as <paramref name="mapName"/>
    /// so this matches. The legacy <c>dist_</c> prefix was wrong: stock and DW ship the bare
    /// <c>&lt;WorldStream&gt;_*.xml</c> form.</param>
    /// <param name="tileOptions">Used for tile size/origin when inferring centers from folder names.</param>
    /// <param name="outputPath">Full path for the Pres XML file; if null, writes to distRoot. Sim and Tex
    /// XMLs are always written next to it as <c>&lt;map&gt;_Sim.xml</c> / <c>&lt;map&gt;_Tex.xml</c>.</param>
    public static void WriteStreamXml(
        string distRoot,
        string mapName,
        TileBuildOptions tileOptions,
        string? outputPath,
        Action<string> log)
    {
        WriteStreamTilesXml(
            distRoot,
            mapName,
            tileOptions,
            TileBuildOptions.CPresPrefix,
            "Pres",
            outputPath,
            log);

        // Sim XML — the engine pairs Pres and Sim per-world-stream entry. Stock and DW both ship
        // _Sim alongside _Pres in `data/stream/`. We don't have a dedicated cSim_* tile prefix in
        // TileBuildOptions, but the engine accepts a Sim XML that mirrors the Pres tile-center layout
        // (the simulation stream covers the same world tiles); reuse the cPres centers for both.
        string? simOutputPath = outputPath == null
            ? null
            : Path.Combine(
                Path.GetDirectoryName(outputPath) ?? distRoot,
                $"{mapName}_Sim.xml");
        WriteStreamTilesXml(
            distRoot,
            mapName,
            tileOptions,
            TileBuildOptions.CPresPrefix,
            "Sim",
            simOutputPath,
            log);

        // Tex XML always lands next to the Pres XML (same directory). The cTex grid spacing matches
        // tileOptions.TileSize — cTex tiles are at the half-offset of cPres tiles but use the same
        // step between adjacent tiles, so the same ±tileSize neighbor logic applies.
        string? texOutputPath = outputPath == null
            ? null
            : Path.Combine(
                Path.GetDirectoryName(outputPath) ?? distRoot,
                $"{mapName}_Tex.xml");
        WriteStreamTilesXml(
            distRoot,
            mapName,
            tileOptions,
            TileBuildOptions.CTexPrefix,
            "Tex",
            texOutputPath,
            log);
    }

    private static void WriteStreamTilesXml(
        string distRoot,
        string mapName,
        TileBuildOptions tileOptions,
        string folderPrefix,
        string xmlLabel,
        string? outputPath,
        Action<string> log)
    {
        var centers = CollectTileCenters(distRoot, tileOptions, folderPrefix);
        if (centers.Count == 0)
        {
            log($"[DistPack] No {folderPrefix} tile folders found; skipping {xmlLabel} stream XML.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"us-ascii\"?>");
        sb.AppendLine("<StreamTiles>");

        int offsetStep = (int)tileOptions.TileSize;
        foreach (var (cx, cy) in centers.OrderBy(c => c.cx).ThenBy(c => c.cy))
        {
            sb.AppendLine("  <StreamTile>");
            sb.AppendLine($"    <Center>{cx}, {cy}</Center>");
            for (int dx = -offsetStep; dx <= offsetStep; dx += offsetStep)
            {
                for (int dy = -offsetStep; dy <= offsetStep; dy += offsetStep)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = cx + dx, ny = cy + dy;
                    if (centers.Contains((nx, ny)))
                        sb.AppendLine($"    <Tile>{nx}, {ny}</Tile>");
                }
            }
            sb.AppendLine("  </StreamTile>");
        }

        sb.AppendLine("</StreamTiles>");

        // Default filename pattern: `<mapName>_<xmlLabel>.xml` (no `dist_` prefix). The previous
        // `dist_<mapName>_<xmlLabel>.xml` shape produced files the engine ignored — stock and DW
        // ship the bare `<WorldStream>_Pres.xml`/`_Sim.xml`/`_Tex.xml` form in `data/stream/`,
        // and that is what `cAssetStreamSystem::ParseXmlStreamTile` looks up.
        string path = outputPath ?? Path.Combine(distRoot, $"{mapName}_{xmlLabel}.xml");
        string dir = Path.GetDirectoryName(path)!;
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        log($"[DistPack] Wrote {xmlLabel} stream XML: {path}");
    }

    /// <summary>
    /// Run the full sequence:
    ///  - if buildFolder != distRoot, copies build output from buildFolder to distRoot first (merge, no overwrite).
    ///  - merge DONOTREMOVE/cpres into cPres_Global
    ///  - pack all cPres* in one Stream File Tool run, then all cSim*, then all cTex*
    ///  - write stream XML
    ///  - optionally delete unpacked cPres*/cSim*/cTex* folders
    ///  - finally fill in DONOTREMOVE/ctex template files for any &lt;DIST&gt;_Tex.* output that the pack step
    ///    didn't already produce (does not overwrite the freshly packed cTex output).
    /// </summary>
    /// <param name="buildFolder">Folder that already contains build output (cPres_*, cSim_*, cTex_*, plus optional *_Global variants). When different from distRoot, output is copied into distRoot first.</param>
    /// <param name="distRoot">Full path to DIST_MapName (will be created).</param>
    /// <param name="donotRemoveDir">Path to DONOTREMOVE (contains cpres subfolder and optionally Stream File Tool.exe).</param>
    /// <param name="streamToolExePath">Full path to Stream File Tool.exe (often DONOTREMOVE\Stream File Tool.exe).</param>
    /// <param name="mapName">Map name for XML filename.</param>
    /// <param name="tileOptions">Tile options for XML center logic.</param>
    /// <param name="deleteUnpackedAfterPack">If true, remove cPres*/cSim* folders after packing.</param>
    /// <param name="cancellationToken">Optional cancellation; checked before copy and before each pack step.</param>
    public static void Run(
        string buildFolder,
        string distRoot,
        string donotRemoveDir,
        string streamToolExePath,
        string mapName,
        TileBuildOptions tileOptions,
        bool deleteUnpackedAfterPack,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        log("[DistPack] Pack phase: merging engine templates, then Stream File Tool (very large maps can take 10+ minutes)…");

        if (!string.Equals(Path.GetFullPath(buildFolder), Path.GetFullPath(distRoot), StringComparison.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyBuildOutputToDist(buildFolder, distRoot, log);
        }

        cancellationToken.ThrowIfCancellationRequested();
        MergeDonotRemoveIntoCpresGlobal(donotRemoveDir, distRoot, log, tileOptions);
        RunStreamPack(distRoot, streamToolExePath, log, cancellationToken);      // packs all Pres in one run, then all Sim
        cancellationToken.ThrowIfCancellationRequested();
        WriteStreamXml(distRoot, mapName, tileOptions, null, log);
        if (deleteUnpackedAfterPack)
            DeleteUnpackedFolders(distRoot, log);
        cancellationToken.ThrowIfCancellationRequested();
        // CTEX root files are copied last and never packed.
        CopyCtexIntoDistRoot(donotRemoveDir, distRoot, log);
        log("[DistPack] Done.");
    }

    private static HashSet<(int cx, int cy)> CollectTileCenters(
        string distRoot,
        TileBuildOptions tileOptions,
        string folderPrefix)
    {
        var centers = new HashSet<(int, int)>();
        string prefix = folderPrefix + "_";                          // e.g. "cPres_" / "cTex_"
        string suffix = "_" + tileOptions.TileSuffix;                // "_high"
        string folderSuffix = tileOptions.FolderSuffix ?? "";        // e.g. "_proxy"

        if (!Directory.Exists(distRoot)) return centers;

        // PSF naming: <prefix>_<cx>_<cy>_high[<folderSuffix>].psf
        foreach (var file in Directory.EnumerateFiles(distRoot, "*.psf"))
        {
            string fileName = Path.GetFileName(file);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

            if (!nameWithoutExt.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string nameForSuffixCheck = nameWithoutExt;
            if (!string.IsNullOrEmpty(folderSuffix) && nameWithoutExt.EndsWith(folderSuffix, StringComparison.OrdinalIgnoreCase))
                nameForSuffixCheck = nameWithoutExt.Substring(0, nameWithoutExt.Length - folderSuffix.Length);
            if (!nameForSuffixCheck.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            // Exclude *_Global[._proxy].psf for both cPres (TileBuildOptions.CPresGlobalFolder) and any
            // hypothetical cTex global. Currently only cPres has a global variant in TileBuildOptions.
            if (string.Equals(folderPrefix, TileBuildOptions.CPresPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string globalName = TileBuildOptions.CPresGlobalFolder + folderSuffix;
                if (nameWithoutExt.Equals(globalName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (nameWithoutExt.Equals(TileBuildOptions.CPresGlobalFolder, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            var parts = nameForSuffixCheck.Split('_');
            if (parts.Length < 4) continue;

            if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int cx))
                continue;
            if (!int.TryParse(parts[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int cy))
                continue;

            centers.Add((cx, cy));
        }
        return centers;
    }

    private static void RunProcess(string exePath, string verb, string folderPath, string type, string workingDir, Action<string> log, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Example:
        //   Stream File Tool.exe pack --folder="path\to\DIST" --type=pres --platform=p
        var args = new List<string>
        {
            verb,
            $"--folder=\"{folderPath}\"",
            $"--type={type}",
            "--platform=p",
        };
        string argsLine = string.Join(" ", args);
        log($"[DistPack] {Path.GetFileName(exePath)} {argsLine}");

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(verb);
        startInfo.ArgumentList.Add($"--folder={folderPath}");
        startInfo.ArgumentList.Add($"--type={type}");
        startInfo.ArgumentList.Add("--platform=p");

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                log("[DistPack] Failed to start Stream File Tool.");
                return;
            }
            string stdout = "";
            string stderr = "";
            var readOut = Task.Run(() => process.StandardOutput.ReadToEnd());
            var readErr = Task.Run(() => process.StandardError.ReadToEnd());
            // Large DISTs (10k+ PSGs across hundreds of cPres/cSim/cTex folders) can take many minutes
            // to pack; 120s was too short and killed the tool after "Done" while the user thought
            // packing had never started.
            const int packTimeoutMs = 3_600_000; // 60 minutes
            bool exited = process.WaitForExit(packTimeoutMs);
            if (!exited)
            {
                process.Kill(entireProcessTree: false);
                log($"[DistPack] Stream File Tool timed out after {packTimeoutMs / 60_000} min and was killed.");
            }
            stdout = readOut.GetAwaiter().GetResult();
            stderr = readErr.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                log($"[DistPack] Stream File Tool exited with code {process.ExitCode} (0x{(uint)process.ExitCode:X8}).");
                log("[DistPack] Codes like 0xC0000005 (access violation) or 0xC0xxxxxx often mean: crash, missing DLL, or wrong working dir. Run the tool manually from a command prompt with the same --folder to see its error.");
                if (!string.IsNullOrWhiteSpace(stderr))
                    log($"[DistPack] stderr: {stderr.Trim()}");
                if (!string.IsNullOrWhiteSpace(stdout))
                    log($"[DistPack] stdout: {stdout.Trim()}");
            }
        }
        catch (Exception ex)
        {
            log($"[DistPack] Stream File Tool error: {ex.Message}");
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir, Action<string> log, bool overwrite = false)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            string fileName = Path.GetFileName(file);
            string dest = Path.Combine(targetDir, fileName);
            File.Copy(file, dest, overwrite);
        }
        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
        {
            string name = Path.GetFileName(subDir);
            CopyDirectory(subDir, Path.Combine(targetDir, name), log, overwrite);
        }
    }
}
