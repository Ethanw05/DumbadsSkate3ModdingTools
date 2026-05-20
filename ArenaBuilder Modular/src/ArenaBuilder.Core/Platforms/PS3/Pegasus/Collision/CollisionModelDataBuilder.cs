using System.Buffers.Binary;

namespace ArenaBuilder.Core.Platforms.PS3.Pegasus.Collision;

/// <summary>
/// CollisionModelData object (RW type 0x00EB000A, 20 bytes on disk).
///
/// <para><b>Layout:</b></para>
/// <list type="table">
///   <item><term>0x00 m_BoundingVolume</term><description>uint32 — encoded arena dictionary index of the
///     paired <c>Volume</c> object (the inner box volume in OTS layouts).
///     Engine resolves to a real pointer via <c>Arena::IdToObject</c> at fixup.
///     <b>Cannot be zero</b>: tCollisionModel uses this volume for broad-phase
///     hit-tests against actor capsules; a zero pointer crashes
///     <c>cTriggerVolumeManager::TestVolume</c> and the engine silently skips
///     the entire trigger.</description></item>
///   <item><term>0x04 m_iNumMeshes</term><description>uint32 — count of entries in the mesh table.</description></item>
///   <item><term>0x08 m_pMeshTable</term><description>uint32 — relative offset (from struct start) to the
///     mesh table when <c>m_iNumMeshes != 0xFFFFFFFF</c>; otherwise an encoded
///     single-mesh dict index.</description></item>
///   <item><term>0x0C field_0x0C</term><description>uint32 — first (and only, in OTS) mesh table entry.</description></item>
///   <item><term>0x10 padding</term><description>uint32 zero.</description></item>
/// </list>
/// </summary>
public static class CollisionModelDataBuilder
{
    /// <summary>Builds a CollisionModelData blob with <c>m_BoundingVolume = 0</c>
    /// (legacy). Kept for callers that don't yet thread an inner-volume dict
    /// index through. Prefer <see cref="Build(uint)"/> for OTS triggers — the
    /// game's <c>cTriggerVolumeManager</c> dereferences the bounding volume
    /// during world load and an unset pointer prevents trigger registration.</summary>
    public static byte[] Build() => Build(boundingVolumeDictIndex: 0);

    /// <summary>Builds a CollisionModelData blob whose
    /// <c>m_BoundingVolume</c> is the encoded arena dictionary index of the
    /// paired inner <c>Volume</c> object. Verified against retail DW
    /// ots_dwmc_01 cSim_Global PSG: trigger i's CollisionModelData (dict idx
    /// <c>4 + i*4</c>) carries <c>m_BoundingVolume = 5 + i*4</c> — i.e. the
    /// inner box Volume that immediately follows it in the dictionary.</summary>
    public static byte[] Build(uint boundingVolumeDictIndex)
    {
        var blob = new byte[20];
        var s = blob.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0, 4), boundingVolumeDictIndex);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(4, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(8, 4), 0x0C);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(12, 4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(16, 4), 0);
        return blob;
    }
}

