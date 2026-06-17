using System.Buffers.Binary;
using ArenaBuilder.Core.Platforms.Common;

namespace ArenaBuilder.Core.Platforms.Common.Pegasus.Collision;

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
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x58, 4), PegasusRwConstants.NullPointer);
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
        return BuildBox(hx, hy, hz, cx, cy, cz, 0f);
    }

    /// <summary>
    /// Builds a VOLUMETYPEBOX volume with explicit world center, half-extents,
    /// AND a yaw rotation around Y (radians). The rotation lives in the upper
    /// 3×3 of the inner Volume's 4×4 transform; the world centre stays in
    /// row 3 — so half-extents stay axis-aligned in local space and the
    /// engine rotates them into world space when it reads the matrix.
    ///
    /// IDA evidence: <c>Sk8::VisualDirector::VDWriter::AddRaceGate @ 0x82674010</c>
    /// computes the race-gate visual transform as
    ///   <c>BoundingVolume.transform × tTriggerInstance.m_TransformMatrix</c>
    /// — a matrix–matrix multiply. With <c>m_TransformMatrix = identity</c>
    /// (the convention every stock race PSG uses), the visual matrix == the
    /// BoundingVolume transform, so any rotation we want surfaced in the
    /// gate visual has to live here.
    /// </summary>
    public static byte[] BuildBox(float hx, float hy, float hz, float cx, float cy, float cz, float yawRadians)
    {
        var blob = new byte[0x60];
        var s = blob.AsSpan();

        // Upper 3×3 = yaw rotation around Y (Skate's Y-up convention, row-vector
        // multiplication so a positive yaw turns local +X toward world -Z).
        float cosY = MathF.Cos(yawRadians);
        float sinY = MathF.Sin(yawRadians);
        float[] m =
        {
            cosY, 0f, -sinY, 0f,
            0f,   1f,  0f,   0f,
            sinY, 0f,  cosY, 0f,
            cx,   cy,  cz,   0f,    // row 3 row.w stays 0 — stock writes 0 here, not 1
        };
        for (int i = 0; i < 16; i++)
            BinaryPrimitives.WriteSingleBigEndian(s.Slice(i * 4, 4), m[i]);

        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x40, 4), VolumeTypeBox);
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(0x44, 4), hx);
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(0x48, 4), hy);
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(0x4C, 4), hz);
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(0x50, 4), 0f);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x54, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x58, 4), PegasusRwConstants.NullPointer);
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

