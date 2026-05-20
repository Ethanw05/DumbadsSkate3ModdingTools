using System.Buffers.Binary;
using System.IO;

namespace DlcBuilder.Builders;

/// Big-endian and little-endian helpers for `BinaryWriter`. Skate 3 (PS3) is
/// big-endian on disk; only the FE language bin uses little-endian for its
/// 12-byte file header. Replaces the original LINQ-based byte-reverse
/// implementations with `BinaryPrimitives.WriteXxxBigEndian` for speed and
/// allocation-free encoding.
public static class BinaryWriterExtensions
{
    public static void WriteBE(this BinaryWriter w, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        w.Write(b);
    }

    public static void WriteBE(this BinaryWriter w, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        w.Write(b);
    }

    public static void WriteBE(this BinaryWriter w, ulong value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, value);
        w.Write(b);
    }

    public static void WriteLE(this BinaryWriter w, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        w.Write(b);
    }
}
