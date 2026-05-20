using System.Buffers.Binary;
using System.Numerics;

namespace ArenaBuilder.Collision.Compression;

/// <summary>
/// Serialize vertices as uncompressed float32 (16 bytes per vertex: x,y,z,w padding).
/// RenderWare: ClusteredMeshCluster::SetVertex for VERTICES_UNCOMPRESSED (rwcclusteredmeshcluster.cpp lines 234-254, 299-303).
/// Ported from Collision_Export_Dumbad_Tuukkas_original.py lines 391-418.
/// </summary>
public static class SerializeClusterUncompressed
{
    private const int BytesPerVertex = 16; // 4 x float32

    public static byte[] Serialize(IReadOnlyList<Vector3> verts)
    {
        if (verts == null || verts.Count == 0)
            return Array.Empty<byte>();

        var buffer = new byte[verts.Count * BytesPerVertex];
        for (int i = 0; i < verts.Count; i++)
        {
            var v = verts[i];
            var span = buffer.AsSpan(i * BytesPerVertex, BytesPerVertex);
            BinaryPrimitives.WriteSingleBigEndian(span, v.X);
            BinaryPrimitives.WriteSingleBigEndian(span.Slice(4), v.Y);
            BinaryPrimitives.WriteSingleBigEndian(span.Slice(8), v.Z);
            BinaryPrimitives.WriteSingleBigEndian(span.Slice(12), 0f);
        }
        return buffer;
    }
}
