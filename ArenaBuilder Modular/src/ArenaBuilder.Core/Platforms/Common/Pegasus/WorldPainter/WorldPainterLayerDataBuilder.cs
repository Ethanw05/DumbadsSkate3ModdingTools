using System.Buffers.Binary;

namespace ArenaBuilder.Core.Platforms.Common.Pegasus.WorldPainter;

/// <summary>
/// Builds pegasus::tWorldPainterLayerData (0x00EB000F): m_pQuadTree, m_pDictionary, m_iLayerTypeGuid (see documentation/WorldPainterData Structs).
/// On disk the first two DWORDs are either raw global dictionary indices (default <see cref="WorldPainterPsgBuilder.WorldPainterPsgBuildOptions.UseArenaEncodedDictionaryRefs"/> false)
/// or packed arena refs per <c>pegasus::tWorldPainterLayerData::Fixup</c> / <see cref="ArenaBuilder.Core.Psg.ArenaDictionaryEncodedPointer"/> when encoding is enabled.
/// </summary>
public static class WorldPainterLayerDataBuilder
{
    public static byte[] Build(uint quadTreeEncodedRef, uint dictionaryEncodedRef, ulong layerTypeGuid)
    {
        var blob = new byte[0x10];
        var s = blob.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x00, 4), quadTreeEncodedRef);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x04, 4), dictionaryEncodedRef);
        BinaryPrimitives.WriteUInt64BigEndian(s.Slice(0x08, 8), layerTypeGuid);
        return blob;
    }
}
