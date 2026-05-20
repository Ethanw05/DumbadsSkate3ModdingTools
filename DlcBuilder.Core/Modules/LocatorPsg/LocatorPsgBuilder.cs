using ArenaBuilder.Core;
using ArenaBuilder.Core.Psg;

namespace DlcBuilder.Modules.LocatorPsg;

/// Wraps a LocationDescData payload (built by `LocationDescDataBuilder`) in the
/// RW4 PS3 Arena container expected by Skate 3's LocationManager. Produces the
/// final `.psg` file bytes via `ArenaBuilder.Core.Psg.GenericArenaWriter`.
///
/// File contents (object order in the dictionary, matching stock locator psgs):
///   [0] VersionData          (16 bytes, type 0x00EB0008)
///   [1] LocationDescData     (variable, type 0x00EB0009)
///   [2] TableOfContents      (variable, type 0x00EB000B)
///
/// TOC has 2 entries (for the minimal 1-locator case):
///   - LocationDescSubref (type 0x00EB0068, m_pObject=0x00800000, guid = locator GUID)
///   - LocationDescData    (type 0x00EB0009, m_pObject=1 = dict index, guid = stable per-file id)
///
/// Subreferences section has 1 record:
///   { ObjectDictIndex=1 (LocationDescData), OffsetInObject = FirstDescOffset }
///
/// All multi-byte fields BE.
public static class LocatorPsgBuilder
{
    /// RW type IDs declared in the locator psg's type registry section. Order
    /// is significant: GenericArenaWriter emits this list verbatim and the
    /// engine indexes types by position when binding RW objects.
    ///
    /// Extracted byte-for-byte from a working AG DLC_Park/Dist_Zen .psg type
    /// registry (file offset 0xE8..0x1E8, 64 entries, numEntries=0x40). Index 0
    /// must be Null — the engine reserves slot 0 for the null/sentinel type.
    /// Reordering would route locator data to the wrong handler at fixup time.
    ///
    /// Earlier ports shipped a 14-entry subset (just Versiondata, Locationdescdata,
    /// Tableofcontents, Locationdescsubref, BaseresourceStart family). The file
    /// PARSED but the engine's world-content loader (sub_49AB1C / sub_79EF58 →
    /// sub_79EDD4 at 0x79EEAC) skipped PSGs whose type registry didn't declare
    /// the full standard Pegasus type set, and our DLC's world load broke
    /// silently. The full 64-entry registry is required for retail parity.
    public static readonly uint[] LocatorTypeRegistry = new uint[]
    {
        0x00000000u,                                                      // [ 0] Null sentinel
        0x00010030u, 0x00010031u, 0x00010032u, 0x00010033u, 0x00010034u,  // [ 1.. 5] Baseresource family
        0x00010010u,                                                      // [ 6] ArenaLocalAtomTable
        0x00EB0000u, 0x00EB0001u, 0x00EB0003u, 0x00EB0004u, 0x00EB0005u,  // [ 7..11] Pegasus base + RW types
        0x00EB0006u, 0x00EB000Au, 0x00EB000Du, 0x00EB0019u, 0x00EB0007u,  // [12..16]
        0x00EB0008u, 0x00EB000Cu, 0x00EB0009u, 0x00EB000Bu, 0x00EB000Eu,  // [17..21] VersionData=17, LocationDescData=19, TOC=20
        0x00EB0011u, 0x00EB000Fu, 0x00EB0010u, 0x00EB0012u, 0x00EB0022u,  // [22..26]
        0x00EB0013u, 0x00EB0014u, 0x00EB0015u, 0x00EB0016u, 0x00EB001Au,  // [27..31]
        0x00EB001Cu, 0x00EB001Du, 0x00EB001Bu, 0x00EB001Eu, 0x00EB001Fu,  // [32..36]
        0x00EB0021u, 0x00EB0017u, 0x00EB0020u, 0x00EB0024u, 0x00EB0023u,  // [37..41]
        0x00EB0025u, 0x00EB0026u, 0x00EB0027u, 0x00EB0028u, 0x00EB0029u,  // [42..46]
        0x00EB0018u, 0x00EC0010u,                                          // [47..48]
        0x00010000u, 0x00010002u,                                          // [49..50]
        0x000200EBu, 0x000200EAu, 0x000200E9u, 0x00020081u, 0x000200E8u,  // [51..55] Material/asset family
        0x00080002u, 0x00080001u, 0x00080006u, 0x00080003u, 0x00080004u,  // [56..60]
        0x00040006u, 0x00040007u, 0x0001000Fu,                             // [61..63] BitTable @ 63
    };

