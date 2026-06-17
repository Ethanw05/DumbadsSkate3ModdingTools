using ArenaBuilder.Core;
using ArenaBuilder.Core.Platforms.Common.Pegasus.Mesh;
using ArenaBuilder.Core.Psg;
using ArenaBuilder.Glb;
using ArenaBuilder.Mesh;
using ArenaBuilder.Texture;

using ArenaBuilder.Core.Platforms.Common.PsgFormat;

namespace ArenaBuilder.Cli.Commands;

/// <summary>Build mesh PSG from GLB. Matches glbtopsg: first mesh, object-space vertices, tangent formula, dec3n (round+mask), face order (i0,i1,i2). We use float positions; default scale=1 (glbtopsg uses 256 for int16 layout).</summary>
internal static class PsgBuildMeshCommand
{
    public static int Run(string[] args)
    {
        float scale = 1f;  // glbtopsg uses 256 for int16 XYZ; we use float XYZ so default 1
        bool flipWinding = false;
        bool flattenAll = false;
        string? textureDirArg = null;
        string? materialsJsonArg = null;
        string? attributorMaterialArg = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--scale=", StringComparison.OrdinalIgnoreCase) &&
                float.TryParse(a[8..], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float s))
                scale = s;
            if (a.Equals("--flip-winding", StringComparison.OrdinalIgnoreCase))
                flipWinding = true;
            if (a.Equals("--flatten-all", StringComparison.OrdinalIgnoreCase))
                flattenAll = true;
            if (a.StartsWith("--texture-dir=", StringComparison.OrdinalIgnoreCase))
                textureDirArg = a["--texture-dir=".Length..];
            if (a.StartsWith("--materials-json=", StringComparison.OrdinalIgnoreCase))
                materialsJsonArg = a["--materials-json=".Length..];
            if (a.StartsWith("--attributor-material=", StringComparison.OrdinalIgnoreCase))
                attributorMaterialArg = a["--attributor-material=".Length..];
        }
        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

        if (positional.Length is < 1 or > 2)
            return CliErrors.Fail("Usage: psg-build-mesh <input.glb> [output.psg] [--scale=1] [--flip-winding] [--flatten-all] [--texture-dir=<dir>] [--materials-json=<path>] [--attributor-material=<path>]");

        string glbPath = positional[0];
        string outPath = positional.Length == 2
            ? positional[1]
            : GetDefaultMeshOutPath(glbPath);
        string textureOutDir = !string.IsNullOrWhiteSpace(textureDirArg)
            ? textureDirArg!
            : GetDefaultTextureOutDir(glbPath);
        if (!string.IsNullOrWhiteSpace(materialsJsonArg) && !File.Exists(materialsJsonArg))
            return CliErrors.Fail($"Materials JSON not found: {materialsJsonArg}");
        string? materialsJsonPath = ResolveMaterialsJsonPath(glbPath, materialsJsonArg);

        if (!File.Exists(glbPath)) return CliErrors.Fail($"Input GLB not found: {glbPath}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        Directory.CreateDirectory(Path.GetFullPath(textureOutDir));

        Console.WriteLine($"Loading GLB: {glbPath}");
        if (!string.IsNullOrWhiteSpace(materialsJsonPath))
            Console.WriteLine($"Using materials JSON for texture paths: {materialsJsonPath}");

        if (flattenAll)
        {
            return RunFlattenAll(glbPath, outPath, textureOutDir, materialsJsonPath, scale, flipWinding, attributorMaterialArg);
        }

        var input = new MeshInputFromGlb(glbPath, scale, reverseWinding: flipWinding);
        if (!string.IsNullOrWhiteSpace(attributorMaterialArg))
            input.AttributorMaterialPath = attributorMaterialArg;

        var textureBuild = GlbTextureAutoBuilder.BuildFromGlb(
            glbPath,
            textureOutDir,
            generateMipMaps: true,
            materialsJsonPath: materialsJsonPath,
            materialNameOverride: input.MaterialName,
            guidNamespace: Path.GetDirectoryName(Path.GetFullPath(glbPath)));
        if (textureBuild.HasOverrides)
        {
            input.TextureChannelOverrides = new RenderMaterialDataBuilder.MaterialTextureOverrides(
                NameChannelGuid: textureBuild.DiffuseGuid,
                DiffuseGuid: textureBuild.DiffuseGuid,
                NormalGuid: textureBuild.NormalGuid,
                LightmapGuid: textureBuild.LightmapGuid,
                SpecularGuid: textureBuild.SpecularGuid);
        }
        if (string.IsNullOrWhiteSpace(input.AttributorMaterialPath) && !string.IsNullOrWhiteSpace(textureBuild.AttributorMaterialStream))
            input.AttributorMaterialPath = textureBuild.AttributorMaterialStream;

        foreach (var tex in textureBuild.BuiltTextures)
            Console.WriteLine($"Texture [{tex.ChannelName}] => {tex.PsgPath} (GUID 0x{tex.Guid:X16})");
        foreach (var warning in textureBuild.Warnings)
            Console.WriteLine($"Texture warning: {warning}");

        var spec = MeshPsgComposer.Compose(input);
        using (var fs = File.Create(outPath))
            GeneralArenaBuilder.Write(spec, fs, ArenaPlatform.Ps3);
        Console.WriteLine($"Wrote mesh PSG: {outPath}");
        return 0;
    }

    /// <summary>
    /// One mesh per GLB primitive; overflow (>65536 verts) split into additional meshes in the same PSG.
    /// All meshes share the same texture from the dominant material.
    /// </summary>
    private static int RunFlattenAll(
        string glbPath,
        string outPath,
        string textureOutDir,
        string? materialsJsonPath,
        float scale,
        bool flipWinding,
        string? attributorMaterialArg)
    {
        var input = new MeshInputFromGlbMulti(glbPath, scale, reverseWinding: flipWinding);
        if (!string.IsNullOrWhiteSpace(attributorMaterialArg))
            input.AttributorMaterialPath = attributorMaterialArg;

        if (input.Parts.Count == 0)
            return CliErrors.Fail("GLB produced no mesh geometry.");

        var textureBuild = GlbTextureAutoBuilder.BuildFromGlb(
            glbPath,
            textureOutDir,
            generateMipMaps: true,
            materialsJsonPath: materialsJsonPath,
            materialNameOverride: input.MaterialName,
            guidNamespace: Path.GetDirectoryName(Path.GetFullPath(glbPath)));

        if (textureBuild.HasOverrides)
        {
            input.TextureChannelOverrides = new RenderMaterialDataBuilder.MaterialTextureOverrides(
                NameChannelGuid: textureBuild.DiffuseGuid,
                DiffuseGuid: textureBuild.DiffuseGuid,
                NormalGuid: textureBuild.NormalGuid,
                LightmapGuid: textureBuild.LightmapGuid,
                SpecularGuid: textureBuild.SpecularGuid);
        }
        if (string.IsNullOrWhiteSpace(input.AttributorMaterialPath) && !string.IsNullOrWhiteSpace(textureBuild.AttributorMaterialStream))
            input.AttributorMaterialPath = textureBuild.AttributorMaterialStream;

        foreach (var tex in textureBuild.BuiltTextures)
            Console.WriteLine($"Texture [{tex.ChannelName}] => {tex.PsgPath} (GUID 0x{tex.Guid:X16})");
        foreach (var warning in textureBuild.Warnings)
            Console.WriteLine($"Texture warning: {warning}");

        var spec = MeshPsgComposer.Compose(input);
        using (var fs = File.Create(outPath))
            GeneralArenaBuilder.Write(spec, fs, ArenaPlatform.Ps3);
        Console.WriteLine($"Wrote mesh PSG: {outPath} ({input.Parts.Count} mesh(es))");

        return 0;
    }

    private static string GetDefaultMeshOutPath(string glbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(glbPath)) ?? ".";
        var outDir = Path.Combine(dir, "cPres_Global");
        string glbStem = Path.GetFileNameWithoutExtension(glbPath);
        string name = Lookup8Hash.HashStringToHex(glbStem + "_mesh") + ".psg";
        return Path.Combine(outDir, name);
    }

    private static string GetDefaultTextureOutDir(string glbPath)
    {
        // Standalone single-GLB build: full-resolution textures live alongside the mesh PSG in
        // cPres_Global as a single self-contained collection.
        var dir = Path.GetDirectoryName(Path.GetFullPath(glbPath)) ?? ".";
        return Path.Combine(dir, TileBuildOptions.CPresGlobalFolder);
    }

    private static string? ResolveMaterialsJsonPath(string glbPath, string? materialsJsonArg)
    {
        if (!string.IsNullOrWhiteSpace(materialsJsonArg))
            return Path.GetFullPath(materialsJsonArg);

        string sidecarJson = Path.ChangeExtension(Path.GetFullPath(glbPath), ".json");
        return File.Exists(sidecarJson) ? sidecarJson : null;
    }
}
