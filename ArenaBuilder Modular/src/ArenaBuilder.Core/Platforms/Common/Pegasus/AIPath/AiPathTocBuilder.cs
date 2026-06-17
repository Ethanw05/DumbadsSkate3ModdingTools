using ArenaBuilder.Core.Psg;
using System.Buffers.Binary;

namespace ArenaBuilder.Core.Platforms.Common.Pegasus.AIPath;

/// <summary>
/// Builds the <see cref="RwTypeIds.TableOfContents"/> (0x00EB000B) object that every
/// stock AIPath PSG carries. Without this third object the engine's
/// cAssetActivationManager never registers the AiPathData with PathManager and no
/// AI traffic spawns.
///
/// Layout decoded from stock content (DIST_Industrial/cSim_350_-50_high/
/// A5249F6736ADC979.psg and 20 AIPath PSGs across DLC_DW_MegaCompund tiles).
/// Total size is always 244 bytes: 20 B header + 1 × 24 B entry + 25 × 8 B type map.
///
///   header (20 B):
///     +0x00  u32  m_uiItemsCount   = 1
///     +0x04  u32  m_pArray         = 0x14
///     +0x08  u32  m_pNames         = 0x2C   (no names blob, points at type map start)
///     +0x0C  u32  m_uiTypeCount    = 25
///     +0x10  u32  m_pTypeMap       = 0x2C
///   entry[0] (24 B):
///     +0x00  u32  m_NameOrHash     = 0
///     +0x04  u32  marker           = 0xFEFFFFFF
///     +0x08  u64  m_Guid           = content-hashed per-PSG unique GUID
///     +0x10  u32  m_TypeId         = 0x00EB0014 (AiPathData)
///     +0x14  u32  m_pObject        = 1        (dict index of the Aipathdata)
///   type map (25 × 8 B):
///     for each TypeId in <see cref="TypeMapOrder"/>:
///       u32 typeId, u32 startIndex = m_uiItemsCount (Collision-style fill -- matches
///       byte-for-byte against stock dumps; WorldPainter's "start-index per type"
///       convention is NOT what AIPath PSGs use).
/// </summary>
public static class AiPathTocBuilder
{
    private const int  TocHeaderSize    = 0x14;
    private const int  TocEntrySize     = 0x18;
    private const uint TocEntryMarker   = 0xFEFFFFFF;

    /// <summary>
    /// Fixed 25-type map order observed in every stock AIPath PSG. Identical to
    /// CollisionTocBuilder.TypeMapTypes.
    /// </summary>
    private static readonly uint[] TypeMapOrder =
    {
        0x00EB0014, 0x00EB0066, 0x00EB0005, 0x00EB0067, 0x00EB0006, 0x00EB0001,
        0x00EB000A, 0x00EB0065, 0x00EB0007, 0x00EB0069, 0x00EB000D, 0x00EB006B,
        0x00EB0019, 0x00EB0064, 0x00EB0004, 0x00EB0068, 0x00EB0009, 0x00EB0016,
        0x00EB0013, 0x00EB0018, 0x00EB0017, 0x00EB0020, 0x00EB0024, 0x00EB0026,
        0x00EB0027
    };

    /// <summary>
    /// Build the TOC blob for a single AiPathData object sitting at dictionary
    /// index 1 in the PSG. <paramref name="assetGuid"/> must be unique per PSG
    /// (stock content uses one TOC GUID per cSim tile -- ours is derived from a
    /// content-hashed seed in <see cref="AiPathPsgBuilder"/>).
    /// </summary>
    public static byte[] Build(ulong assetGuid)
    {
        const int numItems  = 1;
        const int totalSize = TocHeaderSize + numItems * TocEntrySize + 25 * 8; // 244 B

        var buf = new byte[totalSize];
        var s   = buf.AsSpan();

        uint arrayOffset   = TocHeaderSize;                            // 0x14
        uint typeMapOffset = (uint)(TocHeaderSize + numItems * TocEntrySize); // 0x2C
        // m_pNames sits at the same offset as m_pTypeMap in every stock AIPath PSG
        // (no separate names blob is emitted).
        uint namesOffset   = typeMapOffset;

        // Header
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x00, 4), numItems);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x04, 4), arrayOffset);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x08, 4), namesOffset);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x0C, 4), (uint)TypeMapOrder.Length);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x10, 4), typeMapOffset);

        // Entry 0 -> AiPathData at dict index 1
        int e = TocHeaderSize;
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(e + 0x00, 4), 0u);                // NameOrHash
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(e + 0x04, 4), TocEntryMarker);
        BinaryPrimitives.WriteUInt64BigEndian(s.Slice(e + 0x08, 8), assetGuid);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(e + 0x10, 4), RwTypeIds.AiPathData);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(e + 0x14, 4), 1u);                // m_pObject = dict idx 1

        // Type map: collision-style "numItems for every type" fill (byte-verified
        // against 20 stock cSim AIPath PSGs in DLC_DW_MegaCompund).
        int tm = (int)typeMapOffset;
        for (int i = 0; i < TypeMapOrder.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(tm + i * 8 + 0, 4), TypeMapOrder[i]);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(tm + i * 8 + 4, 4), numItems);
        }

        return buf;
    }
}
