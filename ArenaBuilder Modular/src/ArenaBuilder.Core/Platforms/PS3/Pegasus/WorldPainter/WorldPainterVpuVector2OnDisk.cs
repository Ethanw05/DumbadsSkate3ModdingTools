using System.Buffers.Binary;

namespace ArenaBuilder.Core.Platforms.PS3.Pegasus.WorldPainter;

/// <summary>
/// On-disk layout for <c>rw::math::vpu::Vector2</c> inside <c>pegasus::tWorldPainterQuadTreeData</c>.
/// IDA: <c>struct rw::math::vpu::Vector2 { __vector4 mV; }</c> — 16 bytes per logical 2D value
/// (<c>documentation/Skate-File-Format-Documentation-main/.../Quick and Dirty IDA Export.h</c>).
/// </summary>
/// <remarks>
/// <para>
/// <c>WorldPainter::DoQuadTreeLookup</c> loads both root vectors with <c>lvx128</c> at <c>pData+0</c> and <c>pData+16</c>
/// (<c>documentation/WorldPainter/8238F988</c>). <c>InBounds4Way</c> then uses full 128-bit operands before child tests
/// (<c>documentation/WorldPainter/8238FB80</c>). Lanes 0–1 are world X and world Z (horizontal plane); that is all tooling
/// and tooling need for parity with the scalar reduction in
/// <c>Dumping Tools/worldpainter_lookup.py</c> (<c>vcmpbfp_bounds_ok</c> on XZ only).
/// </para>
/// <para>
/// Lanes 2–3 are written as <b>0f</b>: canonical clean <c>__vector4</c> for unused SIMD lanes. Some retail assets contain
/// tiny non-zero values there (likely exporter/register residue); they are not required for the scalar lookup path above.
/// </para>
/// </remarks>
public static class WorldPainterVpuVector2OnDisk
{
    /// <summary>Writes one 16-byte big-endian <c>vpu::Vector2</c> / <c>__vector4</c> (four IEEE f32).</summary>
    public static void WriteVector4BigEndian(Span<byte> dest16, float x, float y, float z, float w)
    {
        if (dest16.Length < 16)
            throw new ArgumentException("Destination must be at least 16 bytes.", nameof(dest16));
        BinaryPrimitives.WriteSingleBigEndian(dest16.Slice(0, 4), x);
        BinaryPrimitives.WriteSingleBigEndian(dest16.Slice(4, 4), y);
        BinaryPrimitives.WriteSingleBigEndian(dest16.Slice(8, 4), z);
        BinaryPrimitives.WriteSingleBigEndian(dest16.Slice(12, 4), w);
    }

    /// <summary>Horizontal XZ only; SIMD lanes 2–3 cleared (see class remarks).</summary>
    public static void WriteVectorXyZeroZw(Span<byte> dest16, float worldX, float worldZ) =>
        WriteVector4BigEndian(dest16, worldX, worldZ, 0f, 0f);

    /// <summary>Writes <c>m_RootNodeTestSubtractor</c> and <c>m_RootNodeHalfWidth</c> (0x00..0x1F of the quad blob).</summary>
    public static void WriteRootPairXyZeroZw(Span<byte> dest32, float rootCenterX, float rootCenterZ, float rootHalfX, float rootHalfZ)
    {
        if (dest32.Length < 0x20)
            throw new ArgumentException("Destination must be at least 0x20 bytes.", nameof(dest32));
        WriteVectorXyZeroZw(dest32.Slice(0, 0x10), rootCenterX, rootCenterZ);
        WriteVectorXyZeroZw(dest32.Slice(0x10, 0x10), rootHalfX, rootHalfZ);
    }
}
