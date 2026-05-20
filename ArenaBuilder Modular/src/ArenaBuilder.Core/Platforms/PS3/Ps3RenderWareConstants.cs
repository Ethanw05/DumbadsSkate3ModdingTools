namespace ArenaBuilder.Core.Platforms.PS3;

/// <summary>
/// PS3 Pegasus/PSG constants used when building objects and composing arenas.
/// </summary>
public static class Ps3RenderWareConstants
{
    /// <summary>
    /// Encoded subref base value. Subrefs are encoded as 0x00800000 | subrefIndex.
    /// </summary>
    public const uint SubrefBase = 0x00800000u;

    /// <summary>
    /// Encoded subref for first material slot (RenderMaterialData @ Material[0]).
    /// </summary>
    public const uint MaterialSubref0 = 0x00800000u;

    /// <summary>
    /// Null pointer value (0xFFFFFFFF).
    /// </summary>
    public const uint NullPointer = 0xFFFFFFFFu;

    /// <summary>
    /// Null pointer value for 64-bit fields (0xFFFFFFFFFFFFFFFF).
    /// </summary>
    public const ulong NullPointer64 = 0xFFFFFFFFFFFFFFFFul;

    /// <summary>
    /// Default padding byte value (0xFF).
    /// </summary>
    public const byte PaddingByte = 0xFF;

    /// <summary>
    /// Default channel mask value (0xFFFF).
    /// </summary>
    public const ushort DefaultChannelMask = 0xFFFF;

    /// <summary>
    /// InstanceData component offset (0xC0).
    /// </summary>
    public const int InstanceDataComponentOffset = 0xC0;

    /// <summary>
    /// Collision PSG type registry (64 types).
    /// </summary>
    public static readonly uint[] CollisionTypeRegistry64 =
    {
        0x00000000, 0x00010030, 0x00010031, 0x00010032, 0x00010033, 0x00010034,
        0x00010010, 0x00EB0000, 0x00EB0001, 0x00EB0003, 0x00EB0004, 0x00EB0005,
        0x00EB0006, 0x00EB000A, 0x00EB000D, 0x00EB0019, 0x00EB0007, 0x00EB0008,
        0x00EB000C, 0x00EB0009, 0x00EB000B, 0x00EB000E, 0x00EB0011, 0x00EB000F,
        0x00EB0010, 0x00EB0012, 0x00EB0022, 0x00EB0013, 0x00EB0014, 0x00EB0015,
        0x00EB0016, 0x00EB001A, 0x00EB001C, 0x00EB001D, 0x00EB001B, 0x00EB001E,
        0x00EB001F, 0x00EB0021, 0x00EB0017, 0x00EB0020, 0x00EB0024, 0x00EB0023,
        0x00EB0025, 0x00EB0026, 0x00EB0027, 0x00EB0028, 0x00EB0029, 0x00EB0018,
        0x00EC0010, 0x00010000, 0x00010002, 0x000200EB, 0x000200EA, 0x000200E9,
        0x00020081, 0x000200E8, 0x00080002, 0x00080001, 0x00080006, 0x00080003,
        0x00080004, 0x00040006, 0x00040007, 0x0001000F
    };

    /// <summary>
    /// Canonical mesh TOC type order observed in real PSGs.
    /// </summary>
    public static readonly uint[] CanonicalMeshTocTypes =
    {
        0x00EB0066, // Rendermaterialsubref
        0x00EB0005, // Rendermaterialdata
        0x00EB0067,
        0x00EB0006,
        0x00EB0001,
        0x00EB000A,
        0x00EB0065,
        0x00EB0007,
        0x00EB0069, // Instancesubref
        0x00EB000D, // Instancedata
        0x00EB006B,
        0x00EB0019,
        0x00EB0064,
        0x00EB0004,
        0x00EB0068,
        0x00EB0009,
        0x00EB0016,
        0x00EB0013,
        0x00EB0014,
        0x00EB0018,
        0x00EB0017,
        0x00EB0020,
        0x00EB0024,
        0x00EB0026,
        0x00EB0027
    };
}

