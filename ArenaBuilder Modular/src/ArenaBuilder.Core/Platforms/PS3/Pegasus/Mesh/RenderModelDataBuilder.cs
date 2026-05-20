using System.Text;
using static ArenaBuilder.Core.BinaryEncoding.BinaryEncodingHelpers;

namespace ArenaBuilder.Core.Platforms.PS3.Pegasus.Mesh;

/// <summary>
/// RenderModelData object (0x00EB0001) for static mesh PSGs.
/// Layout per real dumps (F72E84DEFF51F7BF, FA6082BFC0DBAD11, EC4263D1E7B8E149, FD97267B138CAF7C):
/// - 0x00..0x3F: tRModelData header (m_BBox, pointers, m_iNumTotalBones/m_iNumMeshes/m_iNumBones/m_iNumIslands)
/// - 0x40..0x7F: one IBP matrix (identity)
/// - 0x80..:     mesh table (8 bytes per mesh: word0=dict index to RenderOptiMeshData, word1=0)
/// - next:       bone name table (4 bytes per bone; numBones entries, not numMeshes; static meshes use numBones=1)
/// - next:       island AABBs (32 bytes per island: Vector4 min, Vector4 max)
/// - next:       island area vectors (16 bytes per island: extent X,Y,Z,0)
/// </summary>
public static class RenderModelDataBuilder
{
    /// <summary>
    /// Real single-mesh table: word0=7 (dict index), word1=0. Multi-mesh: 7+i*7 for mesh i.
    /// </summary>
    private const uint MeshTableWord0SingleMesh = 7;
    private const uint MeshTableWord1SingleMesh = 0;

    public const uint MeshTableOffset = 0x80;
    public const uint BoneNameTableOffset = 0x88;
    public const uint BoneNameListOffset = 0x8C;
    public const uint IslandAabbsOffset = 0xA0;
    public const uint IslandAreasOffset = 0x120;

    /// <summary>
    /// Computes island AABB/area offsets for multi-mesh. Per island i: AABB at islandAabbsOff + i*32, Area at islandAreasOff + i*16.
    /// Real dumps pad the AABB block: 1 island 0xA0→0x120 (4 slots), 2 islands 0xB0→0x130 (4 slots), 15 islands 0xF0→0x2F0 (16 slots).
    /// Formula: numAabbSlots = max(4, roundUpToPowerOf2(numIslands)); islandAreasOff = islandAabbsOff + 32*numAabbSlots.
    /// </summary>
    public static (uint IslandAabbsOff, uint IslandAreasOff) ComputeOffsetsForNumMeshes(int numMeshes, int numIslands)
    {
        const int numBones = 1;
        uint boneNameTableOff = (uint)(0x80 + 8 * numMeshes);
        uint boneNameListOff = boneNameTableOff + (uint)(4 * numBones);
        byte[] boneNameBytes = Encoding.ASCII.GetBytes("SP_Root\0");
        uint islandAabbsOff = (uint)(boneNameListOff + boneNameBytes.Length + 15) & ~15u;
        int numAabbSlots = Math.Max(4, RoundUpToPowerOf2(numIslands));
        uint islandAreasOff = islandAabbsOff + (uint)(32 * numAabbSlots);
        return (islandAabbsOff, islandAreasOff);
    }

    private static int RoundUpToPowerOf2(int n)
    {
        if (n <= 1) return Math.Max(1, n);
        n--;
        n |= n >> 1;
        n |= n >> 2;
        n |= n >> 4;
        n |= n >> 8;
        n |= n >> 16;
        return n + 1;
    }

    private const int TemplateLikeSize = 0x160;

    /// <summary>
    /// Single-mesh build. Produces 352 bytes matching real single-mesh dumps.
    /// </summary>
    public static byte[] Build(
        (float X, float Y, float Z) bboxMin,
        (float X, float Y, float Z) bboxMax)
    {
        return Build(bboxMin, bboxMax, 1, new[] { (int)MeshTableWord0SingleMesh }, 1,
            new[] { (bboxMin, bboxMax) },
            new[] { (Math.Max(bboxMax.X - bboxMin.X, 0.001f), Math.Max(bboxMax.Y - bboxMin.Y, 0.001f), Math.Max(bboxMax.Z - bboxMin.Z, 0.001f)) });
    }

