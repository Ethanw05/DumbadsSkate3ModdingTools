namespace ArenaBuilder.Core.Psg;

/// <summary>
/// On-disk arena cross-references (WPLAYER, TOC <c>m_pObject</c>, mesh encoded fields, etc.) use the same packed form
/// that <c>pegasus::tWorldPainterLayerData::Fixup</c> decodes: row byte offset <c>(encoded &gt;&gt; 20) &amp; 0xFFC</c> and
/// combined slot index <c>(encoded &amp; 0x3FFFFF) + ((2 * encoded) &amp; 0x7FFFFE)</c> (see documentation/PSG_STRUCTURE_CONNECTIONS_IDA_VERIFICATION.md).
/// Per-type object ordinals are stored in the low bits; each logical dictionary object occupies three 8-byte words in the type row (stride 24).
/// </summary>
public static class ArenaDictionaryEncodedPointer
{
    /// <summary>Matches IDA Fixup combined index term (unsigned 32-bit).</summary>
    public static uint DecodeCombinedIndex(uint encoded) =>
        (encoded & 0x3FFFFF) + ((2 * encoded) & 0x7FFFFE);

    /// <summary>
    /// Encodes a reference to the <paramref name="perTypeOrdinal"/>-th object of registry type index <paramref name="typeIndex"/>
    /// (0-based ordinal among objects sharing that <see cref="PsgBinary.PsgObject.TypeIndex"/>).
    /// </summary>
    public static uint Encode(int typeIndex, int perTypeOrdinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(typeIndex);
        if (typeIndex > 0x3FF)
            throw new ArgumentOutOfRangeException(nameof(typeIndex), typeIndex, "typeIndex out of encoded range.");
        ArgumentOutOfRangeException.ThrowIfNegative(perTypeOrdinal);
        if (perTypeOrdinal > 0xFFFFF)
            throw new ArgumentOutOfRangeException(nameof(perTypeOrdinal), perTypeOrdinal, "perTypeOrdinal exceeds low 20 bits.");

        uint rowByte = (uint)((typeIndex * 4) & 0xFFC);
        return (rowByte << 20) | (uint)perTypeOrdinal;
    }

    /// <summary>
    /// Resolves an on-disk value to a global dictionary object index. Tries arena encoding first when the high bits
    /// suggest it; falls back to legacy raw global indices (older writers) when <paramref name="encoded"/> &lt; count
    /// and points at <paramref name="expectedTypeId"/>.
    /// </summary>
    public static bool TryDecodeObjectIndex(
        uint encoded,
        uint expectedTypeId,
        IReadOnlyList<PsgBinary.PsgObject> objects,
        out int globalIndex)
    {
        globalIndex = -1;
        if (objects.Count == 0)
            return false;

        // Prefer raw global dict index when in range — matches WorldPainterPsgBuilder default output and older tools.
        // Packed arena refs use high (>>20) bits; those are always >= object count in practice.
        if (encoded < (uint)objects.Count && objects[(int)encoded].TypeId == expectedTypeId)
        {
            globalIndex = (int)encoded;
            return true;
        }

        return TryDecodeStructured(encoded, expectedTypeId, objects, out globalIndex);
    }

    private static bool TryDecodeStructured(
        uint encoded,
        uint expectedTypeId,
        IReadOnlyList<PsgBinary.PsgObject> objects,
        out int globalIndex)
    {
        globalIndex = -1;
        uint rowByte = (encoded >> 20) & 0xFFC;
        uint ti = rowByte / 4;
        if (rowByte != ti * 4)
            return false;

        uint combined = DecodeCombinedIndex(encoded);
        if (combined % 3 != 0)
            return false;
        int ord = (int)(combined / 3);

        int seen = 0;
        for (int i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            if (o.TypeIndex != ti || o.TypeId != expectedTypeId)
                continue;
            if (seen == ord)
            {
                globalIndex = i;
                return true;
            }
            seen++;
        }

        return false;
    }
}
