using ArenaBuilder.Core.Psg;
using System.Buffers.Binary;

namespace ArenaBuilder.Core.Platforms.PS3;

/// <summary>
/// Builds the TableOfContents object for PS3 Pegasus PSGs.
/// Layout: header (0x14), N entries (0x18 each), optional names blob, optional type map.
/// </summary>
public static class DynamicTocBuilder
{
    // Same marker used by texture TOC builder.
    private const uint TocEntryMarker = 0x9B0F1678;
    private const int TocHeaderSize = 0x14;
    private const int TocEntrySize = 0x18;

    public static byte[] Build(PsgTocSpec spec)
    {
        if (spec?.Entries == null)
            throw new ArgumentException("TOC spec must have entries.", nameof(spec));

        int count = spec.Entries.Count;
        int entriesBytes = checked(count * TocEntrySize);

        // Current mesh/collision builders use NameOrHash=0 and do not emit names.
        // If you later want named entries (like texture), extend this to emit a names blob.
        uint namesOffset = 0;
        int namesBytes = 0;

        (uint TypeId, uint StartIndex)[] typeMap = spec.TypeMap ?? DeriveTypeMap(spec.Entries);
        uint typeCount = (uint)typeMap.Length;
        int typeMapBytes = checked(typeMap.Length * 8); // (typeId,u32)+(startIndex,u32)

        uint arrayOffset = TocHeaderSize;
        uint typeMapOffset = (uint)Align4(TocHeaderSize + entriesBytes + namesBytes);
        int totalSize = checked((int)typeMapOffset + typeMapBytes);

        var buf = new byte[totalSize];
        var s = buf.AsSpan();

        // Header
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0, 4), (uint)count);       // m_uiItemsCount
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(4, 4), arrayOffset);       // m_pArray
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(8, 4), namesOffset);       // m_pNames (0 = NULL)
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(12, 4), typeCount);        // m_uiTypeCount
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(16, 4), typeCount == 0 ? 0u : typeMapOffset); // m_pTypeMap

        // Entries
        int entryBase = TocHeaderSize;
        for (int i = 0; i < count; i++)
        {
            var e = spec.Entries[i];
            int off = entryBase + i * TocEntrySize;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(off + 0, 4), e.NameOrHash);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(off + 4, 4), TocEntryMarker);
            BinaryPrimitives.WriteUInt64BigEndian(s.Slice(off + 8, 8), e.Guid);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(off + 16, 4), e.TypeId);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(off + 20, 4), e.ObjectPtr);
        }

        // Type map
        if (typeCount != 0)
        {
            int tmOff = (int)typeMapOffset;
            for (int i = 0; i < typeMap.Length; i++)
            {
                var (typeId, startIndex) = typeMap[i];
                BinaryPrimitives.WriteUInt32BigEndian(s.Slice(tmOff + i * 8 + 0, 4), typeId);
                BinaryPrimitives.WriteUInt32BigEndian(s.Slice(tmOff + i * 8 + 4, 4), startIndex);
            }
        }

        return buf;
    }

    private static (uint TypeId, uint StartIndex)[] DeriveTypeMap(IReadOnlyList<PsgTocEntry> entries)
    {
        // Stable order: first occurrence in entries.
        var first = new Dictionary<uint, uint>();
        for (int i = 0; i < entries.Count; i++)
        {
            uint t = entries[i].TypeId;
            if (!first.ContainsKey(t))
                first[t] = (uint)i;
        }

        var result = new (uint TypeId, uint StartIndex)[first.Count];
        int idx = 0;
        foreach (var kv in first)
            result[idx++] = (kv.Key, kv.Value);
        return result;
    }

    private static int Align4(int n) => (n + 3) & ~3;
}

