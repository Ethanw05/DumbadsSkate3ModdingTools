using System.Buffers.Binary;
using System.Text;

namespace DlcBuilder.Builders;

/// Builds a Skate-format `.bin` pool file: a sequence of named chunks, each
/// with a 4-char ASCII magic + big-endian uint32 total-size header, padded out
/// to a 16-byte boundary. Today the only chunk type is the string pool ("StrE")
/// followed by the terminator ("EndC"); blob support is included for callers
/// that need to layer non-string payloads alongside.
///
/// Strings added via AddString are null-terminated ASCII, returning the
/// absolute file offset where they were placed (8 + their index inside the
/// StrE payload, accounting for the chunk header) so consumers can store
/// pointers into the pool.
///
/// Ported from MinimalDlcBuilder.BinPoolBuilder. Behavioural parity is
/// guaranteed by the chunk framing (StrE…EndC, 16-byte padding, BE length)
/// and the AddString offset semantics.
public sealed class BinPoolBuilder
{
    private const int ChunkHeaderSize = 8;
    private const int Alignment = 16;

    private readonly MemoryStream _stringPool = new(1024);

    /// Add a null-terminated ASCII string to the string pool. Returns the
    /// absolute file offset (within the assembled .bin) where the string
    /// starts — i.e. 8 (chunk header) + its index in the StrE payload.
    public uint AddString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        uint absOffset = CurrentAbsoluteOffset;
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        _stringPool.Write(bytes, 0, bytes.Length);
        _stringPool.WriteByte(0);
        return absOffset;
    }

    /// Append an arbitrary byte blob (no terminator added). Returns the
    /// absolute offset of its first byte.
    public uint AddBlob(ReadOnlySpan<byte> bytes)
    {
        uint absOffset = CurrentAbsoluteOffset;
        _stringPool.Write(bytes);
        return absOffset;
    }

    /// Serialize to the final `.bin` file: StrE chunk (with all string-pool
    /// content), then a terminating EndC chunk with an 8-byte zero payload.
    public byte[] BuildBinFile()
    {
        using var output = new MemoryStream();
        WriteChunk(output, "StrE", _stringPool.GetBuffer().AsSpan(0, (int)_stringPool.Length));
        WriteChunk(output, "EndC", new byte[8]);
        return output.ToArray();
    }

    /// Absolute file offset of the next byte that AddString/AddBlob will write,
    /// relative to the start of the assembled .bin (so it accounts for the
    /// 8-byte StrE header).
    private uint CurrentAbsoluteOffset => (uint)(ChunkHeaderSize + _stringPool.Length);

    private static void WriteChunk(Stream output, string magic, ReadOnlySpan<byte> payload)
    {
        if (Encoding.ASCII.GetByteCount(magic) != 4)
            throw new ArgumentException("Chunk magic must be exactly 4 ASCII characters.", nameof(magic));

        // total size = header (8) + payload + alignment padding
        int unpadded = ChunkHeaderSize + payload.Length;
        int pad = (Alignment - unpadded % Alignment) % Alignment;
        uint total = (uint)(unpadded + pad);

        Span<byte> hdr = stackalloc byte[ChunkHeaderSize];
        Encoding.ASCII.GetBytes(magic, hdr[..4]);
        BinaryPrimitives.WriteUInt32BigEndian(hdr[4..], total);
        output.Write(hdr);
        output.Write(payload);
        if (pad > 0) output.Write(new byte[pad]);
    }
}