    /// <summary>
    /// Multi-mesh build. Per real dumps: mesh table at 0x80, bone table after, island AABBs/areas after.
    /// meshTableDictIndices: dict index per mesh (real: 7, 14, 21, ... for mesh 0,1,2,...).
    /// islandAabbs: (min, max) per island. islandAreas: (extentX, extentY, extentZ) per island.
    /// </summary>
    public static byte[] Build(
        (float X, float Y, float Z) bboxMin,
        (float X, float Y, float Z) bboxMax,
        int numMeshes,
        IReadOnlyList<int> meshTableDictIndices,
        int numIslands,
        IReadOnlyList<((float X, float Y, float Z) Min, (float X, float Y, float Z) Max)> islandAabbs,
        IReadOnlyList<(float X, float Y, float Z)> islandAreas)
    {
        if (meshTableDictIndices == null || meshTableDictIndices.Count != numMeshes)
            throw new ArgumentException("meshTableDictIndices must have numMeshes entries.", nameof(meshTableDictIndices));
        if (islandAabbs == null || islandAabbs.Count != numIslands)
            throw new ArgumentException("islandAabbs must have numIslands entries.", nameof(islandAabbs));
        if (islandAreas == null || islandAreas.Count != numIslands)
            throw new ArgumentException("islandAreas must have numIslands entries.", nameof(islandAreas));

        // Offsets per real dumps: BoneNameTable = 0x80 + 8*numMeshes, BoneNameList = BoneNameTable + 4*numBones.
        // Bone name table has numBones entries (static meshes: numBones=1), NOT numMeshes.
        const int numBones = 1; // Static mesh PSGs always use 1 bone; all meshes share the same bone name.
        uint boneNameTableOff = (uint)(0x80 + 8 * numMeshes);
        uint boneNameListOff = boneNameTableOff + (uint)(4 * numBones);
        byte[] boneNameBytes = Encoding.ASCII.GetBytes("SP_Root\0");
        uint islandAabbsOff = (uint)(boneNameListOff + boneNameBytes.Length + 15) & ~15u;
        int numAabbSlots = Math.Max(4, RoundUpToPowerOf2(numIslands));
        uint islandAreasOff = islandAabbsOff + (uint)(32 * numAabbSlots);

        var buf = new List<byte>();

        // tAABB (m_BBox) - combined bounds
        buf.AddRange(BeF32(bboxMin.X));
        buf.AddRange(BeF32(bboxMin.Y));
        buf.AddRange(BeF32(bboxMin.Z));
        buf.AddRange(BeF32(0));
        buf.AddRange(BeF32(bboxMax.X));
        buf.AddRange(BeF32(bboxMax.Y));
        buf.AddRange(BeF32(bboxMax.Z));
        buf.AddRange(BeF32(0));

        buf.AddRange(BeU32(0x40));
        buf.AddRange(BeU32(MeshTableOffset));
        buf.AddRange(BeU32(boneNameTableOff));
        buf.AddRange(BeU32(boneNameListOff));

        buf.AddRange(BeU16((ushort)numMeshes)); // m_iNumTotalBones = numMeshes per real dumps
        buf.AddRange(BeU16((ushort)numMeshes));
        buf.AddRange(BeU16(1));
        buf.AddRange(BeU16((ushort)numIslands));
        buf.AddRange(BeU32(islandAabbsOff));
        buf.AddRange(BeU32(islandAreasOff));

        while (buf.Count < 0x40) buf.Add(0);

        AddIdentityMatrix(buf);
        while (buf.Count < MeshTableOffset) buf.Add(0);

        // Mesh table: 8 bytes per mesh (word0=dict index, word1=0)
        for (int i = 0; i < numMeshes; i++)
        {
            buf.AddRange(BeU32((uint)meshTableDictIndices[i]));
            buf.AddRange(BeU32(MeshTableWord1SingleMesh));
        }

        // Bone name table: numBones entries, each points to same string (static meshes: 1 entry)
        for (int i = 0; i < numBones; i++)
            buf.AddRange(BeU32(boneNameListOff));

        buf.AddRange(boneNameBytes);
        while (buf.Count < islandAabbsOff) buf.Add(0);

        // Island AABBs: 32 bytes each (Vector4 min, Vector4 max). Real dumps pad to numAabbSlots.
        for (int i = 0; i < numIslands; i++)
        {
            var (min, max) = islandAabbs[i];
            buf.AddRange(BeF32(min.X));
            buf.AddRange(BeF32(min.Y));
            buf.AddRange(BeF32(min.Z));
            buf.AddRange(BeF32(0));
            buf.AddRange(BeF32(max.X));
            buf.AddRange(BeF32(max.Y));
            buf.AddRange(BeF32(max.Z));
            buf.AddRange(BeF32(0));
        }
        while (buf.Count < islandAreasOff) buf.Add(0);

        // Island area vectors: (extentX, extentY, extentZ, 0) per real dumps
        for (int i = 0; i < numIslands; i++)
        {
            var a = islandAreas[i];
            buf.AddRange(BeF32(a.X));
            buf.AddRange(BeF32(a.Y));
            buf.AddRange(BeF32(a.Z));
            buf.AddRange(BeF32(0));
        }

        // Single-mesh: pad to 0x160 to match template
        if (numMeshes == 1 && numIslands == 1)
        {
            while (buf.Count < TemplateLikeSize) buf.Add(0);
        }

        return buf.ToArray();
    }

    private static void AddIdentityMatrix(List<byte> buf)
    {
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
                buf.AddRange(BeF32(row == col ? 1f : 0f));
        }
    }
}

