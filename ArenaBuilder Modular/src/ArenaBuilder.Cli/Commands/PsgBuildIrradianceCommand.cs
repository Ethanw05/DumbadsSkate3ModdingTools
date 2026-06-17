using ArenaBuilder.Core;
using ArenaBuilder.Core.Platforms.Common.Pegasus.Irradiance;

using ArenaBuilder.Core.Platforms.PS3.Pegasus.Irradiance;

namespace ArenaBuilder.Cli.Commands;

/// <summary>
/// Builds IrradianceData PSGs from a ProbeManifest JSON produced by the Blender
/// add-on (<c>Documentation/skate3_irradiance_addon.py</c>). Probes are bucketed
/// per <c>cPres_X_Y_high</c> 100 m tile by world XZ; the engine blends edge
/// probes across neighboring tiles at runtime (no duplication).
///
/// Output: <c>&lt;out-dir&gt;/cPres_X_Y_high/&lt;hash&gt;.psg</c> per non-empty tile.
/// </summary>
internal static class PsgBuildIrradianceCommand
{
    public static int Run(string[] args)
    {
        string? inPath = null;
        string? outDir = null;
        string? guidSalt = null;
        foreach (var arg in args)
        {
            if      (arg.StartsWith("--in=",   StringComparison.Ordinal)) inPath   = arg[5..];
            else if (arg.StartsWith("--out=",  StringComparison.Ordinal)) outDir   = arg[6..];
            else if (arg.StartsWith("--salt=", StringComparison.Ordinal)) guidSalt = arg[7..];
            else if (inPath is null) inPath = arg;
            else if (outDir is null) outDir = arg;
        }

        if (string.IsNullOrWhiteSpace(inPath))
            return CliErrors.Fail(
                "Usage: psg-build-irradiance <probes.json> [output-dir] [--salt=<text>]\n" +
                "  <probes.json>  ProbeManifest emitted by skate3_irradiance_addon.py.\n" +
                "  output-dir     where the per-tile PSGs land. Default: <input-dir>/irradiance_psgs/\n" +
                "  --salt=<s>     mixed into the TOC GUID seed (use to force a fresh GUID on rebuild).");

        if (!File.Exists(inPath))
            return CliErrors.Fail($"Probe manifest not found: {inPath}");

        outDir ??= Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(inPath))!,
            "irradiance_psgs");

        ProbeManifest manifest;
        try
        {
            manifest = ProbeManifest.Load(inPath);
        }
        catch (Exception ex)
        {
            return CliErrors.Fail($"Failed to read probe manifest: {ex.Message}");
        }

        var probes = manifest.ToProbes();
        if (probes.Count == 0)
            return CliErrors.Fail("Probe manifest contained zero probes.");

        var buckets = IrradianceProbeBucketer.Bucket(probes);

        Console.WriteLine($"Loaded {probes.Count} probe(s) from {Path.GetFileName(inPath)}");
        Console.WriteLine($"Bucketed into {buckets.Count} cPres tile(s)");

        Directory.CreateDirectory(outDir);

        string stem = Path.GetFileNameWithoutExtension(inPath);
        int psgsWritten = 0;
        int downsampledTiles = 0;
        foreach (var kv in buckets.OrderBy(k => k.Key.X).ThenBy(k => k.Key.Y))
        {
            var tile = kv.Key;
            var indices = kv.Value;

            var rawProbes = new List<Probe>(indices.Count);
            for (int i = 0; i < indices.Count; i++) rawProbes.Add(probes[indices[i]]);

            IReadOnlyList<Probe> tileProbes = rawProbes;
            if (rawProbes.Count > IrradianceDataBuilder.MaxProbesPerAsset)
            {
                tileProbes = IrradianceProbeBucketer.DownsampleToEngineGrid(
                    rawProbes, tile, IrradianceDataBuilder.MaxProbesPerAsset);
                Console.WriteLine(
                    $"  {tile.FolderName}: {rawProbes.Count} probes → {tileProbes.Count} after 41×41 grid bin");
                downsampledTiles++;
            }

            string seed = $"irradiance_{stem}_{tile.FolderName}_n{tileProbes.Count}";
            if (!string.IsNullOrEmpty(guidSalt)) seed += "_" + guidSalt;
            ulong tocGuid = Lookup8Hash.HashString(seed);
            if (tocGuid == 0) tocGuid = 0x1000000000000001ul;

            byte[] psg;
            try
            {
                psg = IrradiancePsgBuilder.Build(tileProbes, tocGuid);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  {tile.FolderName}: build failed: {ex.Message}");
                continue;
            }

            string tileDir = Path.Combine(outDir, tile.FolderName);
            Directory.CreateDirectory(tileDir);
            string outPath = Path.Combine(tileDir, $"{tocGuid:X16}.psg");
            File.WriteAllBytes(outPath, psg);

            Console.WriteLine($"  {tile.FolderName}/{tocGuid:X16}.psg  " +
                              $"({tileProbes.Count} probe(s), {psg.Length} B)");
            psgsWritten++;
        }

        Console.WriteLine($"Wrote {psgsWritten} PSG(s) under {Path.GetFullPath(outDir)}");
        if (downsampledTiles > 0)
            Console.WriteLine($"{downsampledTiles} tile(s) auto-thinned to fit engine 41×41 grid.");
        return 0;
    }
}
