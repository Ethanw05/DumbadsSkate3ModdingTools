using System.Buffers.Binary;

namespace ArenaBuilder.Core.Platforms.Xbox.Pegasus.Mesh;

/// <summary>
/// Xbox 360 VertexBuffer object (RW type 0x000200EA) — 40 byte form mirroring the runtime
/// <c>renderengine::VertexBuffer</c> struct (sk82_na_zd.xex IDA-verified).
///
/// Layout:
///   +0x00..0x17  D3DResource (24 B) — Common/RefCount/Fence/ReadFence/Identifier/BaseFlush
///   +0x18..0x1F  GPUVERTEX_FETCH_CONSTANT (8 B) — Xenos fetch register pair
///   +0x20        m_bufferSize (uint32) — raw vertex data byte count
///   +0x24        m_type       (uint32) — usually 0
///
/// Engine behaviour at load (<c>VertexBuffer::Initialize</c> @ 0x830cafb8 — DECOMPILED):
///   Zeros the entire 40 B image, then writes Common=1, RefCount=1, BaseFlush=0xFFFF0000,
///   Format.dword[0]|=3, calls <c>XGSetVertexBufferHeader</c>, then reads m_bufferSize
///   and m_type from the on-disk image's trailing 8 B as Parameters.
///   Leading 32 B are clobbered — disk values there are template-only.
///
/// Builder writes template constants matching stock files for diff parity; only
/// +0x20 (bufferSize) and +0x24 (type) carry meaningful Parameters values.
///
/// See docs/X360_Port_Deltas.md §2.
/// </summary>
public static class VertexBufferBuilder
{
    /// <summary>
    /// Builds 40-byte Xbox 360 VertexBuffer. <paramref name="bufferSize"/> is the raw vertex
    /// data byte count (numVertices × stride). <paramref name="type"/> is the runtime usage
    /// type (default 0 — see <c>renderengine::VertexBuffer::Type</c>).
    /// </summary>
    public static byte[] Build(uint bufferSize, uint type = 0, int meshIndex = 0)
    {
        var buf = new byte[0x28];
        var s = buf.AsSpan();

        // D3DResource template (engine overwrites at load, but stock files carry these).
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x00, 4), 1u);          // Common
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x04, 4), 1u);          // ReferenceCount
        // 0x08..0x13: Fence, ReadFence, Identifier — zero (handled by new byte[] init).
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x14, 4), 0xFFFF0000u); // BaseFlush

        // GPUVERTEX_FETCH_CONSTANT dword[0]. Stock X360 meshes write a deterministic per-mesh-index
        // value: 0x0F for the first VB in an arena, +0x1C per subsequent mesh (verified CONSTANT
        // 0x0000000F across 15 stock single-mesh DIST_SkateSchool arenas; 0x0F/0x2B/0x47/... across
        // multi-mesh arena[23]). We previously wrote 0 — the only structural byte difference vs stock.
        // Emit stock's exact value. (IDA: VertexBuffer::Initialize zeros+recomputes this at load, so
        // it is likely engine-recomputed and inert — kept for byte-exact stock parity.)
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x18, 4), (uint)(0x0F + meshIndex * 0x1C));

        // dword[1] = (GLBtoRX2 line 930-942): bit[28] VB flag, bits[1:0] Type=2, bits[29:2] size.
        // Stock VB (size 0x1E0) = 0x100001E2; we now emit the same (was 0x100001E0, missing Type=2).
        // NOTE: this is PARITY ONLY — XGSetVertexBufferHeader@0x83138708 zeros the image and rewrites
        // dword[1] = Length&0x3FFFFFC | 0x10000002 from the runtime args, so the on-disk value is
        // don't-care. Kept identical to stock for clean diffs; it is NOT the render fix.
        uint fetch1 = 0x10000002u | ((bufferSize + 2) & 0x2FFFFFFCu);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x1C, 4), fetch1);

        // Parameters — these are the only fields the engine reads from disk before
        // zeroing the runtime allocation.
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x20, 4), bufferSize);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x24, 4), type);

        return buf;
    }
}
