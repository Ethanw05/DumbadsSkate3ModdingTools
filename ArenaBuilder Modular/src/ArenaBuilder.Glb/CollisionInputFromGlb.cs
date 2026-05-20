using ArenaBuilder.Collision;
using System.Numerics;

namespace ArenaBuilder.Glb;

/// <summary>
/// Adapter that feeds flattened GLB geometry into the collision pipeline.
/// </summary>
public sealed class CollisionInputFromGlb : ICollisionInput, ICollisionInputWithSurfaceIds
{
    public IReadOnlyList<Vector3> Vertices { get; }
    public IReadOnlyList<(int V0, int V1, int V2)> Faces { get; }
    public IReadOnlyList<IReadOnlyList<Vector3>>? Splines { get; }
    public (Vector3 Min, Vector3 Max) Bounds { get; }
    public IReadOnlyList<int>? SurfaceIds { get; }

    /// <inheritdoc />
    public string? InstanceGuidNamespace { get; set; }

    /// <inheritdoc />
    public string? InstanceDisplayName { get; set; }

    public CollisionInputFromGlb(
        IReadOnlyList<Vector3> vertices,
        IReadOnlyList<(int V0, int V1, int V2)> faces,
        IReadOnlyList<IReadOnlyList<Vector3>>? splines,
        int surfaceId)
    {
        Vertices = vertices ?? throw new ArgumentNullException(nameof(vertices));
        Faces = faces ?? throw new ArgumentNullException(nameof(faces));
        Splines = splines;
        Bounds = GlbUtilities.ComputeBounds(vertices);
        SurfaceIds = Enumerable.Repeat(surfaceId, faces.Count).ToArray();
    }

    /// <summary>
    /// Constructor with per-face surface IDs (e.g. when merging collision from multiple GLBs).
    /// </summary>
    public CollisionInputFromGlb(
        IReadOnlyList<Vector3> vertices,
        IReadOnlyList<(int V0, int V1, int V2)> faces,
        IReadOnlyList<IReadOnlyList<Vector3>>? splines,
        IReadOnlyList<int>? surfaceIds)
    {
        Vertices = vertices ?? throw new ArgumentNullException(nameof(vertices));
        Faces = faces ?? throw new ArgumentNullException(nameof(faces));
        Splines = splines;
        Bounds = GlbUtilities.ComputeBounds(vertices);
        SurfaceIds = surfaceIds != null && surfaceIds.Count == faces.Count ? surfaceIds : null;
    }
}

