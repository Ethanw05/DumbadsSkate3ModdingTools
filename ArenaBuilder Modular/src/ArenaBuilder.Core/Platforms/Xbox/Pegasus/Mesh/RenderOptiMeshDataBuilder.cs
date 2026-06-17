using System.Buffers.Binary;

namespace ArenaBuilder.Core.Platforms.Xbox.Pegasus.Mesh;

/// <summary>
/// Xbox 360 RenderOptiMeshData object (RW type 0x00EB0023) — 116 byte form.
///
/// The 96-byte <c>pegasus::tROptiMeshData</c> runtime struct is identical between PS3 and X360
/// (verified via IDA + tROptiMeshData::Fixup @ 0x82d18a14 which runs the same code on both
/// platforms). The +4 byte size delta vs PS3 (112 B) comes from how the inline payload is
/// packed after the struct:
///
///   PS3 (112 B):  struct(96) + IslandDrawParams(16, with RemapTable overlapping +0x6C)
///   X360 (116 B): struct(96) + IslandDrawParams(16) + RemapTable(4, own slot @ +0x70)
///
/// On Xbox 360 the RemapTable pointer at struct +0x3C (m_pRemapTable) points to +0x70 instead
/// of +0x6C. The RemapTable byte(s) live in their own 4-B aligned slot after the IslandDrawParams
/// block instead of overlapping its trailing pad.
///
/// See docs/X360_Port_Deltas.md §6.
/// </summary>
public static class RenderOptiMeshDataBuilder
{
    private const uint SubrefBase = 0x00800000u;

    /// <summary>Encodes a material subref pointer (subref-base | record index).</summary>
    public static uint EncodeMaterialSubref(int subrefRecordIndex) => SubrefBase | (uint)subrefRecordIndex;

    /// <summary>
    /// Builds the 116-byte Xbox 360 RenderOptiMeshData. Parameters match the PS3 builder
    /// signature one-for-one; only the inline trailing layout differs.
    /// </summary>
    public static byte[] Build(
        (float X, float Y, float Z) bboxMin,
        (float X, float Y, float Z) bboxMax,
        uint numVerts,
        uint materialSubrefPtr,
        uint vdDictIndex,
        uint meshHelperDictIndex,
        uint ibDictIndex,
        uint vbDictIndex,
        uint numIndices,
        uint islandAreasSubrefIndex = 1,
        uint islandAABBsSubrefIndex = 2)
    {
        // 96 (struct) + 16 (IslandDrawParams) + 4 (RemapTable slot) = 116.
        var buf = new byte[0x74];
        var s = buf.AsSpan();

        // BBox (32 B): min{x,y,z,0}, max{x,y,z,0}.
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(0x00, 4), bboxMin.X);
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(0x04, 4), bboxMin.Y);
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(0x08, 4), bboxMin.Z);
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(0x0C, 4), 0f);
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(0x10, 4), bboxMax.X);
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(0x14, 4), bboxMax.Y);
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(0x18, 4), bboxMax.Z);
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(0x1C, 4), 0f);

        // 0x20..0x5F: pegasus::tROptiMeshData mid-struct (identical layout to PS3).
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x20, 4), numVerts);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x24, 4), materialSubrefPtr);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x28, 4), vdDictIndex);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x2C, 4), meshHelperDictIndex);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x30, 4), ibDictIndex);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x34, 4), vbDictIndex);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x38, 4), 1u);                                // numIslands
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x3C, 4), SubrefBase | islandAreasSubrefIndex);// m_pIslandAreas
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x40, 4), SubrefBase | islandAABBsSubrefIndex);// m_pIslandAABBs
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x44, 4), 0x60u);                             // m_pIslandDrawParams → +0x60
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x48, 4), 1u);                                // m_uiNumRemapIndices
        // X360 delta: RemapTable lives in its own 4-B slot at +0x70 (PS3 overlaps at +0x6C).
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x4C, 4), 0x70u);                             // m_pRemapTable
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x50, 4), 0u);                                // m_uiNumBlendShapes
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x54, 4), 0u);                                // m_pBlendShapeTable
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x58, 4), 0u);                                // m_szBlendShapeNames
        // 0x5C..0x5F: pad to 96 B.

        // IslandDrawParams @ +0x60 (16 B) — X360 layout, byte-verified against stock
        // DIST_BlackBoxPark cPres mesh (IslandDrawParameters = [4, 0, 0, indexCount]):
        //   word[0] = primitive type   (4 = D3DPT_TRIANGLELIST)
        //   word[1] = 0
        //   word[2] = 0
        //   word[3] = index count      (== IndexBuffer.m_bufferSize/numIndices; stock mesh had 252 here
        //                               matching its IndexBuffer +0x20)
        // The PS3 builder uses a DIFFERENT layout ([0, indexCount, 0x05000000, 0]) — the X360 builder
        // had wrongly copied it, so X360 got primType=0 (invalid) + count=0 → GPU drew zero triangles →
        // mesh invisible while collision (separate arena) worked. On X360 m_pRemapTable=+0x70 (own slot),
        // so word[3] (+0x6C) is free for the count (on PS3 it overlaps the remap table at +0x6C).
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x60, 4), 4u);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x64, 4), 0u);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x68, 4), 0u);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x6C, 4), numIndices);

        // RemapTable @ +0x70 (4 B). Single-island content is a u16 remap index padded to 4 B.
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x70, 4), 0u);

        return buf;
    }
}
