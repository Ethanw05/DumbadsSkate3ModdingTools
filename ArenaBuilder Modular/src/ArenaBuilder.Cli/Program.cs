using ArenaBuilder.Cli.Commands;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help")
            {
                PrintHelp();
                return 0;
            }

            string cmd = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToArray();
            return cmd switch
            {
                "psg-info" => PsgInfoCommand.Run(rest),
                "psg-diff" => PsgDiffCommand.Run(rest),
                "psg-validate-cmesh" => PsgValidateClusteredMeshCommand.Run(rest),
                "psg-build" => PsgBuildCommand.Run(rest),
                "psg-build-batch" => PsgBuildBatchCommand.Run(rest),
                "psg-build-collision" => PsgBuildCollisionCommand.Run(rest),
                "psg-build-mesh" => PsgBuildMeshCommand.Run(rest),
                "psg-build-textures" => PsgBuildTexturesCommand.Run(rest),
                "psg-build-worldpainter" => PsgBuildWorldPainterCommand.Run(rest),
                "psg-build-aipath" => PsgBuildAiPathCommand.Run(rest),
                "psg-build-irradiance" => PsgBuildIrradianceCommand.Run(rest),
                "psg-analyze-stream" => PsgAnalyzeStreamCommand.Run(rest),
                _ => CliErrors.Fail($"Unknown command '{args[0]}'.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 2;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            ArenaBuilder.Cli

            Commands:
              psg-info <path>
                Prints PSG header and arena dictionary entries.

              psg-diff <pathA> <pathB>
                Compares two PSGs by arena dictionary objects (typeId/size/hash) and reports first mismatch per object.

              psg-validate-cmesh <path>
                Parses ClusteredMesh clusters and validates that all unit vertex indices are within [0, numVertices).

              psg-build <input.glb> [mesh_output.psg] [collision_output.psg] [--scale=1] [--force-uncompressed] [--texture-dir=<dir>] [--materials-json=<path>]
                Builds mesh, collision, and texture PSGs from a GLB.

              psg-build-batch <folder> [--tiles] [--global-only] [--cpres-only] [--proxy] [--flatten-all]
                Builds mesh, collision, and texture PSGs from all GLBs in a folder.
                --tiles: Use streaming tiles. Layout:
                         cPres_U_V_high  -> mesh PSGs + full-res textures unique to this tile.
                         cPres_Global    -> full-res textures shared by 2+ cPres tiles.
                         cSim_U_V_high   -> collision data.
                --global-only: No tiles; everything goes into cPres_Global (mesh + full-res textures) and cSim_Global.
                --cpres-only: Only mesh + textures (no collision). With --global-only, only cPres_Global is created.
                --proxy: Append _proxy to all folder names (e.g. cPres_50_50_high_proxy, cPres_Global_proxy). Use for proxy DISTs.
                --flatten-all: One PSG per GLB with multiple meshes (overflow split in same file).
                If materials JSON is provided (or sidecar <input>.json exists), it supplies material metadata and image_name hints; textures are resolved from the GLB.
                Default output folders: mesh -> <input_dir>\cPres_*_high (or cPres_Global), collision -> cSim_*_high (or cSim_Global).

              psg-build-collision <input.glb> [output.psg] [--force-uncompressed]
                Builds a collision PSG from a GLB using the collision pipeline (no JSON).
                If output.psg is omitted, writes to: <input_dir>\cSim_Global\<hash>.psg

              psg-build-mesh <input.glb> [output.psg] [--scale=1] [--texture-dir=<dir>] [--materials-json=<path>]
                Builds a mesh PSG and auto-builds linked texture PSGs from GLB textures.
                If materials JSON is provided (or sidecar <input>.json exists), it supplies material metadata and image_name hints; textures are resolved from the GLB.
                If output.psg is omitted, writes to: <input_dir>\cPres_Global\<hash>.psg
                Texture PSGs are written alongside the mesh in <input_dir>\cPres_Global as a single self-contained collection.

              psg-build-textures <input.{dds|png|jpg|jpeg}> [output.psg] [--guid=0xGUID] [--no-mips]
                Builds a PS3 texture PSG from DDS, or converts PNG/JPG to DDS DXT5 first. GUID from texture key (filename stem).
                If output.psg is omitted, writes to: <input_dir>\<guid>.psg

              psg-build-worldpainter [output.psg] [--guid=0xGUID64] [--value=0xU32]
                Builds a minimal WorldPainter PSG directly (no JSON/intermediate files).
                Default mode writes 6 hardcoded layer GUIDs (University-style seed set) with generated quadtrees.
                --guid writes a single layer PSG; --value overrides dictionary value (default: low dword of guid).

              psg-build-aipath <input.bin> [output-dir]
                Builds AIPath PSGs from an AIPNODE3 recording produced by AIPathRecorder/recorder.py.
                One .bin can contain many paths (the recorder splits on every in-game respawn);
                the builder fans them out per cSim_X_Y_high tile by world XZ bounding box (paths
                that overlap multiple tiles are duplicated into each).
                Default output dir: <input-dir>/aipath_psgs/cSim_X_Y_high/<hash>.psg per tile,
                using Lookup8 content-hash filenames (matches stock cSim tile naming).

              psg-build-irradiance <probes.json> [output-dir] [--salt=<text>]
                Builds IrradianceData (SH light probe) PSGs from a ProbeManifest JSON
                produced by skate3_irradiance_addon.py (Documentation/). Probes are bucketed
                per cPres_X_Y_high 100 m tile by world XZ; the engine blends edge probes
                across neighboring tiles at runtime (no duplication needed). Engine cap:
                1681 probes per tile (silently clamped) and 12 simultaneous active hulls.
                Default output dir: <input-dir>/irradiance_psgs/cPres_X_Y_high/<hash>.psg.

              psg-analyze-stream <stream-root-dir> [--out-dir=<path>] [--samples=N]
                Walks a stream root, parses every .psg in every cPres_* / cSim_* tile folder, and reports:
                  - per-stream-category PSG breakdown (mesh/texture/collision/...)
                  - texture-TOC GUID owner overlap across folders
                  - mesh RenderMaterialData channel GUIDs and where they resolve, with per-channel histogram
                  - sample mesh→texture resolution traces
                --out-dir writes psgs.csv, toc_entries.csv, channels.csv, channel_resolution.csv.
            """);
    }
}

