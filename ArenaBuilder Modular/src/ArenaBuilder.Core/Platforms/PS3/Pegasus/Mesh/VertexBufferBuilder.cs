using System.Buffers.Binary;

namespace ArenaBuilder.Core.Platforms.PS3.Pegasus.Mesh;

/// <summary>
/// VertexBuffer object (0x000200EA). PS3 layout.
/// Real mesh + glbtopsg layout:
/// +0x00 baseResourceIndex, +0x04 zero/padding, +0x08 bufferSize, +0x0C flags (usually 0).
/// </summary>
public static class VertexBufferBuilder
{
    /// <summary>
    /// Builds VertexBuffer structure. baseResourceDictIndex = dictionary index of BaseResource entry.
    /// </summary>
    public static byte[] Build(uint baseResourceDictIndex, uint bufferSize, uint flags = 0)
    {
        var buf = new byte[0x10];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), baseResourceDictIndex);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), 0); // Reserved / zero in real meshes.
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), bufferSize);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12, 4), flags);
        return buf;
    }
}

