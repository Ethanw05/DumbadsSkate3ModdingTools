using ArenaBuilder.Core.Platforms.PS3.Pegasus.WorldPainter;
using System.Globalization;

namespace ArenaBuilder.Cli.Commands;

/// <summary>
/// Builds a minimal WorldPainter PSG directly (no JSON/intermediate format).
/// </summary>
internal static class PsgBuildWorldPainterCommand
{
    public static int Run(string[] args)
    {
        string? guidArg = GetOptionValue(args, "--guid=");
        string? valueArg = GetOptionValue(args, "--value=");
        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (positional.Length > 1)
            return CliErrors.Fail("Usage: psg-build-worldpainter [output.psg] [--guid=0xGUID64] [--value=0xLO32]  (default Lookup8 pair is guid low/high dwords; --value sets lo, hi=0)");

        string outputPath = positional.Length == 1
            ? positional[0]
            : Path.Combine(Directory.GetCurrentDirectory(), "worldpainter_minimal.psg");

        var options = new WorldPainterPsgBuilder.WorldPainterPsgBuildOptions();

        if (!string.IsNullOrWhiteSpace(guidArg))
        {
            if (!TryParseU64Hex(guidArg!, out ulong layerGuid))
                return CliErrors.Fail($"Invalid --guid value '{guidArg}'. Expected hex, e.g. --guid=0xEA754449D4731193");

            uint lo = (uint)(layerGuid & 0xFFFFFFFFu);
            uint hi = (uint)(layerGuid >> 32);
            if (!string.IsNullOrWhiteSpace(valueArg))
            {
                if (!TryParseU32Hex(valueArg!, out lo))
                    return CliErrors.Fail($"Invalid --value value '{valueArg}'. Expected hex, e.g. --value=0x4911C800");
                hi = 0;
            }

            options = options with
            {
                Layers = new[]
                {
                    new WorldPainterPsgBuilder.WorldPainterLayerSeed(layerGuid, new[] { lo, hi })
                }
            };
        }

        try
        {
            WorldPainterPsgBuilder.WriteMinimal(outputPath, options);
            Console.WriteLine($"Wrote WorldPainter PSG: {Path.GetFullPath(outputPath)}");
            if (options.Layers != null)
            {
                Console.WriteLine($"Layer GUID: 0x{options.Layers[0].LayerGuid:X16}");
                var dv = options.Layers[0].DictionaryValues;
                Console.WriteLine($"Dictionary Lookup8: 0x{dv[0]:X8}, 0x{dv[1]:X8}");
            }
            else
            {
                Console.WriteLine($"Layers written: {WorldPainterPsgBuilder.DefaultLayers.Length} (default hardcoded set)");
            }
            return 0;
        }
        catch (Exception ex)
        {
            return CliErrors.Fail($"WorldPainter build failed: {ex.Message}");
        }
    }

    private static bool TryParseU64Hex(string value, out ulong parsed)
    {
        string v = value.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            v = v[2..];
        return ulong.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed);
    }

    private static bool TryParseU32Hex(string value, out uint parsed)
    {
        string v = value.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            v = v[2..];
        return uint.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed);
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
