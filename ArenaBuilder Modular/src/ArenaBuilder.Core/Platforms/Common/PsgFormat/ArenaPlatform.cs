namespace ArenaBuilder.Core.Platforms.Common.PsgFormat;

/// <summary>
/// Target platform for an RW4 arena file. Selects header magic, byte offsets, and
/// BaseResource type ID inside <see cref="GeneralArenaBuilder"/>.
///
/// See docs/X360_Port_Deltas.md §1 for the full list of platform deltas.
/// </summary>
public enum ArenaPlatform
{
    /// <summary>PlayStation 3 (.psg). Magic "ps3\0", HeaderSize 0xC0, graphics-size @ +0x6C, BaseResource 0x00010034.</summary>
    Ps3 = 0,

    /// <summary>Xbox 360 (.rx2). Magic "xb2\0", HeaderSize 0xAC, graphics-size @ +0x54, BaseResource 0x00010031.</summary>
    Xbox360 = 1,
}
