using ArenaBuilder.Core.Platforms.Common.PsgFormat;
using ArenaBuilder.Glb;
using System.Diagnostics;
using System.Text;

namespace ArenaBuilder.Build;

/// <summary>
/// After a tile build: copy DONOTREMOVE/cpres into cPres_Global (engine boilerplate), run Stream File Tool
/// to pack cPres / cSim tile folders, then write stream XML manifests for Pres and Sim.
/// </summary>
public static class DistPackRunner
{
    public const string StreamToolExeName = "Stream File Tool.exe";

    /// <summary>
    /// Returns true when <paramref name="folderName"/> belongs to a stream tile folder we copy/pack/clean
    /// (cPres_*, cSim_*).
    /// </summary>
    private static bool IsStreamTileFolderName(string folderName) =>
        folderName.StartsWith(TileBuildOptions.CPresPrefix, StringComparison.OrdinalIgnoreCase) ||
        folderName.StartsWith(TileBuildOptions.CSimPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copy build output (cPres_*, cSim_*, plus *_Global variants) from buildFolder to distRoot.
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
    /// DIST folder name with a "_Tex" suffix. The build emits no per-tile texture stream content,
    /// so the &lt;DIST&gt;_Tex.* manifest files from the template are placed verbatim — the engine
    /// still expects a Tex stream descriptor to exist even when it lists zero tiles. Skips any
    /// file already present.
    /// </summary>
    public static void CopyCtexIntoDistRoot(string donotRemoveDir, string distRoot, Action<string> log, ArenaPlatform platform = ArenaPlatform.Ps3)
    {
        // The Tex stream descriptor is platform-specific (PS3 .psf vs X360 .rx2/stream). PS3 reads
        // the canned stub from DONOTREMOVE/ctex; X360 reads it from DONOTREMOVE/ctex_xbox. Without a
        // matching-platform descriptor the engine waits forever on the missing Tex stream (infinite load).
        string subFolder = platform == ArenaPlatform.Xbox360 ? "ctex_xbox" : "ctex";
        string ctexSource = Path.Combine(donotRemoveDir, subFolder);
        if (!Directory.Exists(ctexSource))
        {
            if (platform == ArenaPlatform.Xbox360)
                log($"[DistPack] WARNING: DONOTREMOVE/{subFolder} not found ({ctexSource}). " +
                    "The X360 engine REQUIRES a <DIST>_Tex stream descriptor or the map infinite-loads. " +
                    "Create DONOTREMOVE/ctex_xbox with the X360 Tex stub (same role as the PS3 ctex stub).");
            else
                log($"[DistPack] DONOTREMOVE/{subFolder} not found: {ctexSource}");
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
                log($"[DistPack] Keeping existing: {dstName}");
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
        // cPres template PSGs are platform-specific (PS3 .psg vs X360 .rx2). PS3 reads them from
        // DONOTREMOVE/cpres; X360 from DONOTREMOVE/cpres_xbox. Picking the wrong folder would merge a
        // wrong-platform texture (e.g. the macro-overlay) into cPres_Global.
        ArenaPlatform platform = tileOptions?.TargetPlatform ?? ArenaPlatform.Ps3;
        string subFolder = platform == ArenaPlatform.Xbox360 ? "cpres_xbox" : "cpres";
        string cpresSource = Path.Combine(donotRemoveDir, subFolder);
        if (!Directory.Exists(cpresSource))
        {
            log($"[DistPack] DONOTREMOVE/{subFolder} not found: {cpresSource}");
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
    /// Run Stream File Tool.exe to pack PRES and SIM content under distRoot using the folder-based CLI:
    ///   Stream File Tool.exe pack --folder="DIST_..." --type=pres --platform=p
    ///   Stream File Tool.exe pack --folder="DIST_..." --type=sim  --platform=p
    /// </summary>
    public static void RunStreamPack(string distRoot, string streamToolExePath, Action<string> log, char platform = 'p', CancellationToken cancellationToken = default)
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

        PackStreamTypeIfPresent(distFull, streamToolExePath, toolDir, "Pres", TileBuildOptions.CPresPrefix, "pres", log, platform, cancellationToken);
        PackStreamTypeIfPresent(distFull, streamToolExePath, toolDir, "Sim",  TileBuildOptions.CSimPrefix,  "sim",  log, platform, cancellationToken);
    }

    /// <summary>
    /// Invoke Stream File Tool for a single stream type if any matching <paramref name="folderPrefix"/> folder
    /// or *.psf is present under <paramref name="distFull"/>. <paramref name="toolType"/> is the lowercase
    /// CLI token (pres / sim).
    /// </summary>
    private static void PackStreamTypeIfPresent(
        string distFull,
        string streamToolExePath,
        string toolDir,
        string label,
        string folderPrefix,
        string toolType,
        Action<string> log,
        char platform,
        CancellationToken cancellationToken)
    {
        bool hasPsf = Directory.EnumerateFiles(distFull, folderPrefix + "_*.psf").Any();
        bool hasFolder = Directory.EnumerateDirectories(distFull)
            .Any(d => Path.GetFileName(d).StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase));
        if (!hasPsf && !hasFolder)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        log($"[DistPack] Running Stream File Tool for {label} (folder-based)...");
        RunProcess(streamToolExePath, "pack", distFull, toolType, toolDir, log, platform, cancellationToken);
    }

    /// <summary>
    /// Delete all cPres* / cSim* directories under distRoot (after packing).
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
    /// Write stream XML for Pres and Sim tiles. Each StreamTile has a Center (cx, cy) and Tile entries
    /// for the 8 neighbors at ±tileSize. Centers are inferred from packed PSF filenames in
    /// <paramref name="distRoot"/> (<c>cPres_&lt;cx&gt;_&lt;cy&gt;_high.psf</c>; cPres_Global ignored).
    /// </summary>
    /// <param name="distRoot">DIST root containing cPres_* PSFs (before or after pack).</param>
    /// <param name="mapName">Map name for the output filename. Used directly as the basename:
    /// <c>&lt;mapName&gt;_Pres.xml</c>, <c>&lt;mapName&gt;_Sim.xml</c>.</param>
    /// <param name="tileOptions">Used for tile size/origin when inferring centers from folder names.</param>
    /// <param name="outputPath">Full path for the Pres XML file; if null, writes to distRoot. Sim XML
    /// is always written next to it as <c>&lt;map&gt;_Sim.xml</c>.</param>
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

        // Sim XML — the engine pairs Pres and Sim per-world-stream entry. Reuse the cPres centers
        // (the simulation stream covers the same world tiles).
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
    ///  - pack all cPres* in one Stream File Tool run, then all cSim*
    ///  - write stream XML
    ///  - optionally delete unpacked cPres*/cSim* folders
    /// </summary>
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
        char platform = tileOptions.TargetPlatform == ArenaPlatform.Xbox360 ? 'x' : 'p';
        RunStreamPack(distRoot, streamToolExePath, log, platform, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        WriteStreamXml(distRoot, mapName, tileOptions, null, log);
        if (deleteUnpackedAfterPack)
            DeleteUnpackedFolders(distRoot, log);
        cancellationToken.ThrowIfCancellationRequested();
        CopyCtexIntoDistRoot(donotRemoveDir, distRoot, log, tileOptions.TargetPlatform);
        log("[DistPack] Done.");
    }

    private static HashSet<(int cx, int cy)> CollectTileCenters(
        string distRoot,
        TileBuildOptions tileOptions,
        string folderPrefix)
    {
        var centers = new HashSet<(int, int)>();
        string prefix = folderPrefix + "_";
        string suffix = "_" + tileOptions.TileSuffix;
        string folderSuffix = tileOptions.FolderSuffix ?? "";

        if (!Directory.Exists(distRoot)) return centers;

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

    private static void RunProcess(string exePath, string verb, string folderPath, string type, string workingDir, Action<string> log, char platform, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var args = new List<string>
        {
            verb,
            $"--folder=\"{folderPath}\"",
            $"--type={type}",
            $"--platform={platform}",
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
        startInfo.ArgumentList.Add($"--platform={platform}");

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
