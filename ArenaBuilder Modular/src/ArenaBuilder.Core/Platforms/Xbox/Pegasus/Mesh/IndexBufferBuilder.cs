using System.Buffers.Binary;

namespace ArenaBuilder.Core.Platforms.Xbox.Pegasus.Mesh;

/// <summary>
/// Xbox 360 IndexBuffer object (RW type 0x000200EB) — 36 byte form mirroring the runtime
/// <c>renderengine::IndexBuffer</c> struct (sk82_na_zd.xex IDA-verified).
///
/// Layout:
///   +0x00..0x17  D3DResource (24 B) — Common/RefCount/Fence/ReadFence/Identifier/BaseFlush
///   +0x18..0x1B  Address (uint32) — Xenos GPU VA; engine recomputes from base resource
///   +0x1C..0x1F  Size    (uint32) — byte count, 16-B aligned
///   +0x20        m_numIndices (uint32) — index count
///
/// Engine behaviour at load (<c>IndexBuffer::Initialize</c> @ 0x830c967c — DECOMPILED):
///   Zeros the entire 36 B image, then writes:
///     Common = (depth==32 ? 0xC0000000 : 0x20000000) | 2 — D3DRTYPE_INDEXBUFFER (low byte)
///                                                         + depth encoding (high bits)
///     RefCount = 1
///     BaseFlush = 0xFFFF0000
///     Address = raw data pointer (from m_baseResources[2])
///     Size = (numIndices × idxBytes + 15) & ~0xF
///     m_numIndices = params.numIndices
///   Only m_numIndices and Common.depth-bits are read from the on-disk image.
///
/// See docs/X360_Port_Deltas.md §3.
/// </summary>
public static class IndexBufferBuilder
{
    /// <summary>
    /// Builds 36-byte Xbox 360 IndexBuffer. <paramref name="numIndices"/> is the index count.
    /// <paramref name="indexFormat32"/> selects 32-bit indices (uint) instead of 16-bit (ushort).
    /// </summary>
    public static byte[] Build(uint numIndices, bool indexFormat32 = false, int meshIndex = 0)
    {
        var buf = new byte[0x24];
        var s = buf.AsSpan();

        // D3DResource template. Common upper byte encodes depth: 0x20 → 16-bit, 0xC0 → 32-bit.
        // Low byte 0x02 = D3DRTYPE_INDEXBUFFER. Verified 0x20000002 across 3 stock IBs.
        uint common = (indexFormat32 ? 0xC0000000u : 0x20000000u) | 2u;
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x00, 4), common);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x04, 4), 1u);          // ReferenceCount
        // 0x08..0x13 zeros (Fence/ReadFence/Identifier).
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x14, 4), 0xFFFF0000u); // BaseFlush

        // Address dword. Stock X360 meshes write a deterministic per-mesh-index value: 0x05 for the
        // first IB in an arena, +0x07 per subsequent mesh (verified CONSTANT 0x00000005 across 15 stock
        // single-mesh DIST_SkateSchool arenas; 0x05/0x0C/0x13/... across multi-mesh arena[23]). We
        // previously wrote 0. Emit stock's exact value. (IDA: IndexBuffer::Initialize zeros+recomputes
        // this at load, so likely engine-recomputed and inert — kept for byte-exact stock parity.)
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x18, 4), (uint)(0x05 + meshIndex * 0x07));

        // Size = ceil(numIndices × idxBytes, 16). Engine recomputes too but stock writes it.
        uint idxBytes = indexFormat32 ? 4u : 2u;
        uint sizeBytes = (numIndices * idxBytes + 15u) & ~15u;
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x1C, 4), sizeBytes);

        // The single Parameters field read by the engine.
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x20, 4), numIndices);

        return buf;
    }
}
