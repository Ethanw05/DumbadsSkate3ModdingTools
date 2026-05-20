using ArenaBuilder.Core.Platforms.PS3;
using ArenaBuilder.Core.Psg;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ArenaBuilder.Mesh;

/// <summary>
/// Builds PsgTocSpec for mesh PSG. Per PSG_STRUCTURE_CONNECTIONS.md.
/// TOC: N×Rendermaterialsubref, RenderMaterialData, Instancesubref, Instancedata.
/// m_pObject: 0x00800000|subrefIndex for subrefs, dict index for direct.
/// </summary>
public static class MeshTocBuilder
{
    // Canonical mesh TOC type order observed in real PSGs.
    // Types not present in this file map to m_uiItemsCount.
    private static readonly uint[] CanonicalMeshTocTypes = Ps3RenderWareConstants.CanonicalMeshTocTypes;

    /// <summary>
    /// Builds TOC spec for mesh. materialGuids ordered by material index; instanceGuid for instance.
    /// firstMaterialNameGuid: must match RenderMaterialData Channel[0] (Name) GUID for material 0.
    /// instanceSubrefIndex: subref index for Instancesubref.
    /// materialSubrefIndices: optional explicit subref index per material TOC entry (for multi-mesh: 0,3,6,...).
    /// </summary>
    public static PsgTocSpec Build(
        int numMaterials,
        ulong firstMaterialNameGuid,
        ulong instanceGuid,
        int renderMaterialDictIndex,
        int instanceDataDictIndex,
        int instanceSubrefIndex,
        IReadOnlyList<uint>? materialSubrefIndices = null,
        IReadOnlyList<ulong>? materialNameGuids = null)
    {
        if (materialSubrefIndices != null && materialSubrefIndices.Count != numMaterials)
            throw new ArgumentException("materialSubrefIndices must have one entry per material.", nameof(materialSubrefIndices));

        if (materialNameGuids != null && materialNameGuids.Count != numMaterials)
            throw new ArgumentException("materialNameGuids must have one entry per material.", nameof(materialNameGuids));

        var entries = new List<PsgTocEntry>();

        for (int i = 0; i < numMaterials; i++)
        {
            // Keep TOC material-subref GUID linked to RenderMaterialData Channel[0] (Name) GUID.
            ulong guid;
            if (materialNameGuids != null)
            {
                guid = materialNameGuids[i];
            }
            else if (i == 0)
            {
                guid = firstMaterialNameGuid;
            }
            else
            {
                byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes($"mesh_mat_{i}_{instanceGuid}"));
                guid = BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(0, 8));
            }
            uint materialSubrefIndex = materialSubrefIndices?[i] ?? (uint)i;
            entries.Add(new PsgTocEntry(0, guid, 0x00EB0066, 0x00800000u + materialSubrefIndex));
        }

        byte[] rmHash = MD5.HashData(Encoding.UTF8.GetBytes($"mesh_rmdata_{instanceGuid}"));
        ulong rmGuid = BinaryPrimitives.ReadUInt64BigEndian(rmHash.AsSpan(0, 8));
        entries.Add(new PsgTocEntry(0, rmGuid, 0x00EB0005, (uint)renderMaterialDictIndex));

        entries.Add(new PsgTocEntry(0, instanceGuid, 0x00EB0069, 0x00800000u + (uint)instanceSubrefIndex));

        byte[] idHash = MD5.HashData(Encoding.UTF8.GetBytes($"mesh_instdata_{instanceGuid}"));
        ulong idGuid = BinaryPrimitives.ReadUInt64BigEndian(idHash.AsSpan(0, 8));
        entries.Add(new PsgTocEntry(0, idGuid, 0x00EB000D, (uint)instanceDataDictIndex));

        // Real mesh TypeMap pattern (e.g. FA6082BFC0DBAD11): only Rendermaterialsubref and Rendermaterialdata
        // use first-occurrence index; ALL other types (EB0067 onward) use numItems.
        var firstByType = new Dictionary<uint, uint>();
        for (int i = 0; i < entries.Count; i++)
        {
            uint t = entries[i].TypeId;
            if (!firstByType.ContainsKey(t))
                firstByType[t] = (uint)i;
        }

        uint numItems = (uint)entries.Count;
        const uint Rendermaterialsubref = 0x00EB0066;
        const uint Rendermaterialdata = 0x00EB0005;

        var typeMap = new (uint TypeId, uint StartIndex)[CanonicalMeshTocTypes.Length];
        for (int i = 0; i < CanonicalMeshTocTypes.Length; i++)
        {
            uint typeId = CanonicalMeshTocTypes[i];
            uint startIndex = (typeId == Rendermaterialsubref || typeId == Rendermaterialdata)
                ? (firstByType.TryGetValue(typeId, out uint found) ? found : numItems)
                : numItems;
            typeMap[i] = (typeId, startIndex);
        }

        return new PsgTocSpec
        {
            Entries = entries,
            TypeMap = typeMap
        };
    }
}
