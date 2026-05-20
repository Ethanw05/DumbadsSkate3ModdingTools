using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace ChallengeEditor.Psg;

// Reader for Skate 3 PS3 PSF files (CompressedChunkArena format).
//
// Format (all multi-byte fields big-endian):
//   File header (32 bytes meaningful, padded to firstChunkOffset):
//     0x00  4   "SFIL" magic
//     0x04  4   version (3 for DW)
//     0x08  4   reserved/zero
//     0x0C  4   checksum / hash
//     0x10  4   firstChunkOffset (also chunk-header alignment, 0x100)
//     0x14  4   group count
//     0x18  4   chunk-or-group count hint (not always == real chunk count)
//     0x1C  4   payload size hint
//
//   Per chunk (24 bytes meaningful, padded to chunkHeaderSize/0x100):
//     0x00  8   AssetID (u64 BE) - typically a hash of the asset filename
//     0x08  4   srcSize - size of payload (sub-header + RefPack stream)
//     0x0C  4   dataOffset - distance from chunk start to its payload (==0x100)
//     0x10  4   chunk metadata field (varies; not used for parsing)
//     0x14  4   reserved/zero
//
//   Chunk payload (srcSize bytes starting at chunk_start + dataOffset):
//     A small sub-header (typically 0x30 bytes, leading u32 BE = its own size)
//     followed by a RefPack-compressed asset stream.
//
// We walk chunks linearly until EOF; the chunk-count hint at file 0x18 has
// historically been unreliable on real DW DLC samples.
public sealed class PsfReader
{
    public const uint SfilMagic = 0x5346494C; // "SFIL"

    public enum ChunkEncoding
    {
        Unknown,
        Raw,        // payload starts directly with the RW4 PSG (retail PS3 stream files)
        RefPack,    // payload has a small sub-header followed by RefPack-compressed PSG (DLC)
    }

    public sealed class Chunk
    {
        public ulong AssetId;
        public uint SrcSize;
        public uint DataOffset;
        public uint MetaField;
        public long ChunkStart;     // file offset of chunk header
        public long PayloadStart;   // file offset of chunk payload
        public ReadOnlyMemory<byte> Payload = ReadOnlyMemory<byte>.Empty;
        public ChunkEncoding Encoding;
        public int RefPackOffset = -1;  // offset of RefPack signature within payload (only valid when Encoding == RefPack)
    }

    public sealed class File
    {
        public required uint Version;
        public required uint Checksum;
        public required uint FirstChunkOffset;
        public required uint GroupCount;
        public required uint ChunkCountHint;
        public required uint PayloadSizeHint;
        public List<Chunk> Chunks { get; } = new();
    }

    public static File Read(string path)
    {
        byte[] data = System.IO.File.ReadAllBytes(path);
        return Read(data);
    }

