using System.Numerics;

namespace ArenaBuilder.Collision.Rw;

/// <summary>
/// Working set for one mesh cluster during build. Aligns with <c>rw::collision::meshbuilder::detail::UnitCluster</c>
/// in meshbuilder/detail/unitcluster.h: <c>clusterID</c>, <c>clusterOffset</c> (16-bit mode),
/// <c>unitIDs</c>/<c>numUnits</c>, <c>vertexIDs</c>/<c>numVertices</c>, <c>compressionMode</c>
/// (C++ pads with <c>m_padding[3]</c> after <c>compressionMode</c>; not modeled on this managed type).
/// Sort/compress and <c>GetVertexCode</c> logic live in <see cref="Cluster.ClusterVertexSet"/> and
/// <see cref="Cluster.ClusterUnitOps"/> (same responsibilities as the C++ type’s static/member methods).
/// </summary>
public sealed class RwUnitCluster
{
    /// <summary>Matches <c>clusterID</c> (<c>uint32_t</c>).</summary>
    public uint ClusterId { get; set; }

    /// <summary>16-bit compression origin; matches <c>ClusteredMeshCluster::Vertex32 clusterOffset</c>.</summary>
    public RwVertex32 ClusterOffset { get; set; }

    /// <summary>Triangle indices in this cluster; matches <c>unitIDs</c> / <c>UnitID</c> array semantics.</summary>
    public List<int> UnitIds { get; } = new();

    public uint NumUnits => (uint)UnitIds.Count;

    /// <summary>Vertex indices (global mesh ids); matches <c>vertexIDs</c> / <c>VertexSet</c>.</summary>
    public List<int> VertexIds { get; } = new();

    public uint NumVertices => (uint)VertexIds.Count;

    /// <summary>Matches <c>uint8_t compressionMode</c> (e.g. uncompressed / 16-bit / 32-bit).</summary>
    public byte CompressionMode { get; set; }

    /// <summary>Decoded vertex positions after compression; not part of the C++ <c>UnitCluster</c> struct.</summary>
    public List<Vector3> Vertices { get; } = new();

    /// <summary>Global vertex id to local code; pipeline helper, not in C++ <c>UnitCluster</c>.</summary>
    public Dictionary<int, int> VertexMap { get; } = new();

    /// <summary>Byte offset in serialized cluster stream; pipeline helper.</summary>
    public long ByteOffsetStart { get; set; }

    /// <summary>Per-triangle edge codes; pipeline extension for Skate/cluster encoding.</summary>
    public Dictionary<int, (int E0, int E1, int E2)> EdgeCodes { get; } = new();
}
