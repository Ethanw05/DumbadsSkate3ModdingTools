namespace ArenaBuilder.Collision.Cluster;

/// <summary>RenderWare: clusteredmeshcluster.h line 245. Skate 3: triangles only (no quads).</summary>
public static class ClusterConstants
{
    public const int MaxVertexCount = 255;

    /// <summary>Max vertices per unit. RenderWare uses 4 for worst-case quads; Skate 3 is triangles-only so use 3.</summary>
    public const int MaxVerticesPerUnit = 3;
}
