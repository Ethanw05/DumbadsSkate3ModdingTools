namespace ArenaBuilder.Collision.Rw;

/// <summary>
/// Matches <c>rw::collision::ClusteredMeshCluster::Vertex32</c> — 16-bit vertex compression origin (int32 x,y,z in RW).
/// See meshbuilder/detail/unitcluster.h <c>clusterOffset</c>.
/// </summary>
public readonly record struct RwVertex32(int X, int Y, int Z)
{
    public static implicit operator RwVertex32((int X, int Y, int Z) t) => new(t.X, t.Y, t.Z);
}
