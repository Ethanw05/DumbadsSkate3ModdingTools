namespace ArenaBuilder.Collision;

/// <summary>
/// Abstraction for collision mesh input. Implemented by the Blender add-on or other hosts;
/// ArenaBuilder.Collision consumes this interface only (no Blender dependency).
/// Plan: Section 2 – Input abstraction.
/// </summary>
public interface ICollisionInput
{
    /// <summary>Vertex positions (X, Y, Z).</summary>
    IReadOnlyList<System.Numerics.Vector3> Vertices { get; }

    /// <summary>Triangles as (vertex index 0, 1, 2).</summary>
    IReadOnlyList<(int V0, int V1, int V2)> Faces { get; }

    /// <summary>Optional spline curves (each curve is a list of points).</summary>
    IReadOnlyList<IReadOnlyList<System.Numerics.Vector3>>? Splines { get; }

    /// <summary>Axis-aligned bounding box (min, max) of the mesh.</summary>
    (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max) Bounds { get; }

    /// <summary>
    /// Optional namespace used when computing the instance GUID (e.g. GLB path or output path).
    /// When set, copy-pasted geometry in different GLBs gets different GUIDs and avoids collision.
    /// When null, GUID is derived only from bounds + vertex count (legacy behavior).
    /// </summary>
    string? InstanceGuidNamespace { get; }

    /// <summary>
    /// Optional display name for the instance (e.g. GLB filename stem). Used in the InstanceData component string
    /// so each file gets a unique label: [0x{guid}]_Blender_Export_Collision_{InstanceDisplayName}.
    /// When null, component string uses only the default suffix (not unique per file).
    /// </summary>
    string? InstanceDisplayName { get; }
}
