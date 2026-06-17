namespace ArenaBuilder.Core.Psg;

/// <summary>
/// Constants used in PSG file format layout and structure.
/// </summary>
public static class PsgFormatConstants
{
    /// <summary>
    /// Size of the PSG file header in bytes (192 bytes / 0xC0).
    /// </summary>
    public const int HeaderSize = 0xC0;

    /// <summary>
    /// Default size of the sections block for mesh PSGs (384 bytes / 0x180).
    /// </summary>
    public const int DefaultSectionsSize = 0x180;

    /// <summary>
    /// Compact size of the sections block for texture PSGs (156 bytes / 0x9C).
    /// </summary>
    public const int CompactTextureSectionsSize = 0x9C;

    /// <summary>
    /// Default offset where objects start in the file (576 bytes / 0x240).
    /// </summary>
    public const int DefaultObjectsStart = 0x240;

    /// <summary>
    /// Size of each dictionary entry in bytes (24 bytes).
    /// </summary>
    public const int DictEntrySize = 24;

    /// <summary>
    /// Size of each subref record in bytes (8 bytes).
    /// </summary>
    public const int SubrefRecordSize = 8;

    /// <summary>
    /// Default alignment for objects and buffers (16 bytes).
    /// </summary>
    public const int Alignment = 16;
}
