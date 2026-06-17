using System.Buffers.Binary;

namespace ArenaBuilder.NavPower;

/// <summary>
/// Retail Skate NavPower tile PSGs use a fixed TableOfContents layout: one entry for
/// <c>0x00EB0027</c> (NavPowerData), marker <c>0xFEFFFFFF</c> (not the mesh/collision
/// <c>0x9B0F1678</c>), and a 25-pair type map ending in NavPower — see
/// <c>cSim_*_high/*.psg</c> dumps (e.g. C6C2971E1B80290F.psg).
/// </summary>
public static class NavPowerNativeTocBuilder
{
    /// <summary>TOC entry marker observed on retail NavPower PSGs (not the mesh/collision TOC marker).</summary>
    public const uint NavPowerTocEntryMarker = 0xFEFFFFFF;

    private const int HeaderSize = 0x14;
    private const int EntrySize = 0x18;
    private const int TotalSize = 244;

    /// <summary>
    /// Fixed type map from retail NavPower TOC: each pair is (typeId, startIndex); all start indices are 1.
    /// </summary>
    private static ReadOnlySpan<uint> TypeMapFlat => // type, index, type, index, ...
    [
        0x00EB0066u, 1, 0x00EB0005u, 1, 0x00EB0067u, 1, 0x00EB0006u, 1, 0x00EB0001u, 1,
        0x00EB000Au, 1, 0x00EB0065u, 1, 0x00EB0007u, 1, 0x00EB0069u, 1, 0x00EB000Du, 1,
        0x00EB006Bu, 1, 0x00EB0019u, 1, 0x00EB0064u, 1, 0x00EB0004u, 1, 0x00EB0068u, 1,
        0x00EB0009u, 1, 0x00EB0016u, 1, 0x00EB0013u, 1, 0x00EB0014u, 1, 0x00EB0018u, 1,
        0x00EB0017u, 1, 0x00EB0020u, 1, 0x00EB0024u, 1, 0x00EB0026u, 1, 0x00EB0027u, 1,
    ];

    /// <summary>
    /// Builds the 244-byte TableOfContents object body matching retail NavPower tiles.
    /// </summary>
    /// <param name="tocAssetGuid">Deterministic asset GUID (e.g. from path + content salt).</param>
    /// <param name="navPowerDictionaryIndex">
    /// Arena dictionary index of the NavPowerData object. Retail order is VersionData (0), NavPower (1), TOC (2).
    /// </param>
    public static byte[] Build(ulong tocAssetGuid, uint navPowerDictionaryIndex = 1)
    {
        var buf = new byte[TotalSize];
        var s = buf.AsSpan();

        const uint arrayRel = 0x14;
        const uint namesAndTypeMapRel = 0x2C;
        const uint typePairCount = 25;

        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(4, 4), arrayRel);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(8, 4), namesAndTypeMapRel);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(12, 4), typePairCount);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(16, 4), namesAndTypeMapRel);

        int entryOff = HeaderSize;
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(entryOff + 0, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(entryOff + 4, 4), NavPowerTocEntryMarker);
        BinaryPrimitives.WriteUInt64BigEndian(s.Slice(entryOff + 8, 8), tocAssetGuid);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(entryOff + 16, 4), ArenaBuilder.Core.Platforms.Common.RwTypeIds.NavPowerData);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(entryOff + 20, 4), navPowerDictionaryIndex);

        int tmOff = (int)namesAndTypeMapRel;
        ReadOnlySpan<uint> flat = TypeMapFlat;
        for (int i = 0; i < flat.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(tmOff + i * 4, 4), flat[i]);

        return buf;
    }
}
