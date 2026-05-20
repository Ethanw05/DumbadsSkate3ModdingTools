using System.Buffers.Binary;

namespace ArenaBuilder.Core.Platforms.PS3.Pegasus.Mesh;

/// <summary>
/// VersionData object. Python _build_objects adds be_u32(25)+be_u32(13)+be_u64(0) for TYPE_VER.
/// </summary>
public static class VersionDataBuilder
{
    public static byte[] Build()
    {
        var buf = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), 25);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), 13);
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(8, 8), 0);
        return buf;
    }
}

