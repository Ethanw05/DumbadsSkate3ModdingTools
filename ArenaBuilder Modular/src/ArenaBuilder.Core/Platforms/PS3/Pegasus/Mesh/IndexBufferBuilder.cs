using System.Buffers.Binary;

namespace ArenaBuilder.Core.Platforms.PS3.Pegasus.Mesh;

/// <summary>
/// IndexBuffer object (0x000200EB). Real mesh layout:
/// +0x00 baseResourceIndex, +0x04 zero/padding, +0x08 numIndices,
/// +0x0C indexFormat (real meshes use 2), +0x10 trailing flags (real meshes use 0x01000000).
/// </summary>
public static class IndexBufferBuilder
{
    /// <summary>
    /// Builds IndexBuffer structure. baseResourceDictIndex = dictionary index of BaseResource entry.
    /// </summary>
    public static byte[] Build(
        uint baseResourceDictIndex,
        uint numIndices,
        uint indexFormat = 2,
        uint trailingFlags = 0x01000000)
    {
        var buf = new byte[0x14];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), baseResourceDictIndex);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), numIndices);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12, 4), indexFormat);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(16, 4), trailingFlags);
        return buf;
    }
}