    public static File Read(ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> span = data.Span;
        if (span.Length < 0x20) throw new InvalidDataException("PSF too short for SFIL header.");

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(span[..4]);
        if (magic != SfilMagic)
            throw new InvalidDataException($"PSF magic mismatch: expected SFIL (0x{SfilMagic:X8}), got 0x{magic:X8}.");

        File file = new()
        {
            Version = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(0x04, 4)),
            Checksum = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(0x0C, 4)),
            FirstChunkOffset = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(0x10, 4)),
            GroupCount = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(0x14, 4)),
            ChunkCountHint = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(0x18, 4)),
            PayloadSizeHint = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(0x1C, 4)),
        };

        long pos = file.FirstChunkOffset;
        if (pos <= 0 || pos > span.Length)
            throw new InvalidDataException($"PSF firstChunkOffset {pos} out of range (file is {span.Length} bytes).");

        while (pos + 24 <= span.Length)
        {
            ReadOnlySpan<byte> hdr = span.Slice((int)pos, 24);

            // An all-zero "chunk header" at end of file is just trailing pad.
            if (IsAllZero(hdr)) break;

            Chunk chunk = new()
            {
                AssetId = BinaryPrimitives.ReadUInt64BigEndian(hdr[..8]),
                SrcSize = BinaryPrimitives.ReadUInt32BigEndian(hdr.Slice(8, 4)),
                DataOffset = BinaryPrimitives.ReadUInt32BigEndian(hdr.Slice(12, 4)),
                MetaField = BinaryPrimitives.ReadUInt32BigEndian(hdr.Slice(16, 4)),
                ChunkStart = pos,
            };
            chunk.PayloadStart = pos + chunk.DataOffset;

            long payloadEnd = chunk.PayloadStart + chunk.SrcSize;
            if (chunk.PayloadStart < pos || payloadEnd > span.Length || chunk.SrcSize == 0)
                throw new InvalidDataException(
                    $"PSF chunk at 0x{pos:X} has out-of-range payload (payloadStart=0x{chunk.PayloadStart:X}, srcSize=0x{chunk.SrcSize:X}, fileLen=0x{span.Length:X}).");

            chunk.Payload = data.Slice((int)chunk.PayloadStart, (int)chunk.SrcSize);
            ClassifyEncoding(chunk);
            file.Chunks.Add(chunk);

            // Advance to next chunk. The packer's stride per chunk is the metaField
            // (chunkSize: header + padding + payload + end-padding), not just an
            // aligned-up payload end — those two values can disagree by an extra
            // alignment unit, in which case AlignUp(payload_end) lands inside the
            // post-chunk pad zone and the linear walk hits all-zero "chunk headers"
            // and exits early.
            uint align = file.FirstChunkOffset; // 0x100 in practice
            long stride = chunk.MetaField > 0
                ? AlignUp(chunk.MetaField, align)
                : AlignUp(chunk.DataOffset + chunk.SrcSize, align);
            long next = pos + stride;
            if (stride <= 0 || next <= pos) break; // safety
            pos = next;
        }

        return file;
    }

    // Returns the chunk's payload as a usable RW4 PSG byte stream:
    //   - Raw chunks: the payload as-is.
    //   - RefPack chunks: RefPack-decoded bytes (sub-header skipped).
    public static byte[] DecompressChunk(Chunk chunk)
    {
        ReadOnlySpan<byte> payload = chunk.Payload.Span;
        return chunk.Encoding switch
        {
            ChunkEncoding.Raw     => payload.ToArray(),
            ChunkEncoding.RefPack => RefPack.Decode(payload[chunk.RefPackOffset..]),
            _ => throw new InvalidDataException($"Chunk 0x{chunk.AssetId:X16} encoding could not be determined (no RW4 magic, no RefPack signature)."),
        };
    }

    private static void ClassifyEncoding(Chunk chunk)
    {
        ReadOnlySpan<byte> p = chunk.Payload.Span;
        if (p.Length >= 12 && PsgReader.LooksLikePsg(p))
        {
            chunk.Encoding = ChunkEncoding.Raw;
            return;
        }
        int rp = FindRefPackStart(p);
        if (rp >= 0)
        {
            chunk.Encoding = ChunkEncoding.RefPack;
            chunk.RefPackOffset = rp;
            return;
        }
        chunk.Encoding = ChunkEncoding.Unknown;
    }

    public static int FindRefPackStart(ReadOnlySpan<byte> payload)
    {
        // RefPack signature: 0x10 0xFB or 0x90 0xFB. Limit scan to first 0x100 bytes —
        // the sub-header is always small. Past that we'd risk false positives in payload data.
        int limit = Math.Min(payload.Length - 1, 0x100);
        for (int i = 0; i < limit; i++)
        {
            if (payload[i + 1] != 0xFB) continue;
            byte b = payload[i];
            if (b == 0x10 || b == 0x90) return i;
        }
        return -1;
    }

    private static long AlignUp(long value, long alignment)
    {
        long rem = value % alignment;
        return rem == 0 ? value : value + (alignment - rem);
    }

    private static bool IsAllZero(ReadOnlySpan<byte> s)
    {
        for (int i = 0; i < s.Length; i++) if (s[i] != 0) return false;
        return true;
    }
}
