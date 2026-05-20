using System.Buffers.Binary;
using System.Text;

namespace ArenaBuilder.Texture.RenderWare;

/// <summary>
/// Builds the TableOfContents object for a single-texture PSG.
/// Layout: header (0x14), one entry (0x18) with marker 0x9B0F1678, then NUL-terminated names blob,
/// padded to **8-byte** alignment (not 16). Stock thumbnails (e.g. <c>DLC_Location_Danny.Texture</c>) pack in 72 bytes;
/// longer names must not be truncated — the game reads the TOC name string.
/// </summary>
public static class TextureTocBuilder
{
    private const int TocHeaderSize = 0x14;
    private const int TocEntrySize = 0x18;

    /// <summary>
    /// Builds the TOC blob for one texture. Size scales with <paramref name="overrideName"/> length.
    /// </summary>
    /// <param name="textureGuid">TOC entry m_uiGuid (cross-file identifier).</param>
    /// <param name="overrideName">Optional TOC name string. When null, uses "0x{guid}.Texture".
    /// Pass the file basename (e.g. "milkfactory.Texture") to match real DLC naming convention.</param>
    public static byte[] Build(ulong textureGuid, string? overrideName = null)
    {
        string nameString = overrideName ?? TextureGuidStrategy.GuidToTocNameString(textureGuid);
        byte[] nameBytes = Encoding.ASCII.GetBytes(nameString + "\0");
        uint nameOffset = TocHeaderSize + TocEntrySize; // names start after the single entry

        int minSize = (int)nameOffset + nameBytes.Length;
        // Stock FE thumbnails (e.g. DLC_Location_Danny) pad the TOC blob to **8-byte** bounds
        // (71 bytes used → 72-byte object). 16-byte padding would wrongly inflate those to 80 bytes.
        // Long DLC_Location_* names still extend the blob beyond 72 bytes.
        int tocTotal = (minSize + 7) & ~7;

        var buf = new byte[tocTotal];
        var s = buf.AsSpan();

        // Header
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0, 4), 1);           // m_uiItemsCount
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(4, 4), TocHeaderSize); // m_pArray
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(8, 4), (uint)nameOffset); // m_pNames
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(12, 4), 0);           // m_uiTypeCount
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(16, 4), (uint)tocTotal); // m_pTypeMap (past padded TOC)

        // Entry: m_Name (offset to name), marker, guid, type, m_pObject
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(20, 4), (uint)nameOffset);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(24, 4), TexturePsgConstants.TocEntryMarker);
        BinaryPrimitives.WriteUInt64BigEndian(s.Slice(28, 8), textureGuid);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(36, 4), TexturePsgConstants.TocEntryTypeTexture);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(40, 4), TexturePsgConstants.TocEntryObjectPointer);

        // Full name + padding to tocTotal (zeros)
        nameBytes.CopyTo(s.Slice((int)nameOffset, nameBytes.Length));

        return buf;
    }
}
