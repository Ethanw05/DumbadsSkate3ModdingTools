using DotRecast.Recast;

namespace ArenaBuilder.NavPower;

/// <summary>
/// Opaque handle wrapping a DotRecast <see cref="RcPolyMesh"/> built from the entire world's
/// collision geometry. Passed to <see cref="NavPowerPsgWriter.WriteTilePsgFromGlobalMesh"/>
/// for per-tile clipping and NavPower serialization.
/// </summary>
public sealed class GlobalNavMesh
{
    internal RcPolyMesh Mesh { get; }

    /// <summary>Number of convex polygons in the global nav mesh.</summary>
    public int PolygonCount => Mesh.npolys;

    internal GlobalNavMesh(RcPolyMesh mesh) => Mesh = mesh;
}
