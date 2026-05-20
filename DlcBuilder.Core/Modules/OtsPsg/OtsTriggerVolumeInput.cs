using System.Numerics;
using ArenaBuilder.Collision;

namespace DlcBuilder.Modules.OtsPsg;

/// Adapter for `ArenaBuilder.Collision.ICollisionInput` that turns a single
/// trigger volume's polygon into a triangle-mesh input the existing collision
/// pipeline can consume.
///
/// Generates a closed convex prism: bottom face (fan triangulation), top face
/// (reversed fan), and side quads. For an N-vertex polygon:
///   • vertex count = 2N (bottom ring + top ring)
///   • triangle count = 3N (2 caps × (N-2) tris + N side quads × 2 tris)
///
/// Public so callers can drive the ClusteredMesh pipeline themselves; the
/// OTS PSG builder uses it internally.
public sealed class OtsTriggerVolumeInput : ICollisionInput
{
    private readonly List<Vector3> _verts;
    private readonly List<(int V0, int V1, int V2)> _faces;
    private readonly Vector3 _min, _max;

    public OtsTriggerVolumeInput(OtsTriggerVolume v, string namespaceSuffix)
    {
        ArgumentNullException.ThrowIfNull(v);
        if (v.Polygon == null || v.Polygon.Count < 3)
            throw new ArgumentException("Polygon must have at least 3 points.", nameof(v));

        int n = v.Polygon.Count;
        _verts = new List<Vector3>(n * 2);
        _faces = new List<(int, int, int)>(3 * n);

        // Bottom ring (Y = MinY) — vertices 0..n-1
        for (int i = 0; i < n; i++)
            _verts.Add(new Vector3(v.Polygon[i].X, v.MinY, v.Polygon[i].Z));

        // Top ring (Y = MaxY) — vertices n..2n-1
        for (int i = 0; i < n; i++)
            _verts.Add(new Vector3(v.Polygon[i].X, v.MaxY, v.Polygon[i].Z));

        // Bottom face: fan from vertex 0. (N-2) triangles, normal pointing -Y
        // (outward). Winding (0, i+1, i) gives outward normal for Y-up world.
        for (int i = 1; i < n - 1; i++)
            _faces.Add((0, i + 1, i));

        // Top face: fan from vertex n. (N-2) triangles, normal pointing +Y.
        // Reverse winding compared to the bottom.
        for (int i = 1; i < n - 1; i++)
            _faces.Add((n, n + i, n + i + 1));

        // Side quads — between bottom ring and top ring. Each quad = 2 tris.
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            int b0 = i, b1 = next, t0 = n + i, t1 = n + next;
            // Outward-normal winding.
            _faces.Add((b0, b1, t1));
            _faces.Add((b0, t1, t0));
        }

        Vector3 mn = _verts[0], mx = _verts[0];
        foreach (var p in _verts) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        _min = mn;
        _max = mx;
        InstanceGuidNamespace = namespaceSuffix;
        InstanceDisplayName = v.Name.Length <= 64 ? v.Name : v.Name.Substring(0, 64);
    }

    public IReadOnlyList<Vector3> Vertices => _verts;
    public IReadOnlyList<(int V0, int V1, int V2)> Faces => _faces;
    public IReadOnlyList<IReadOnlyList<Vector3>>? Splines => null;
    public (Vector3 Min, Vector3 Max) Bounds => (_min, _max);
    public string? InstanceGuidNamespace { get; }
    public string? InstanceDisplayName { get; }
}
