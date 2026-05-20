using ArenaBuilder.Core.Psg;
using System.Buffers.Binary;

namespace ArenaBuilder.Core.Platforms.PS3.Pegasus.WorldPainter;

/// <summary>
/// Builds WorldPainter-style TableOfContents (0x00EB000B).
/// Real WorldPainter TOCs use marker 0xFEFFFFFF and a 25-entry type map.
/// </summary>
public static class WorldPainterTocBuilder
{
    private const int TocHeaderSize = 0x14;
    private const int TocEntrySize = 0x18;
    private const uint TocEntryMarker = 0xFEFFFFFF;
    private const uint SentinelStartIndexAbsent = 0xFFFFFFFF;

    // Fixed 25-item type map observed in real WorldPainter PSGs.
    private static readonly uint[] TypeMapOrder =
    {
        RwTypeIds.RenderMaterialSubRef,
        RwTypeIds.RenderMaterialData,
        RwTypeIds.CollisionMaterialSubRef,
        RwTypeIds.CollisionMaterialData,
        RwTypeIds.RenderModelData,
        RwTypeIds.CollisionModelData,
        RwTypeIds.RollerDescSubRef,
        RwTypeIds.RollerDescData,
        RwTypeIds.InstanceSubRef,
        RwTypeIds.InstanceData,
        RwTypeIds.TriggerInstanceSubRef,
        RwTypeIds.TriggerInstanceData,
        RwTypeIds.SplineSubRef,
        RwTypeIds.SplineData,
        RwTypeIds.LocationDescSubRef,
        RwTypeIds.LocationDescData,
        RwTypeIds.MassiveData,
        RwTypeIds.RainData,
        RwTypeIds.AiPathData,
        RwTypeIds.LionData,
        RwTypeIds.DepthMapData,
        RwTypeIds.SpatialMap,
        RwTypeIds.IrradianceData,
        RwTypeIds.BlobData,
        RwTypeIds.NavPowerData,
    };

    public static byte[] Build(IReadOnlyList<PsgTocEntry> entries)
    {
        if (entries == null)
            throw new ArgumentNullException(nameof(entries));

        int entryCount = entries.Count;
        int entriesBytes = checked(entryCount * TocEntrySize);
        uint arrayOffset = TocHeaderSize;
        uint namesOffset = (uint)(TocHeaderSize + entriesBytes); // no names blob, but real files place this at end-of-entries
        uint typeCount = (uint)TypeMapOrder.Length;
        uint typeMapOffset = namesOffset;
        int totalSize = checked((int)typeMapOffset + TypeMapOrder.Length * 8);

        var buf = new byte[totalSize];
        var s = buf.AsSpan();

        // Header
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x00, 4), (uint)entryCount); // m_uiItemsCount
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x04, 4), arrayOffset);       // m_pArray
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x08, 4), namesOffset);       // m_pNames
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x0C, 4), typeCount);         // m_uiTypeCount
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x10, 4), typeMapOffset);     // m_pTypeMap

        // Entries
        for (int i = 0; i < entryCount; i++)
        {
            var e = entries[i];
            int off = TocHeaderSize + i * TocEntrySize;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(off + 0, 4), e.NameOrHash);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(off + 4, 4), TocEntryMarker);
            BinaryPrimitives.WriteUInt64BigEndian(s.Slice(off + 8, 8), e.Guid);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(off + 16, 4), e.TypeId);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(off + 20, 4), e.ObjectPtr);
        }

        // Type map: start index of first item for each type; absent => 0xFFFFFFFF.
        for (int i = 0; i < TypeMapOrder.Length; i++)
        {
            uint typeId = TypeMapOrder[i];
            uint start = SentinelStartIndexAbsent;
            for (int j = 0; j < entryCount; j++)
            {
                if (entries[j].TypeId == typeId)
                {
                    start = (uint)j;
                    break;
                }
            }

            int off = (int)typeMapOffset + i * 8;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(off + 0, 4), typeId);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(off + 4, 4), start);
        }

        return buf;
    }
}