    /// Build and write a locator `.psg` file.
    public static void Write(string locName, byte[] locDescPayload, ulong locatorGuid, Stream output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locName);
        ArgumentNullException.ThrowIfNull(locDescPayload);
        ArgumentNullException.ThrowIfNull(output);

        byte[] versionData = BuildVersionData();
        byte[] tocPayload = BuildToc(locatorGuid);

        // tLocationDesc[0] sits at LocationDescDataBuilder.FirstDescOffset
        // within the LocationDescData payload. The lone subref record points
        // there so the engine resolves the LocationDescSubref TOC entry.
        var subrefRecords = new List<PsgSubrefRecord>
        {
            new(ObjectDictIndex: 1u, OffsetInObject: (uint)LocationDescDataBuilder.FirstDescOffset),
        };

        var spec = new PsgArenaSpec
        {
            ArenaId = (uint)(Lookup8Hash.HashString(locName) & 0xFFFFFFFFu),
            Objects = new List<PsgObjectSpec>
            {
                new(versionData, 0x00EB0008u),
                new(locDescPayload, 0x00EB0009u),
                new(tocPayload, 0x00EB000Bu),
            },
            TypeRegistry = LocatorTypeRegistry,
            Toc = new PsgTocSpec { Entries = BuildTocEntriesForSpec(locatorGuid, locName) },
            Subrefs = new PsgSubrefSpec(subrefRecords),
            HeaderTypeIdAt0x70 = 1u,
            UseFileSizeAt0x44 = true,
            DictRelocIsZero = false,
            DeferBaseResourceLayout = false,
            CompactTextureSectionLayout = false,
        };

