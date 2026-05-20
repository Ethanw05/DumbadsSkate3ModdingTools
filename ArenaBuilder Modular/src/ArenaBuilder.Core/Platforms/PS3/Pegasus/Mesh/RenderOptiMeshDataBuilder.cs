using static ArenaBuilder.Core.BinaryEncoding.BinaryEncodingHelpers;

namespace ArenaBuilder.Core.Platforms.PS3.Pegasus.Mesh;

/// <summary>
/// RenderOptiMeshData object (0x00EB0023), real mesh style.
/// - Base struct is 0x60 bytes.
/// - For one island, draw params start at +0x60 and remap table pointer is +0x6C (inside draw params block).
/// - Island area/AABB payloads are owned by RenderModelData and referenced via subrefs.
/// - m_pMaterial/m_pIsland* use subref encoding (0x00800000 | recordIndex); VB/IB/VD/MeshHelper use dict indices.
/// </summary>
public static class RenderOptiMeshDataBuilder
{
    private const uint SubrefBase = 0x00800000;

    /// <summary>
    /// Encodes material subref pointer.
    /// </summary>
    public static uint EncodeMaterialSubref(int subrefRecordIndex) => SubrefBase | (uint)subrefRecordIndex;

    /// <summary>
    /// Builds RenderOptiMeshData. materialSubrefPtr = 0x00800000 | subrefRecordIndex.
    /// vdDictIndex, meshHelperDictIndex, ibDictIndex, vbDictIndex are dictionary indices.
    /// islandAreasSubrefIndex, islandAABBsSubrefIndex: subref record indices for IslandAreas/IslandAABBs in RenderModelData.
    /// Single-mesh uses 1,2; multi-mesh mesh i uses (1+3*i), (2+3*i) per real dumps.
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
        // Base struct (0x60) + one draw params entry (0x10) = 0x70.
        var buf = new List<byte>(0x70);

        buf.AddRange(BeF32(bboxMin.X));
        buf.AddRange(BeF32(bboxMin.Y));
        buf.AddRange(BeF32(bboxMin.Z));
        buf.AddRange(BeF32(0));
        buf.AddRange(BeF32(bboxMax.X));
        buf.AddRange(BeF32(bboxMax.Y));
        buf.AddRange(BeF32(bboxMax.Z));
        buf.AddRange(BeF32(0));

        buf.AddRange(BeU32(numVerts));
        buf.AddRange(BeU32(materialSubrefPtr));
        buf.AddRange(BeU32(vdDictIndex));
        buf.AddRange(BeU32(meshHelperDictIndex));
        buf.AddRange(BeU32(ibDictIndex));
        buf.AddRange(BeU32(vbDictIndex));

        buf.AddRange(BeU32(1)); // numIslands
        buf.AddRange(BeU32(SubrefBase | islandAreasSubrefIndex)); // m_pIslandAreas
        buf.AddRange(BeU32(SubrefBase | islandAABBsSubrefIndex)); // m_pIslandAABBs
        buf.AddRange(BeU32(0x60)); // m_pIslandDrawParams (relative offset)
        buf.AddRange(BeU32(1)); // m_uiNumRemapIndices (real meshes use 1 here, not numIndices)
        buf.AddRange(BeU32(0x6C)); // m_pRemapTable (relative offset inside draw params block)
        buf.AddRange(BeU32(0)); // numBlendShapes
        buf.AddRange(BeU32(0)); // m_pBlendShapeTable
        buf.AddRange(BeU32(0)); // m_szBlendShapeNames

        while (buf.Count < 0x60) buf.Add(0);

        // IslandDrawParams (16 bytes):
        // word[0] = startIndex
        // word[1] = indexCount
        // word[2] = primitive type (0x05000000 in real mesh PSGs)
        // word[3] = 0 (its first 2 bytes are remap[0] due m_pRemapTable = +0x6C)
        buf.AddRange(BeU32(0)); // word[0] startIndex
        buf.AddRange(BeU32(numIndices)); // word[1] indexCount
        buf.AddRange(BeU32(0x05000000));
        buf.AddRange(BeU32(0));

        return buf.ToArray();
    }
}

