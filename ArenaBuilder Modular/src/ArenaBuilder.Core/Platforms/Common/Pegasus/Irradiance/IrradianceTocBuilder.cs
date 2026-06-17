using System.Buffers.Binary;

namespace ArenaBuilder.Core.Platforms.Common.Pegasus.Irradiance;

/// <summary>
/// Builds the <see cref="RwTypeIds.TableOfContents"/> (0x00EB000B) object that every
/// IrradianceData PSG carries. Without it cAssetActivationManager never registers the
/// asset and <c>SHLightingMan::AddHullLightProbes</c> never gets called for this hull.
///
/// Layout matches the AIPath TOC (244 B, same header + entry shape + 25-type map);
/// only the entry TypeId (0xEB0024 for IrradianceData) and the TypeMap ordering differ.
/// NOTE: prior comment cited a Python reference's <c>DEFAULT_TOC_TYPEMAP</c>, but the
/// shipped <c>Documentation/skate3_irradiance_addon.py</c> emits ProbeManifest JSON only
/// — no PSG bytes — so any "verified loading in-game" claim was historical/unfounded.
/// In-game verification of THIS TOC layout is still pending:
///   header (20 B): count=1, arrayOff=0x14, namesOff=0x2C, typeCount=25, typeMapOff=0x2C
///   entry[0]  (24 B): NameOrHash=0, marker=0xFEFFFFFF, GUID(64), TypeId=0xEB0024, pObject=1
///   type map (25 × 8 B): each typeId paired with start=1 (collision-style fill).
///
/// Total = 244 B (same total as AIPath TOC — only the entry TypeId and the TypeMap
/// ordering differ).
/// </summary>
public static class IrradianceTocBuilder
{
    private const int  TocHeaderSize  = 0x14;
    private const int  TocEntrySize   = 0x18;
    private const uint TocEntryMarker = 0xFEFFFFFF;

    /// <summary>
    /// Fixed 25-type map preserved verbatim from the reference Python addon's
    /// <c>DEFAULT_TOC_TYPEMAP</c>. Ordering is what the addon shipped with for
    /// the in-game-verified IrradianceData PSG.
    /// </summary>
    private static readonly uint[] TypeMapOrder =
    {
        0xEB0066, 0xEB0005, 0xEB0067, 0xEB0006, 0xEB0001, 0xEB000A, 0xEB0065,
        0xEB0007, 0xEB0069, 0xEB000D, 0xEB006B, 0xEB0019, 0xEB0064, 0xEB0004,
        0xEB0068, 0xEB0009, 0xEB0016, 0xEB0013, 0xEB0014, 0xEB0018, 0xEB0017,
        0xEB0020, 0xEB0024, 0xEB0026, 0xEB0027
    };

    public static byte[] Build(ulong assetGuid)
    {
        const int numItems  = 1;
        const int totalSize = TocHeaderSize + numItems * TocEntrySize + 25 * 8; // 244 B

        var buf = new byte[totalSize];
        var s   = buf.AsSpan();

        uint arrayOffset   = TocHeaderSize;                                  // 0x14
        uint typeMapOffset = (uint)(TocHeaderSize + numItems * TocEntrySize); // 0x2C
        uint namesOffset   = typeMapOffset;

        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x00, 4), numItems);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x04, 4), arrayOffset);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x08, 4), namesOffset);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x0C, 4), (uint)TypeMapOrder.Length);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x10, 4), typeMapOffset);

        int e = TocHeaderSize;
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(e + 0x00, 4), 0u);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(e + 0x04, 4), TocEntryMarker);
        BinaryPrimitives.WriteUInt64BigEndian(s.Slice(e + 0x08, 8), assetGuid);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(e + 0x10, 4), RwTypeIds.IrradianceData);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(e + 0x14, 4), 1u);

        int tm = (int)typeMapOffset;
        for (int i = 0; i < TypeMapOrder.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(tm + i * 8 + 0, 4), TypeMapOrder[i]);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(tm + i * 8 + 4, 4), numItems);
        }

        return buf;
    }
}
