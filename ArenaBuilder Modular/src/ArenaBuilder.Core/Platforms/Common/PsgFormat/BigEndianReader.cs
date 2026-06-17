using System.Buffers.Binary;

namespace ArenaBuilder.Core.Psg;

/// <summary>
/// Big-endian primitive readers (U32, U64, U16, F32) for PSG/RW byte parsing.
/// </summary>
internal static class BigEndianReader
{
    public static uint U32(ReadOnlySpan<byte> s, int off) => BinaryPrimitives.ReadUInt32BigEndian(s.Slice(off, 4));
    public static ulong U64(ReadOnlySpan<byte> s, int off) => BinaryPrimitives.ReadUInt64BigEndian(s.Slice(off, 8));
    public static ushort U16(ReadOnlySpan<byte> s, int off) => BinaryPrimitives.ReadUInt16BigEndian(s.Slice(off, 2));

    public static float F32(ReadOnlySpan<byte> s, int off)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(s.Slice(off, 4)));
}
