using ArenaBuilder.Core.Psg;
using ArenaBuilder.Texture;
using ArenaBuilder.Texture.Dds;
using System.Globalization;

using ArenaBuilder.Core.Platforms.Common.PsgFormat;

namespace ArenaBuilder.Cli.Commands;

/// <summary>
/// Build a texture PSG from DDS/PNG/JPG.
/// For PNG/JPG input, image is converted to DDS DXT5 first.
/// </summary>
internal static class PsgBuildTexturesCommand
{
    public static int Run(string[] args)
    {
        bool generateMipMaps = !args.Any(a => a.Equals("--no-mips", StringComparison.OrdinalIgnoreCase));
        bool hasAlpha = args.Any(a => a.Equals("--alpha", StringComparison.OrdinalIgnoreCase) || a.Equals("--dxt5", StringComparison.OrdinalIgnoreCase));
        string? guidArg = GetOptionValue(args, "--guid=");

        // Platform: PS3 (.psg, default) or Xbox 360 (.rx2). Accept --platform=xbox|ps3|360 and --xbox.
        string? platformArg = GetOptionValue(args, "--platform=");
        bool xbox = args.Any(a => a.Equals("--xbox", StringComparison.OrdinalIgnoreCase) || a.Equals("--rx2", StringComparison.OrdinalIgnoreCase))
                    || (platformArg != null && (platformArg.Equals("xbox", StringComparison.OrdinalIgnoreCase)
                                                || platformArg.Equals("xbox360", StringComparison.OrdinalIgnoreCase)
                                                || platformArg.Equals("360", StringComparison.OrdinalIgnoreCase)));

        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (positional.Length is < 1 or > 2)
            return CliErrors.Fail("Usage: psg-build-textures <input.{dds|png|jpg|jpeg}> [output.{psg|rx2}] [--platform=xbox] [--guid=0xGUID] [--no-mips] [--alpha]");

        string inputPath = positional[0];
        string outPath = positional.Length == 2
            ? positional[1]
            : GetDefaultTextureOutPath(inputPath, xbox);

        if (!File.Exists(inputPath))
            return CliErrors.Fail($"Input file not found: {inputPath}");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

        string ext = Path.GetExtension(inputPath).ToLowerInvariant();
        byte[] inputBytes = File.ReadAllBytes(inputPath);
        byte[] ddsBytes;
        if (ext == ".dds")
        {
            Console.WriteLine($"Reading DDS: {inputPath}");
            ddsBytes = inputBytes;
        }
        else if (ext is ".png" or ".jpg" or ".jpeg")
        {
            Console.WriteLine($"Converting image to DDS {(hasAlpha ? "DXT5 (alpha)" : "DXT1 (no alpha)")} : {inputPath}");
            try
            {
                ddsBytes = ImageToDdsConverter.ConvertToDds(inputBytes, generateMipMaps, hasAlpha);
            }
            catch (Exception ex)
            {
                return CliErrors.Fail($"Image -> DDS conversion failed: {ex.Message}");
            }
        }
        else
        {
            return CliErrors.Fail("Unsupported input extension. Use .dds, .png, .jpg, or .jpeg.");
        }

        DdsTextureInput ddsInput;
        try
        {
            ddsInput = DdsReader.Read(ddsBytes);
        }
        catch (Exception ex)
        {
            return CliErrors.Fail($"DDS parse failed: {ex.Message}");
        }

        string stem = Path.GetFileNameWithoutExtension(inputPath);
        ulong textureGuid;
        if (!string.IsNullOrWhiteSpace(guidArg))
        {
            if (!TryParseGuidHex(guidArg!, out textureGuid))
                return CliErrors.Fail($"Invalid --guid value '{guidArg}'. Expected hex, e.g. --guid=0x010C746AE5A9798B");
        }
        else
        {
            string textureKey = TextureGuidStrategy.BuildTextureKey(stem, stem, "diffuse", stem);
            textureGuid = TextureGuidStrategy.KeyToGuid(textureKey);
        }

        if (xbox)
        {
            TextureRX2Composer.Write(ddsInput, textureGuid, outPath);
            Console.WriteLine($"Wrote X360 texture arena: {outPath} (GUID 0x{textureGuid:X16})");
            return 0;
        }

        var spec = TexturePsgComposer.Compose(ddsInput, textureGuid);
        using (var fs = File.Create(outPath))
            GeneralArenaBuilder.Write(spec, fs, ArenaPlatform.Ps3);

        Console.WriteLine($"Wrote texture PSG: {outPath} (GUID 0x{textureGuid:X16})");
        return 0;
    }

    private static string GetDefaultTextureOutPath(string ddsPath, bool xbox)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(ddsPath)) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(ddsPath);
        string textureKey = TextureGuidStrategy.BuildTextureKey(stem, stem, "diffuse", stem);
        ulong guid = TextureGuidStrategy.KeyToGuid(textureKey);
        return Path.Combine(dir, $"{guid:X16}.{(xbox ? "rx2" : "psg")}");
    }

    private static bool TryParseGuidHex(string value, out ulong guid)
    {
        string v = value.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            v = v[2..];
        return ulong.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out guid);
    }

    private static string? GetOptionValue(IEnumerable<string> args, string optionPrefix)
    {
        foreach (var a in args)
        {
            if (a.StartsWith(optionPrefix, StringComparison.OrdinalIgnoreCase))
                return a.Substring(optionPrefix.Length);
        }
        return null;
    }
}
