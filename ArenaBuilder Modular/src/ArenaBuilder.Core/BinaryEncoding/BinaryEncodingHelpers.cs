using System.Buffers;
using System.Buffers.Binary;

namespace ArenaBuilder.Core.BinaryEncoding;

/// <summary>
/// Shared binary encoding helpers for big-endian PSG format.
///
/// <para>The legacy <see cref="BeU32"/> / <see cref="BeU16"/> / <see cref="BeU64"/>
/// / <see cref="BeF32"/> methods return fresh <c>byte[]</c> instances and are
/// almost always followed by <c>list.AddRange(BeU32(x))</c> which boxes the
/// array as <see cref="IEnumerable{T}"/>. They remain here for legacy
/// callers but new code should use the <c>WriteBeU*</c> extension methods on
/// <see cref="IBufferWriter{T}"/> below — those write directly into a pooled
/// span with zero per-call allocation and no enumerator boxing.</para>
/// </summary>
public static class BinaryEncodingHelpers
{
    /// <summary>Encodes a uint32 as big-endian bytes. <b>Legacy</b>: allocates a 4-byte array; prefer <see cref="WriteBeU32"/>.</summary>
    public static byte[] BeU32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>Encodes a uint16 as big-endian bytes. <b>Legacy</b>: allocates a 2-byte array; prefer <see cref="WriteBeU16"/>.</summary>
    public static byte[] BeU16(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>Encodes a uint64 as big-endian bytes. <b>Legacy</b>: allocates an 8-byte array; prefer <see cref="WriteBeU64"/>.</summary>
    public static byte[] BeU64(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>Encodes a float32 as big-endian bytes. <b>Legacy</b>: allocates a 4-byte array; prefer <see cref="WriteBeF32"/>.</summary>
    public static byte[] BeF32(float value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteSingleBigEndian(bytes, value);
        return bytes;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Allocation-free fast path. Each call grabs a small span from the
    // writer (which it must already have pre-allocated capacity for), writes
    // the value in-place, and advances. No `byte[]`, no `AddRange`, no
    // enumerator boxing — what the legacy `BeU*` callers were paying per
    // field. PSG builders typically write tens to hundreds of fields per
    // mesh part / per texture / per material, multiplied by thousands of
    // PSGs in a tile build — this is the single highest-leverage allocation
    // cleanup in the writer hot path.
    // ──────────────────────────────────────────────────────────────────────
    public static void WriteBeU32(this IBufferWriter<byte> w, uint value)
    {
        Span<byte> span = w.GetSpan(4);
        BinaryPrimitives.WriteUInt32BigEndian(span, value);
        w.Advance(4);
    }

    public static void WriteBeU16(this IBufferWriter<byte> w, ushort value)
    {
        Span<byte> span = w.GetSpan(2);
        BinaryPrimitives.WriteUInt16BigEndian(span, value);
        w.Advance(2);
    }

    public static void WriteBeU64(this IBufferWriter<byte> w, ulong value)
    {
        Span<byte> span = w.GetSpan(8);
        BinaryPrimitives.WriteUInt64BigEndian(span, value);
        w.Advance(8);
    }

    public static void WriteBeF32(this IBufferWriter<byte> w, float value)
    {
        Span<byte> span = w.GetSpan(4);
        BinaryPrimitives.WriteSingleBigEndian(span, value);
        w.Advance(4);
    }

    /// <summary>Append raw bytes (e.g. payload, sub-blob) to the writer.</summary>
    public static void WriteBytes(this IBufferWriter<byte> w, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return;
        Span<byte> span = w.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        w.Advance(bytes.Length);
    }

    /// <summary>Append <paramref name="count"/> zero bytes (used for header
    /// padding / alignment fill — replaces byte-by-byte <c>list.Add(0)</c>
    /// loops in <c>GenericArenaWriter</c> with a single span clear).</summary>
    public static void WriteZeros(this IBufferWriter<byte> w, int count)
    {
        if (count <= 0) return;
        Span<byte> span = w.GetSpan(count);
        span.Slice(0, count).Clear();
        w.Advance(count);
    }
}
