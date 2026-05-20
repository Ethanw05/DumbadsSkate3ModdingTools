using System.Buffers.Binary;

namespace ArenaBuilder.Core.Platforms.PS3.Pegasus.Collision;

/// <summary>
/// Builds 96-byte Volume objects (AGGREGATE or BOX type).
/// </summary>
public static class VolumeBuilder
{
    private const uint VolumeTypeAggregate = 6;
    private const uint VolumeTypeBox = 4;

    public static byte[] Build()
    {
        return BuildAggregate(3);
    }

    public static byte[] Build(uint aggregateTarget)
    {
        return BuildAggregate(aggregateTarget);
    }

    /// <summary>
    /// Builds a VOLUMETYPEAGGREGATE volume whose ___u2 payload is a dict-index
    /// pointing at the paired ClusteredMesh object.
    /// </summary>
    public static byte[] BuildAggregate(uint aggregateTarget)
    {
        var blob = new byte[0x60];
        var s = blob.AsSpan();
        WriteIdentityMatrix(s);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x40, 4), VolumeTypeAggregate);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x44, 4), aggregateTarget);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x48, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x4C, 4), 0);
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(0x50, 4), 0f);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x54, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x58, 4), Ps3RenderWareConstants.NullPointer);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x5C, 4), 1);
        return blob;
    }

    /// <summary>
    /// Builds a VOLUMETYPEBOX volume whose ___u2 payload carries BoxSpecificData
    /// (hx, hy, hz half-extents). Matches retail OTS inner-volume layout.
    /// </summary>
    public static byte[] BuildBox(float hx, float hy, float hz)
    {
        return BuildBox(hx, hy, hz, 0f, 0f, 0f);
    }

    /// <summary>
    /// Builds a VOLUMETYPEBOX volume with explicit world-space center and
    /// half-extents. Retail OTS inner volumes store center in transform row[3].
    /// </summary>
    public static byte[] BuildBox(float hx, float hy, float hz, float cx, float cy, float cz)
    {
        var blob = new byte[0x60];
        var s = blob.AsSpan();
        WriteIdentityMatrix(s);
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(0x30, 4), cx);
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(0x34, 4), cy);
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(0x38, 4), cz);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x40, 4), VolumeTypeBox);
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(0x44, 4), hx);
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(0x48, 4), hy);
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(0x4C, 4), hz);
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(0x50, 4), 0f);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x54, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x58, 4), Ps3RenderWareConstants.NullPointer);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x5C, 4), 1);
        return blob;
    }

    private static void WriteIdentityMatrix(Span<byte> s)
    {
        float[] identity =
        {
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f,
            0f, 0f, 0f, 0f
        };
        for (int i = 0; i < 16; i++)
            BinaryPrimitives.WriteSingleBigEndian(s.Slice(i * 4, 4), identity[i]);
    }
}

