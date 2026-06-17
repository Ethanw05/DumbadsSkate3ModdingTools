namespace ArenaBuilder.Texture;

/// <summary>
/// Minimal constants and layout for PS3 texture PSGs, inventoried from
/// template_Texture_psg_dumped.txt and real_texture_1A8211DFC2D00A95_dumped.txt.
/// Use these when building texture PSGs so output matches game expectations.
/// </summary>
public static class TexturePsgConstants
{
    // --- Arena / Dictionary ---
    /// <summary>Type ID for BaseResource (PS3 texture payload).</summary>
    public const uint TypeIdBaseResource = 0x00010034;

    /// <summary>Type ID for the 40-byte Texture (TextureInformationPS3) object.</summary>
    public const uint TypeIdTexture = 0x000200E8;

    /// <summary>Type ID for TableOfContents in texture PSG.</summary>
    public const uint TypeIdTableOfContents = 0x00EB000B;

    /// <summary>Type ID for VersionData.</summary>
    public const uint TypeIdVersionData = 0x00EB0008;

    /// <summary>Type registry for texture PSG (order matches template). Same 10 types as template; we use indices for BaseResource(5), Texture(7), TOC(9), Version(8).</summary>
    public static readonly uint[] TextureTypeRegistry =
    {
        0x00000000, 0x00010030, 0x00010031, 0x00010032, 0x00010033, 0x00010034,
        0x00010010, 0x000200E8, 0x00EB0008, 0x00EB000B
    };

    // --- TOC entry (single texture) ---
    /// <summary>Constant marker in TOC entry (observed in all texture dumps).</summary>
    public const uint TocEntryMarker = 0x9B0F1678;

    /// <summary>TOC entry type for texture (cross-file lookup key).</summary>
    public const uint TocEntryTypeTexture = 0xAC462E4A;

    /// <summary>m_pObject for single texture: points to ArenaDictionary Entry[1] (Texture object).</summary>
    public const uint TocEntryObjectPointer = 0x00000001;

    // --- Texture object (40 bytes, TextureInformationPS3) ---
    /// <summary>Unknown word observed in all dumps (e.g. 0x0000AAE4).</summary>
    public const uint TextureRemap = 0x0000AAE4;

    /// <summary>Dimension 2D.</summary>
    public const byte TextureDimension2D = 2;

    /// <summary>Depth 1 for 2D textures.</summary>
    public const ushort TextureDepth2D = 1;

    /// <summary>storeType observed (2).</summary>
    public const uint TextureStoreType = 2;

    /// <summary>Unknown ushort at offset +0x24 in Texture object (0x5572).</summary>
    public const ushort TextureUnknown0x5572 = 0x5572;

    // --- VersionData ---
    public const uint VersionDataVersion = 0x19;
    /// <summary>Default revision for world-stream / mesh texture PSGs (tooling template).</summary>
    public const uint VersionDataRevision = 0x0D;

    /// <summary>
    /// Revision observed on stock FE location thumbnails (e.g. Danny Way <c>DLC_Location_Danny.rps3</c>).
    /// </summary>
    public const uint VersionDataRevisionFeLocation = 0x02;

    // --- PS3 format bytes (DDS FourCC → PS3 format byte) ---
    public const byte FormatDxt1 = 0x86;
    public const byte FormatDxt1Alt = 0x81;
    public const byte FormatDxt3 = 0x87;
    public const byte FormatDxt5 = 0x88;
}
