namespace ArenaBuilder.Texture.Xbox;

/// <summary>
/// Xbox 360 (.rx2) texture PSG constants. Verified against stock <c>DIST_BlackBoxPark</c> cTex .rx2.
/// </summary>
public static class XboxTextureConstants
{
    /// <summary>BaseResource type ID for X360 (and Wii). PS3 uses 0x00010034.</summary>
    public const uint TypeIdBaseResource = 0x00010031;

    /// <summary>
    /// Type registry for an X360 texture arena — 9 entries (PS3 has 10; X360 drops the PS3-only
    /// BaseResource 0x00010034). Order fixes the dictionary type indices: BaseResource=2, Texture=6,
    /// VersionData=7, TOC=8 — all confirmed against stock dict entries.
    /// </summary>
    public static readonly uint[] TextureTypeRegistry =
    {
        0x00000000, 0x00010030, 0x00010031, 0x00010032, 0x00010033,
        0x00010010, 0x000200E8, 0x00EB0008, 0x00EB000B
    };
}
