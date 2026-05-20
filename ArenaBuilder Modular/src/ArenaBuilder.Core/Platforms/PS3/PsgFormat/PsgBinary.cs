using System.Security.Cryptography;

namespace ArenaBuilder.Core.Psg;

/// <summary>
/// Minimal PSG reader for inspection/diffing and tests.
/// </summary>
public sealed class PsgBinary
{
    public required uint ArenaId { get; init; }
    public required uint DictStart { get; init; }
    public required uint SectionsStart { get; init; }
    public required uint FileSizeField { get; init; }
    public required IReadOnlyList<PsgObject> Objects { get; init; }

    public sealed record PsgObject(int Ptr, int Size, uint TypeId, uint Alignment, uint TypeIndex);

    public static PsgBinary Parse(byte[] bytes)
    {
        if (bytes == null) throw new ArgumentNullException(nameof(bytes));
        return Parse(bytes.AsSpan());
    }

    public static PsgBinary Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < PsgFormatConstants.HeaderSize) throw new InvalidOperationException("Not a PSG (too small).");

        uint arenaId = BigEndianReader.U32(bytes, 0x1C);
        uint dictStart = BigEndianReader.U32(bytes, 0x30);
        uint sectionsStart = BigEndianReader.U32(bytes, 0x34);
        uint fileSizeField = BigEndianReader.U32(bytes, 0x44);

        if (dictStart >= (uint)bytes.Length) throw new InvalidOperationException("dictStart out of range.");
        if (sectionsStart >= (uint)bytes.Length) throw new InvalidOperationException("sectionsStart out of range.");

        // Arena header says numUsed at 0x24 (matches python "8" for collision PSGs).
        uint numUsed = BigEndianReader.U32(bytes, 0x24);
        if (numUsed == 0 || numUsed > 0x10000) throw new InvalidOperationException($"Suspicious numUsed={numUsed}.");

        int dictOff = checked((int)dictStart);
        int dictBytes = checked((int)numUsed * PsgFormatConstants.DictEntrySize);
        if (dictOff + dictBytes > bytes.Length) throw new InvalidOperationException("Dictionary exceeds file.");

        var objects = new List<PsgObject>(checked((int)numUsed));
        for (int i = 0; i < numUsed; i++)
        {
            int baseOff = dictOff + i * PsgFormatConstants.DictEntrySize;
            int ptr = checked((int)BigEndianReader.U32(bytes, baseOff + 0));
            int size = checked((int)BigEndianReader.U32(bytes, baseOff + 8));
            uint alignment = BigEndianReader.U32(bytes, baseOff + 12);
            uint typeIndex = BigEndianReader.U32(bytes, baseOff + 16);
            uint typeId = BigEndianReader.U32(bytes, baseOff + 20);
            if (ptr < 0 || size < 0 || ptr + size > bytes.Length)
                throw new InvalidOperationException($"Object {i} out of range: ptr=0x{ptr:X} size={size}.");
            objects.Add(new PsgObject(ptr, size, typeId, alignment, typeIndex));
        }

        return new PsgBinary
        {
            ArenaId = arenaId,
            DictStart = dictStart,
            SectionsStart = sectionsStart,
            FileSizeField = fileSizeField,
            Objects = objects
        };
    }

    public static string Sha256Hex16(ReadOnlySpan<byte> data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}

