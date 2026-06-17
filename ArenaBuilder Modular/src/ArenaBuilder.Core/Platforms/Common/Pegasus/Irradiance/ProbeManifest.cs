using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArenaBuilder.Core.Platforms.Common.Pegasus.Irradiance;

/// <summary>
/// JSON interchange format for probe data baked by the Blender add-on
/// (<c>skate3_irradiance_addon.py</c> in <c>Documentation/</c>).
///
/// Positions and SH coefficients are written in <strong>game frame</strong>
/// (Y-up, right-handed); the Blender side is responsible for the axis swizzle
/// before serialising. ArenaBuilder treats values as opaque world-space and
/// only buckets / repacks them.
///
/// Wire shape:
/// <code>
/// {
///   "schema": 1,
///   "probes": [
///     { "pos": [x, y, z], "sh": [[r0,g0,b0], ..., [r8,g8,b8]] },
///     ...
///   ]
/// }
/// </code>
/// </summary>
public sealed class ProbeManifest
{
    public const int CurrentSchema = 1;

    [JsonPropertyName("schema")]
    public int Schema { get; set; } = CurrentSchema;

    [JsonPropertyName("probes")]
    public List<ProbeEntry> Probes { get; set; } = new();

    public sealed class ProbeEntry
    {
        [JsonPropertyName("pos")]
        public float[] Pos { get; set; } = Array.Empty<float>();

        [JsonPropertyName("sh")]
        public float[][] Sh { get; set; } = Array.Empty<float[]>();
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public static ProbeManifest Load(string path)
    {
        using var fs = File.OpenRead(path);
        var m = JsonSerializer.Deserialize<ProbeManifest>(fs, Options)
            ?? throw new InvalidDataException("Probe manifest deserialized to null.");
        if (m.Schema != CurrentSchema)
            throw new InvalidDataException(
                $"Probe manifest schema {m.Schema} not supported (expected {CurrentSchema}).");
        return m;
    }

    public IReadOnlyList<Probe> ToProbes()
    {
        var list = new List<Probe>(Probes.Count);
        for (int i = 0; i < Probes.Count; i++)
        {
            var e = Probes[i];
            if (e.Pos.Length != 3)
                throw new InvalidDataException($"probe[{i}].pos must have 3 floats");
            if (e.Sh.Length != Probe.ShBandCount)
                throw new InvalidDataException($"probe[{i}].sh must have {Probe.ShBandCount} entries");
            var r = new float[Probe.ShBandCount];
            var g = new float[Probe.ShBandCount];
            var b = new float[Probe.ShBandCount];
            for (int band = 0; band < Probe.ShBandCount; band++)
            {
                if (e.Sh[band].Length != 3)
                    throw new InvalidDataException($"probe[{i}].sh[{band}] must be [r,g,b]");
                r[band] = e.Sh[band][0];
                g[band] = e.Sh[band][1];
                b[band] = e.Sh[band][2];
            }
            list.Add(new Probe
            {
                X = e.Pos[0], Y = e.Pos[1], Z = e.Pos[2],
                ShR = r, ShG = g, ShB = b
            });
        }
        return list;
    }
}
