using ArenaBuilder.Build;
using ArenaBuilder.Core.Platforms.Common.PsgFormat;
using ArenaBuilder.Glb;
using ArenaBuilder.NavPower;

namespace ArenaBuilder.Cli.Commands;

/// <summary>
/// Batch build with optional streaming tiles. Processes all GLBs in a folder.
/// </summary>
internal static class PsgBuildBatchCommand
{
    public static int Run(string[] args)
    {
        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var flags = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToArray();

        if (positional.Length < 1)
            return CliErrors.Fail("Usage: psg-build-batch <folder> [--tiles] [--global-only] [--cpres-only] [--proxy] [--flatten-all] [--emit-navpower] [--dump-navobj=<dir>]");

        string folder = Path.GetFullPath(positional[0]);
        if (!Directory.Exists(folder))
            return CliErrors.Fail($"Folder not found: {folder}");

        var glbs = Directory.GetFiles(folder, "*.glb", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // .bin files (AIPNODE3 recordings) are picked up by TileBuildPipeline
        // alongside the GLB pass. Still run the build when only .bin files are
        // present so a recordings-only folder doesn't short-circuit.
        var bins = Directory.GetFiles(folder, "*.bin", SearchOption.TopDirectoryOnly);
        if (glbs.Length == 0 && bins.Length == 0)
        {
            Console.WriteLine("No .glb or .bin files found in the selected folder.");
            return 0;
        }
        if (glbs.Length == 0)
            Console.WriteLine($"No .glb files, but found {bins.Length} .bin file(s) -- running AIPath-only pass.");

        bool globalOnly = flags.Any(f => string.Equals(f, "--global-only", StringComparison.OrdinalIgnoreCase));
        bool cpresOnly = flags.Any(f => string.Equals(f, "--cpres-only", StringComparison.OrdinalIgnoreCase));
        bool proxy = flags.Any(f => string.Equals(f, "--proxy", StringComparison.OrdinalIgnoreCase));
        bool tiles = flags.Any(f => string.Equals(f, "--tiles", StringComparison.OrdinalIgnoreCase));
        bool emitNavPower = flags.Any(f => string.Equals(f, "--emit-navpower", StringComparison.OrdinalIgnoreCase));

        // Platform: PS3 (.psg, default) or Xbox 360 (.rx2). --platform=xbox|ps3|360 or --xbox.
        var platformFlag = flags.FirstOrDefault(f => f.StartsWith("--platform=", StringComparison.OrdinalIgnoreCase));
        string? platformVal = platformFlag?["--platform=".Length..];
        bool xbox = flags.Any(f => string.Equals(f, "--xbox", StringComparison.OrdinalIgnoreCase))
                    || (platformVal != null && (platformVal.Equals("xbox", StringComparison.OrdinalIgnoreCase)
                                                || platformVal.Equals("xbox360", StringComparison.OrdinalIgnoreCase)
                                                || platformVal.Equals("360", StringComparison.OrdinalIgnoreCase)));
        ArenaPlatform targetPlatform = xbox ? ArenaPlatform.Xbox360 : ArenaPlatform.Ps3;

        string? dumpNavObjDir = null;
        var dumpNavObjFlag = flags.FirstOrDefault(f => f.StartsWith("--dump-navobj=", StringComparison.OrdinalIgnoreCase));
        if (dumpNavObjFlag != null)
            dumpNavObjDir = Path.GetFullPath(dumpNavObjFlag["--dump-navobj=".Length..]);

        var options = new TileBuildOptions
        {
            GlobalOnly = globalOnly,
            CPresOnly = cpresOnly,
            FolderSuffix = proxy ? "_proxy" : "",
            EmitNavPower = emitNavPower,
            TargetPlatform = targetPlatform,
            NavPower = new NavPowerBuildOptions
            {
                DumpObjDir = dumpNavObjDir,
            },
        };
        if (tiles && globalOnly)
            Console.WriteLine("[Note] --tiles is ignored when --global-only is set.");

        const float meshScale = 1f;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        try
        {
            TileBuildPipeline.Build(folder, glbs, options, meshScale, Console.WriteLine, cts.Token);
            // Match prior ReleaseAfterBuildPhase behavior: compact LOH after build (TileBuildPipeline
            // only calls ReleaseBuildWorkingSet so WinForms can reach DIST pack without a long GC).
            BuildMemory.TryCompactManagedHeap();
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Build cancelled.");
            return 130; // common exit code for SIGINT
        }
        return 0;
    }
}
