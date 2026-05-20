using System.Numerics;

namespace ArenaBuilder.Collision.Compression;

/// <summary>
/// Determine compression mode and offset for cluster vertices.
/// RenderWare: quantize with truncation then <see cref="VertexCompression.DetermineCompressionModeAndOffsetForRange"/>.
/// Ported from Collision_Export_Dumbad_Tuukkas_original.py lines 332-387.
/// </summary>
public static class DetermineCompressionMode
{
    /// <summary>
    /// Returns (compressionMode, (offsetX, offsetY, offsetZ)).
    /// Mode: 1 = 16-bit, 2 = 32-bit. Offset used only for mode 1.
    /// Uses truncation (int)(v / granularity) to match RenderWare.
    /// </summary>
    public static (byte CompressionMode, (int X, int Y, int Z) Offset) Execute(
        IReadOnlyList<Vector3> verts,
        float granularity)
    {
        if (verts == null || verts.Count == 0 || granularity == 0)
            return (CompressionConstants.Vertices16BitCompressed, (0, 0, 0));

        // Convert to integer space (RenderWare: int32_t x32 = (int32_t)( v.GetX() / m_vertexCompressionGranularity );
        int x32Min = int.MaxValue, x32Max = int.MinValue;
        int y32Min = int.MaxValue, y32Max = int.MinValue;
        int z32Min = int.MaxValue, z32Max = int.MinValue;
        foreach (var v in verts)
        {
            int x = (int)(v.X / granularity);
            int y = (int)(v.Y / granularity);
            int z = (int)(v.Z / granularity);
            if (x < x32Min) x32Min = x;
            if (x > x32Max) x32Max = x;
            if (y < y32Min) y32Min = y;
            if (y > y32Max) y32Max = y;
            if (z < z32Min) z32Min = z;
            if (z > z32Max) z32Max = z;
        }

        return VertexCompression.DetermineCompressionModeAndOffsetForRange(
            x32Min, x32Max, y32Min, y32Max, z32Min, z32Max);
    }
}
