using System.Numerics;
using System.Text.Json;

namespace ArenaBuilder.Build;

/// <summary>
/// Loads BlenRose materials JSON (from Blender export) for collision surface IDs and splines.
/// </summary>
public sealed class BlenroseMaterialsDb
{
    public sealed class CollisionInfo
    {
        public int PhysicsSurface { get; init; }
        public int AudioSurface { get; init; }
        public int SurfacePattern { get; init; }
    }

    public sealed class Spline
    {
        public string Name { get; init; } = string.Empty;
        public List<Vector3> Points { get; init; } = new();
        public bool IsClosed { get; init; }
        public string Type { get; init; } = string.Empty;
    }

    public sealed class Material
    {
        public string Name { get; init; } = string.Empty;
        public CollisionInfo Collision { get; init; } = new();
        /// <summary>
        /// When true, tile collision accumulation skips triangles that use this material (BlenRose export).
        /// </summary>
        public bool ExcludeCollision { get; init; }
        /// <summary>
        /// When true, tile cPres/presentation accumulation skips triangles that use this material (BlenRose export).
        /// </summary>
        public bool ExcludePres { get; init; }
        /// <summary>
        /// When true, triangles using this material are NOT split across per-tile cPres folders; instead the
        /// entire primitive is routed into a single cPres_Global mesh PSG (BlenRose export).
        /// </summary>
        public bool IncludeInCpresGlobal { get; init; }
        public List<Spline>? Splines { get; init; }
    }

    private readonly Dictionary<string, Material> _materials;

    private BlenroseMaterialsDb(Dictionary<string, Material> materials) => _materials = materials;

    public static BlenroseMaterialsDb Load(string jsonPath)
    {
        using var fs = File.OpenRead(jsonPath);
        using var doc = JsonDocument.Parse(fs);
        var mats = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            string name = prop.Name;
            var root = prop.Value;

            var collision = root.GetProperty("collision");
            int physics = int.Parse(collision.GetProperty("physics_surface").GetString() ?? "0");
            int audio = int.Parse(collision.GetProperty("audio_surface").GetString() ?? "0");
            int pattern = int.Parse(collision.GetProperty("surface_pattern").GetString() ?? "0");

            bool excludeCollision = root.TryGetProperty("exclude_collision", out var exclEl)
                && exclEl.ValueKind == JsonValueKind.True;
            bool excludePres = root.TryGetProperty("exclude_pres", out var exclPresEl)
                && exclPresEl.ValueKind == JsonValueKind.True;
            bool includeInCpresGlobal = root.TryGetProperty("include_in_cpres_global", out var inclGlobalEl)
                && inclGlobalEl.ValueKind == JsonValueKind.True;

            List<Spline>? splines = null;
            if (root.TryGetProperty("splines", out var splinesEl) && splinesEl.ValueKind == JsonValueKind.Array)
            {
                splines = new List<Spline>();
                foreach (var s in splinesEl.EnumerateArray())
                {
                    var pts = new List<Vector3>();
                    foreach (var p in s.GetProperty("points").EnumerateArray())
                    {
                        pts.Add(new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle()));
                    }
                    splines.Add(new Spline
                    {
                        Name = s.GetProperty("name").GetString() ?? string.Empty,
                        IsClosed = s.GetProperty("is_closed").GetBoolean(),
                        Type = s.GetProperty("type").GetString() ?? string.Empty,
                        Points = pts
                    });
                }
            }

            mats[name] = new Material
            {
                Name = name,
                Collision = new CollisionInfo { PhysicsSurface = physics, AudioSurface = audio, SurfacePattern = pattern },
                ExcludeCollision = excludeCollision,
                ExcludePres = excludePres,
                IncludeInCpresGlobal = includeInCpresGlobal,
                Splines = splines
            };
        }

        return new BlenroseMaterialsDb(mats);
    }

    public Material? TryGetMaterial(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return _materials.TryGetValue(name, out var m) ? m : null;
    }
}