        GenericArenaWriter.Write(spec, output);
    }

    /// Convenience: returns Lookup8 of the locator name as a 64-bit GUID. Used
    /// both as the per-locator m_uiGuid in tLocationDesc and as the matching
    /// TOC entry guid.
    public static ulong ComputeLocatorGuid(string locName) => Lookup8Hash.HashString(locName);

    /// VersionData payload: m_uiVersion=25, m_uiRevision=13, then 8 zero bytes.
    /// Matches values observed in retail DLC locator psg dumps.
    private static byte[] BuildVersionData()
    {
        var buf = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), 25u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), 13u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(8, 8), 0ul);
        return buf;
    }

    /// TOC entries that go into PsgTocSpec. The actual TableOfContents payload
    /// bytes are built by `BuildToc` (lives as a separate RW object); both
    /// must agree on guids and types.
    private static IReadOnlyList<PsgTocEntry> BuildTocEntriesForSpec(ulong locatorGuid, string locName)
    {
        ulong tableGuid = Lookup8Hash.HashString(locName + "::table");
        return new List<PsgTocEntry>
        {
            // Subref entry: m_pObject = 0x00800000 | subrefIndex (subref idx 0).
            // Encoding decoded from a retail DLC locator psg: ((ptr >> 20) & 0xFFC)
            // gives the section type and (ptr & 0x3FFFFF) gives the subref index.
            new(NameOrHash: 0u, Guid: locatorGuid, TypeId: 0x00EB0068u, ObjectPtr: 0x00800000u),
            // Main LocationDescData entry: m_pObject = dict index of the table object.
            new(NameOrHash: 0u, Guid: tableGuid, TypeId: 0x00EB0009u, ObjectPtr: 1u),
        };
    }

    /// Build the TableOfContents RW object payload bytes.
    ///
    /// Layout (matching retail locator psg dumps):
    ///   0x00 m_uiItemsCount       u32 BE = entries.Count
    ///   0x04 m_pArray              u32 BE = 0x14 (entries follow header)
    ///   0x08 m_pNames              u32 BE = entries-end (no name strings)
    ///   0x0C m_uiTypeCount         u32 BE = TypeMap entry count
    ///   0x10 m_pTypeMap            u32 BE = entries-end
    ///   0x14 .. entries (24 bytes each: m_Name=0, m_Marker=0xFEFFFFFF, m_uiGuid, m_Type, m_pObject)
    ///   ...   TypeMap entries (8 bytes each: type_id, item_count)
    private static byte[] BuildToc(ulong locatorGuid)
    {
        // Per-PSG unique table GUID. An earlier hardcoded shared seed
        // ("locator_table_guid_seed") collided across every PSG in a pack and
        // caused the engine to dedupe / fail to register them. Retail PSGs
        // each have a distinct LocationDescData GUID; deriving from the
        // locator-specific GUID guarantees uniqueness per PSG.
        ulong tableGuid = locatorGuid ^ 0x9E3779B97F4A7C15UL;
        const int headerSize = 0x14;
        const int entrySize = 24;

        var entries = new List<(ulong Guid, uint TypeId, uint PObject)>
        {
            (locatorGuid, 0x00EB0068u, 0x00800000u),  // LocationDescSubref
            (tableGuid,   0x00EB0009u, 1u),           // LocationDescData (dict index 1)
        };

        // TypeMap: every retail locator PSG ships this 25-entry registry of
        // supported pegasus content types — verified byte-identical between
        // two unrelated retail DLC locator PSGs. Looks like tool-generated
        // boilerplate (covers far more than this PSG actually contains), but
        // matching shipped DLCs byte-for-byte is the safest bet.
        uint idx = (uint)entries.Count;
        var typeMap = new List<(uint Type, uint Count)>
        {
            (0x00EB0066u, idx),  // RenderMaterialSubref
            (0x00EB0005u, idx),  // RenderMaterialData
            (0x00EB0067u, idx),  // CollisionMaterialSubref
            (0x00EB0006u, idx),  // CollisionMaterialData
            (0x00EB0001u, idx),  // RenderModelData
            (0x00EB000Au, idx),  // CollisionModelData
            (0x00EB0065u, idx),  // RollerDescSubref
            (0x00EB0007u, idx),  // RollerDescData
            (0x00EB0069u, idx),  // InstanceSubref
            (0x00EB000Du, idx),  // InstanceData
            (0x00EB006Bu, idx),  // TriggerInstanceSubref
            (0x00EB0019u, idx),  // TriggerInstanceData
            (0x00EB0064u, idx),  // SplineSubref
            (0x00EB0004u, idx),  // SplineData
            (0x00EB0068u, idx),  // LocationDescSubref
            (0x00EB0009u, idx),  // LocationDescData
            (0x00EB0016u, idx),  // MassiveData
            (0x00EB0013u, idx),  // RainData
            (0x00EB0014u, idx),  // AIPathData
            (0x00EB0018u, idx),  // LionData
            (0x00EB0017u, idx),  // DepthMapData
            (0x00EB0020u, idx),  // SpatialMap
            (0x00EB0024u, idx),  // IrradianceData
            (0x00EB0026u, idx),  // BlobData
            (0x00EB0027u, idx),  // NavpowerData
        };

        int entriesStart = headerSize;
        int entriesEnd = entriesStart + entries.Count * entrySize;
        int typeMapStart = entriesEnd;
        int total = typeMapStart + typeMap.Count * 8;

        var buf = new byte[total];
        Span<byte> span = buf;

        // Header
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x00, 4), (uint)entries.Count);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x04, 4), (uint)entriesStart);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x08, 4), (uint)typeMapStart);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x0C, 4), (uint)typeMap.Count);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x10, 4), (uint)typeMapStart);

        // Entries
        for (int i = 0; i < entries.Count; i++)
        {
            int off = entriesStart + i * entrySize;
            var e = entries[i];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(span.Slice(off + 0x00, 4), 0u);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(span.Slice(off + 0x04, 4), 0xFEFFFFFFu);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(span.Slice(off + 0x08, 8), e.Guid);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(span.Slice(off + 0x10, 4), e.TypeId);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(span.Slice(off + 0x14, 4), e.PObject);
        }

        // TypeMap
        for (int i = 0; i < typeMap.Count; i++)
        {
            int off = typeMapStart + i * 8;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(span.Slice(off + 0x00, 4), typeMap[i].Type);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(span.Slice(off + 0x04, 4), typeMap[i].Count);
        }

        return buf;
    }
}
