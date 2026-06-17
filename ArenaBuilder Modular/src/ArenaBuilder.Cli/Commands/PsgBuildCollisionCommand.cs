using ArenaBuilder.Collision;
using ArenaBuilder.Core;
using ArenaBuilder.Glb;

namespace ArenaBuilder.Cli.Commands;

internal static class PsgBuildCollisionCommand
{
    public static int Run(string[] args)
    {
        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (positional.Length is < 1 or > 2)
            return CliErrors.Fail("Usage: psg-build-collision <input.glb> [output.psg] [--force-uncompressed]");

        string glbPath = positional[0];
        string outPath = positional.Length == 2
            ? positional[1]
            : GetDefaultCollisionOutPath(glbPath);

        if (!File.Exists(glbPath)) return CliErrors.Fail($"Input GLB not found: {glbPath}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

        Console.WriteLine($"Loading GLB: {glbPath}");
        var flat = GlbMeshFlattener.Flatten(glbPath);

        var input = new CollisionInputFromGlb(flat.Vertices, flat.Faces, splines: null, surfaceId: 0)
        {
            InstanceDisplayName = Path.GetFileNameWithoutExtension(glbPath)
        };
        var builder = new CollisionPsgBuilder
        {
            ForceUncompressed = true,
            EnableVertexSmoothing = true,
            Granularity = 0.001f
        };

        using (var mem = new MemoryStream())
        {
            if (!builder.Build(input, mem))
            {
                Console.Error.WriteLine($"Collision build skipped: {glbPath} has no mesh geometry (spline-only or empty).");
                return 2;
            }
            using var fs = File.Create(outPath);
            mem.Position = 0;
            mem.CopyTo(fs);
        }

        Console.WriteLine($"Wrote PSG: {outPath}");
        return 0;
    }

    private static string GetDefaultCollisionOutPath(string glbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(glbPath)) ?? ".";
        var outDir = Path.Combine(dir, "cSim_Global");
        string glbStem = Path.GetFileNameWithoutExtension(glbPath);
        string name = Lookup8Hash.HashStringToHex(glbStem + "_collision") + ".psg";
        return Path.Combine(outDir, name);
    }
}

