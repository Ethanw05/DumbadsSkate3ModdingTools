namespace ArenaBuilder.Core.Platforms.Xbox;

/// <summary>
/// Xbox 360 (.rx2) specific RW arena constants.
/// Differences vs PS3 (see docs/X360_Port_Deltas.md):
///   - Arena magic platform tag: "xb2\0" instead of "ps3\0".
///   - BaseResource type ID: 0x00010031 (Xbox 360 + Wii) instead of PS3's 0x00010034.
///   - Sections offset: 0xAC instead of 0xC0.
///   - graphics_baseresource_size field at file +0x54 instead of +0x6C.
/// All other RW type IDs (Pegasus / Collision / Graphics) are platform-agnostic and live in
/// <see cref="ArenaBuilder.Core.Platforms.Common.PegasusRwConstants"/> +
/// <see cref="ArenaBuilder.Core.Platforms.Common.RwTypeIds"/>.
/// </summary>
public static class XboxRwConstants
{
    /// <summary>Xbox 360 BaseResource RW type ID (also used by Wii). Stamped in dictionary entries
    /// holding raw vertex/index/texture payload.</summary>
    public const uint BaseResource = 0x00010031u;

    /// <summary>3-byte ASCII platform tag inserted after "\x89RW4" in the arena magic.</summary>
    public static readonly byte[] MagicPlatformTag = { (byte)'x', (byte)'b', (byte)'2', 0x00 };

    /// <summary>File offset of the sections descriptor block (vs 0xC0 on PS3).</summary>
    public const int SectionsOffset = 0xAC;

    /// <summary>File offset of the total-graphics-disposable-size u32 (vs 0x6C on PS3).</summary>
    public const int GraphicsBaseResourceSizeOffset = 0x54;
}
