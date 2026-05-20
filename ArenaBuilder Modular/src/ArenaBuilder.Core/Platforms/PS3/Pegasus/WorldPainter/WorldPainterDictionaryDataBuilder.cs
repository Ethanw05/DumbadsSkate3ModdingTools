using System.Buffers.Binary;

namespace ArenaBuilder.Core.Platforms.PS3.Pegasus.WorldPainter;

/// <summary>
/// Builds pegasus::tWorldPainterDictionaryData (0x00EB0011), eType = eUInt32.
/// Pointers are blob-relative offsets.
/// </summary>
public static class WorldPainterDictionaryDataBuilder
{
    private const int HeaderSize = 0x14;
    private const int EntryTableRecordSize = 0x08;
    private const int ETypeUInt32 = 0;

    public static byte[] BuildSingleValue(uint value)
    {
        return BuildFromSlots(new[] { new[] { value } });
    }

    public static byte[] BuildFromSlots(IReadOnlyList<IReadOnlyList<uint>> slots)
    {
        if (slots == null || slots.Count == 0)
            throw new ArgumentException("WorldPainter dictionary requires at least one slot.", nameof(slots));

        int totalEntries = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i] == null || slots[i].Count == 0)
                throw new ArgumentException($"Dictionary slot {i} is empty; each slot must contain at least one value.", nameof(slots));
            totalEntries += slots[i].Count;
        }

        int entryTableOffset = HeaderSize;
        int entriesOffset = HeaderSize + slots.Count * EntryTableRecordSize;
        int totalSize = entriesOffset + totalEntries * sizeof(uint);
        var blob = new byte[totalSize];
        var s = blob.AsSpan();

        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x00, 4), (uint)totalEntries); // m_NumTotalEntries
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x04, 4), (uint)slots.Count);  // m_NumEntryTableSlots
        BinaryPrimitives.WriteInt32BigEndian(s.Slice(0x08, 4), ETypeUInt32);          // m_eType
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x0C, 4), (uint)entryTableOffset);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x10, 4), (uint)entriesOffset);

        int runningEntryOffset = entriesOffset;
        for (int i = 0; i < slots.Count; i++)
        {
            int tableOff = entryTableOffset + i * EntryTableRecordSize;
            var bucket = slots[i];

            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(tableOff + 0, 4), (uint)runningEntryOffset); // tEntryTable.m_pEntry
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(tableOff + 4, 4), (uint)bucket.Count);       // tEntryTable.m_iSize

            for (int j = 0; j < bucket.Count; j++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(s.Slice(runningEntryOffset + j * 4, 4), bucket[j]);
            }
            runningEntryOffset += bucket.Count * 4;
        }

        return blob;
    }
}
