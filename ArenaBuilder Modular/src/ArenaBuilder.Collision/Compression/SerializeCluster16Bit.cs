using System.Buffers.Binary;
using System.Numerics;

namespace ArenaBuilder.Collision.Compression;

/// <summary>
/// Serialize vertices using 16-bit compression: 12-byte offset (3 x int32) + 6 bytes per vertex (3 x uint16).
/// RenderWare: ClusteredMeshCluster::SetVertex for VERTICES_16BIT_COMPRESSED (rwcclusteredmeshcluster.cpp lines 255-283).
/// Ported from Collision_Export_Dumbad_Tuukkas_original.py lines 421-475. Uses truncation, not rounding.
/// </summary>
public static class SerializeCluster16Bit
{
    public static byte[] Serialize(
        IReadOnlyList<Vector3> verts,
        float granularity,
        (int X, int Y, int Z) offset)
    {
        if (verts == null || verts.Count == 0)
        {
            var empty = new byte[12];
            return empty;
        }

        var buffer = new byte[12 + verts.Count * 6];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteInt32BigEndian(span, offset.X);
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(4), offset.Y);
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(8), offset.Z);

        for (int i = 0; i < verts.Count; i++)
        {
            var v = verts[i];
            int xVal = (int)(v.X / granularity) - offset.X;
            int yVal = (int)(v.Y / granularity) - offset.Y;
            int zVal = (int)(v.Z / granularity) - offset.Z;
            if (xVal < 0 || xVal > 65535 || yVal < 0 || yVal > 65535 || zVal < 0 || zVal > 65535)
                throw new InvalidOperationException(
                    $"16-bit compression overflow. Values ({xVal},{yVal},{zVal}) exceed uint16. Vertex=({v.X},{v.Y},{v.Z}), Granularity={granularity}, Offset={offset}");
            int baseOff = 12 + i * 6;
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(baseOff), (ushort)(xVal & 0xFFFF));
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(baseOff + 2), (ushort)(yVal & 0xFFFF));
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(baseOff + 4), (ushort)(zVal & 0xFFFF));
        }
        return buffer;
    }
}
