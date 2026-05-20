using System.Buffers;
using System.Buffers.Binary;

namespace ArenaBuilder.NavPower;

/// <summary>
/// Big-endian byte writer for NavPower image assembly. Backed by
/// <see cref="ArrayBufferWriter{T}"/> so the buffer grows via pooled
/// arrays and <see cref="ToMemory"/> exposes the written bytes without a
/// second copy (the legacy <see cref="MemoryStream"/>-backed shape did
/// <c>ToArray()</c>, which allocated a duplicate of the entire payload —
/// per-tile NavPower assembly creates 5+ writers and each could be
/// hundreds of KB, so the duplication added up).
/// </summary>
internal sealed class BigEndianWriter
{
    private readonly ArrayBufferWriter<byte> _buf;

    public BigEndianWriter() : this(capacity: 1024) { }

    /// <param name="capacity">Initial buffer size. Callers that know the
    /// final size up front (KD tree size, header+payload sums) should pass
    /// a tight estimate to avoid the writer growing through its pooled-array
    /// doubling steps.</param>
    public BigEndianWriter(int capacity)
    {
        _buf = new ArrayBufferWriter<byte>(Math.Max(16, capacity));
    }

    public long Length => _buf.WrittenCount;

    /// <summary>Returns the written bytes WITHOUT copying. The caller must
    /// not mutate <see cref="WriteUInt32"/> etc. after taking this view,
    /// since the underlying buffer is shared.</summary>
    public ReadOnlyMemory<byte> ToMemory() => _buf.WrittenMemory;

    public void WriteUInt32(uint v)
    {
        Span<byte> span = _buf.GetSpan(4);
        BinaryPrimitives.WriteUInt32BigEndian(span, v);
        _buf.Advance(4);
    }

    public void WriteInt32(int v) => WriteUInt32(unchecked((uint)v));

    public void WriteFloat32(float v)
    {
        Span<byte> span = _buf.GetSpan(4);
        BinaryPrimitives.WriteSingleBigEndian(span, v);
        _buf.Advance(4);
    }

    public void WriteBytes(ReadOnlySpan<byte> b)
    {
        if (b.IsEmpty) return;
        Span<byte> span = _buf.GetSpan(b.Length);
        b.CopyTo(span);
        _buf.Advance(b.Length);
    }
}
