using ArenaBuilder.Core;
using ArenaBuilder.Core.Platforms.Common.Pegasus.AIPath;

using ArenaBuilder.Core.Platforms.PS3.Pegasus.AIPath;

namespace ArenaBuilder.Cli.Commands;

/// <summary>
/// Builds AIPath PSGs from an AIPNODE3 recording .bin produced by
/// AIPathRecorder/recorder.py. One .bin can contain many paths (each in-game
/// respawn during recording closes the current path and opens a new one); the
/// builder fans those paths out per cSim_X_Y_high tile based on their world XZ
/// bounding boxes (a path that overlaps multiple tiles is duplicated into each).
///
/// Output: <out-dir>/cSim_X_Y_high/&lt;hash&gt;.psg per tile, where &lt;hash&gt;
/// is the Lookup8 64-bit hash of a deterministic GUID seed (matches the
/// content-hash filename convention used by stock cSim tiles).
/// </summary>
internal static class PsgBuildAiPathCommand
{
    public static int Run(string[] args)
    {
        string? inPath = null;
        string? outDir = null;
        byte? widthLeft  = AiPathPsgBuilder.DefaultWidthClamp;
        byte? widthRight = AiPathPsgBuilder.DefaultWidthClamp;
        foreach (var arg in args)
        {
            if      (arg.StartsWith("--in=",  StringComparison.Ordinal)) inPath = arg[5..];
            else if (arg.StartsWith("--out=", StringComparison.Ordinal)) outDir = arg[6..];
            else if (arg.StartsWith("--width=",   StringComparison.Ordinal)) { var w = ParseWidth(arg[8..]);  widthLeft = w; widthRight = w; }
            else if (arg.StartsWith("--width-l=", StringComparison.Ordinal))   widthLeft  = ParseWidth(arg[10..]);
            else if (arg.StartsWith("--width-r=", StringComparison.Ordinal))   widthRight = ParseWidth(arg[10..]);
            else if (inPath is null) inPath = arg;
            else if (outDir is null) outDir = arg;
        }

        if (string.IsNullOrWhiteSpace(inPath))
            return CliErrors.Fail(
                "Usage: psg-build-aipath <input.bin> [output-dir] [--width=N | --width-l=N --width-r=N]\n" +
                "  <input.bin>   AIPNODE3 recording produced by AIPathRecorder/recorder.py\n" +
                "  output-dir    where the per-tile PSGs land. Default: <input-dir>/aipath_psgs/\n" +
                "  --width=N     half-width clamp per node side in units of 2 cm (0..255 or 'keep').\n" +
                $"                Default {AiPathPsgBuilder.DefaultWidthClamp} (20 cm half / 40 cm corridor). 'keep' = passthrough recorder values.\n" +
                "  --width-l=N   override left side only\n" +
                "  --width-r=N   override right side only");

        if (!File.Exists(inPath))
            return CliErrors.Fail($"Input .bin not found: {inPath}");

        outDir ??= Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(inPath))!,
            "aipath_psgs");

        AiPathBinFile.File bin;
        try
        {
            bin = AiPathBinFile.Read(inPath);
        }
        catch (Exception ex)
        {
            return CliErrors.Fail($"Failed to read .bin: {ex.Message}");
        }

        if (bin.Paths.Count == 0)
            return CliErrors.Fail("Recording contained zero paths.");

        // Compute per-path bbox so we can bucket into tiles.
        var bboxes = new AiPathTileBucketer.Bbox[bin.Paths.Count];
        for (int i = 0; i < bin.Paths.Count; i++)
        {
            var nodes = bin.Paths[i].Nodes;
            bboxes[i] = AiPathTileBucketer.Bbox.FromPositions(
                Enumerable.Range(0, nodes.Count).Select(n => AiPathBinFile.ReadPos(nodes[n])));
        }

        var buckets = AiPathTileBucketer.Bucket(bboxes);

        Console.WriteLine($"Loaded {bin.Paths.Count} path(s) from {Path.GetFileName(inPath)}");
        Console.WriteLine($"  name_stem='{bin.NameStem}'  allowed=0x{bin.AllowedSkaters:X16}  " +
                          $"skill={bin.SkillLevel}  loop={(bin.IsLoop ? 1 : 0)}");
        string FmtW(byte? w) => w is byte b ? $"{b} (≈ {b * 0.02f:F2} m)" : "keep recorder";
        Console.WriteLine($"  width-clamp L={FmtW(widthLeft)}  R={FmtW(widthRight)}");
        Console.WriteLine($"Bucketed into {buckets.Count} tile(s)");

        Directory.CreateDirectory(outDir);

        int psgsWritten = 0;
        foreach (var kv in buckets.OrderBy(k => k.Key.X).ThenBy(k => k.Key.Y))
        {
            var tile = kv.Key;
            var indices = kv.Value;

            // Subset the input file down to the paths in this tile.
            var subsetPaths = indices
                .Select(i => bin.Paths[i])
                .ToList()
                .AsReadOnly();
            var subsetBin = new AiPathBinFile.File(
                bin.AllowedSkaters, bin.SkillLevel, bin.IsLoop, bin.NameStem, subsetPaths);

            string tocSalt = $"{Path.GetFileNameWithoutExtension(inPath)}_{tile.FolderName}";
            byte[] psg = AiPathPsgBuilder.BuildFromBin(
                subsetBin,
                tocGuidSalt: tocSalt,
                widthClampLeft: widthLeft,
                widthClampRight: widthRight);

            // Content-hash filename per stock convention. We include the tile key in the
            // seed so each tile's PSG gets a distinct GUID even when its path subsets
            // overlap with another tile.
            string seed = $"aipath_{bin.NameStem}_{tile.FolderName}_n{indices.Count}";
            ulong hash = Lookup8Hash.HashString(seed);
            string tileDir = Path.Combine(outDir, tile.FolderName);
            Directory.CreateDirectory(tileDir);
            string outPath = Path.Combine(tileDir, $"{hash:X16}.psg");
            File.WriteAllBytes(outPath, psg);

            Console.WriteLine($"  {tile.FolderName}/{hash:X16}.psg  " +
                              $"({indices.Count} path(s), {psg.Length} B)");
            psgsWritten++;
        }

        Console.WriteLine($"Wrote {psgsWritten} PSG(s) under {Path.GetFullPath(outDir)}");
        return 0;
    }

    // Parses --width=N. "keep" / "passthrough" / "none" → null (honor recorder
    // bytes verbatim). Numeric → 0..255 byte clamp value (units of 2 cm).
    private static byte? ParseWidth(string s)
    {
        s = s.Trim();
        if (s.Equals("keep",        StringComparison.OrdinalIgnoreCase) ||
            s.Equals("passthrough", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("none",        StringComparison.OrdinalIgnoreCase))
            return null;
        if (!byte.TryParse(s, out byte b))
            throw new ArgumentException($"--width value must be 0..255 or 'keep' (got '{s}')");
        return b;
    }
}
