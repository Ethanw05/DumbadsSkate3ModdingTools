using DotRecast.Core.Numerics;
using DotRecast.Recast;
using DotRecast.Recast.Geom;

namespace ArenaBuilder.NavPower;

/// <summary>
/// Minimal <see cref="IRcInputGeomProvider"/> that wraps a single flat triangle mesh
/// built from tile collision geometry. Convex volumes and off-mesh connections are unused.
/// </summary>
internal sealed class NavPowerRecastGeomProvider : IRcInputGeomProvider
{
    private readonly RcTriMesh _mesh;
    private readonly RcVec3f _bmin;
    private readonly RcVec3f _bmax;

    internal NavPowerRecastGeomProvider(float[] verts, int[] tris, RcVec3f bmin, RcVec3f bmax)
    {
        _mesh = new RcTriMesh(verts, tris);
        _bmin = bmin;
        _bmax = bmax;
    }

    public RcTriMesh GetMesh() => _mesh;
    public RcVec3f GetMeshBoundsMin() => _bmin;
    public RcVec3f GetMeshBoundsMax() => _bmax;

    public IEnumerable<RcTriMesh> Meshes()
    {
        yield return _mesh;
    }

    public void AddConvexVolume(RcConvexVolume convexVolume) { }
    public IList<RcConvexVolume> ConvexVolumes() => [];

    public List<RcOffMeshConnection> GetOffMeshConnections() => [];
    public void AddOffMeshConnection(RcVec3f start, RcVec3f end, float radius, bool bidir, int area, int flags) { }
    public void RemoveOffMeshConnections(Predicate<RcOffMeshConnection> filter) { }
}
