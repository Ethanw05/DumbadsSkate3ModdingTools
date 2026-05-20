using ArenaBuilder.Core;
using ArenaBuilder.Texture.Dds;
using System.IO.Hashing;
using System.Text;

namespace ArenaBuilder.Texture;

/// <summary>
/// Canonical texture key and GUID derivation for the Skate 3 PS3 streamer.
///
/// <para>
/// <b>Engine fallback rule (empirically verified against stock <c>DLC_DW_MegaCompund</c>):</b>
/// the engine pairs a full-resolution texture with a small fallback by toggling
/// <b>bit 62</b> (mask <c>0x4000_0000_0000_0000</c>) of the 64-bit asset GUID.
/// </para>
///
/// <list type="bullet">
///   <item>Bit 62 == 0 → full-resolution copy, lives in <c>cTex_X_Y_high</c>.</item>
///   <item>Bit 62 == 1 → small fallback copy (1/8 linear of full), lives in <c>cPres_U_V_high</c>.</item>
///   <item>Mesh material channels always reference the FULL GUID (bit 62 == 0).</item>
/// </list>
///
/// <para>
/// At render time the engine looks up the channel GUID; if the cTex tile is not currently
/// streamed in, the engine ORs <c>0x4000_0000_0000_0000</c> into the GUID and re-queries,
/// resolving the small fallback the cPres tile already has loaded. (Verified across 70 of 70
/// sibling pairs in the stock dataset; 1247 of 1247 mesh-channel resolutions point at the
/// bit-62-clear member of the pair, never the bit-62-set member.)
/// </para>
/// </summary>
public static class TextureGuidStrategy
{
    /// <summary>
    /// Bit 62 of the 64-bit asset GUID (<c>0x4000_0000_0000_0000</c>): set on the small/cPres
    /// fallback copy, cleared on the full/cTex copy. Mesh channels always reference the
    /// bit-62-clear GUID; the engine retries with this bit set on a primary lookup miss.
    /// </summary>
    public const ulong SmallVariantFlag = 0x4000_0000_0000_0000UL;

    /// <summary>
    /// Canonical full-variant mask for content-addressed GUIDs, matching
    /// BlenRose.py's <c>_tex_guid_from_dedupe_key</c> (<c>&amp; 0xBFFFFFFEFFFFFFFF</c>):
    /// clears bit 62 (the small-variant flag) AND HIDWORD bit 0
    /// (<c>0x0000_0001_0000_0000</c>) — the asset-manager entropy bit BlenRose
    /// keeps clear on every shipped texture GUID.
    /// </summary>
    public const ulong ContentFullGuidMask = 0xBFFF_FFFE_FFFF_FFFFUL;

    /// <summary>
    /// Content-addressed FULL-variant GUID — port of BlenRose.py's
    /// <c>_tex_guid_from_dedupe_key</c> (≈line 4018). The GUID is derived
    /// purely from the ENCODED DDS (format + dimensions + mip count + hash of
    /// the payload), so byte-identical texture content anywhere in the build
    /// collapses to ONE GUID and ONE PSG — true global dedup, independent of
    /// which GLB / material / channel / tile referenced it.
    ///
    /// <para>Key format mirrors <see cref="TextureDeduplicationRegistry.BuildDedupeKey"/>
    /// with a null scope: <c>{fmt:X2}|{w}|{h}|{mips}|{xxhash128(payload) hex}</c>.
    /// BlenRose uses SHA-256 for the payload hash; we keep XxHash128 (already
    /// the registry's hash, ~10-20× faster) — cross-tool GUID parity with
    /// BlenRose is NOT a goal, only deterministic in-build content addressing.</para>
    /// </summary>
    public static ulong ContentAddressedFullGuid(DdsTextureInput dds)
    {
        if (dds == null) return 0;
        Span<byte> hash = stackalloc byte[16];
        XxHash128.Hash(dds.Payload, hash);
        string hashHex = Convert.ToHexString(hash).ToLowerInvariant();
        string key = $"{dds.Ps3Format:X2}|{dds.Width}|{dds.Height}|{dds.MipCount}|{hashHex}";
        return Lookup8Hash.HashString(key) & ContentFullGuidMask;
    }

    /// <summary>
    /// Builds a stable, collision-resistant texture key from GLB/material binding context.
    /// Format: "&lt;glbFileStem&gt;/&lt;materialName&gt;/&lt;channelName&gt;/&lt;imageName&gt;".
    /// All components are normalized to ASCII for Lookup8Hash.
    /// </summary>
    /// <param name="glbFileStem">GLB filename without extension (e.g. "MyLevel").</param>
    /// <param name="materialName">Material name in the asset (e.g. "Material0").</param>
    /// <param name="channelName">Shader channel (e.g. "diffuse", "normal").</param>
    /// <param name="imageName">Image/sampler name or embedded image key (e.g. "tex_0", "image0").</param>
    /// <returns>Canonical key string; use <see cref="KeyToGuid"/> to get the 64-bit GUID.</returns>
    public static string BuildTextureKey(
        string? glbFileStem,
        string? materialName,
        string? channelName,
        string? imageName)
    {
        string a = NormalizeToAscii(glbFileStem ?? "");
        string b = NormalizeToAscii(materialName ?? "");
        string c = NormalizeToAscii(channelName ?? "");
        string d = NormalizeToAscii(imageName ?? "");
        return $"{a}/{b}/{c}/{d}";
    }

    /// <summary>
    /// Computes the canonical 64-bit FULL-variant GUID from the texture key. Bit 62 is forced
    /// clear so this GUID is always safe to use as a mesh-channel reference: the engine will
    /// retry the lookup with bit 62 set on a primary miss to find a small fallback.
    /// </summary>
    public static ulong KeyToGuid(string textureKey)
    {
        if (string.IsNullOrEmpty(textureKey))
            return 0;

        ulong hash = Lookup8Hash.HashString(textureKey);
        return MakeFullVariantGuid(hash);
    }

    /// <summary>
    /// Computes the FE location-texture GUID exactly as EBOOT does for menu thumbnails:
    /// 64-bit FNV-1a over "<paramref name="textureBaseName"/>.Texture" with offset basis
    /// <c>0xCBF29CE484222325</c> and prime <c>0x100000001B3</c>.
    /// </summary>
    /// <param name="textureBaseName">Base texture token without extension (e.g. "DLC_Location_Danny" or "map1024").</param>
    /// <returns>Runtime-compatible TOC <c>m_uiGuid</c> value.</returns>
    public static ulong FeLocationBaseNameToGuid(string textureBaseName)
    {
        if (string.IsNullOrEmpty(textureBaseName))
            return 0;

        return Fnv1a64(textureBaseName + ".Texture");
    }

    /// <summary>
    /// Returns the small-fallback (cPres) variant of <paramref name="fullGuid"/> by setting bit 62.
    /// Idempotent: passing an already-small GUID returns the same value.
    /// </summary>
    public static ulong MakeSmallVariantGuid(ulong fullGuid) =>
        fullGuid | SmallVariantFlag;

    /// <summary>
    /// Returns the full-resolution (cTex) variant of <paramref name="guid"/> by clearing bit 62.
    /// Use when constructing mesh-channel references so the engine can perform its full→small
    /// fallback retry path.
    /// </summary>
    public static ulong MakeFullVariantGuid(ulong guid) =>
        guid & ~SmallVariantFlag;

    /// <summary>True iff <paramref name="guid"/> is a small/cPres variant (bit 62 set).</summary>
    public static bool IsSmallVariantGuid(ulong guid) =>
        (guid & SmallVariantFlag) != 0;

    /// <summary>
    /// TOC name string observed in real texture PSGs: "0x&lt;guidLowerHex&gt;.Texture" or "0x...T".
    /// Dumps show "0x054a480902fd9a8d.Texture" (template) and "0x2c70170a000d002a.Texture" (real);
    /// some use ".T" suffix. We use ".Texture" for readability; game looks up by GUID.
    /// </summary>
    public static string GuidToTocNameString(ulong guid)
    {
        return $"0x{guid:x16}.Texture";
    }

    /// <summary>
    /// Normalizes a string to ASCII so Lookup8Hash.HashString does not throw.
    /// Non-ASCII characters are replaced with '?'.
    /// </summary>
    private static string NormalizeToAscii(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
            sb.Append(c <= 0x7F ? c : '?');
        return sb.ToString();
    }

    private static ulong Fnv1a64(string value)
    {
        const ulong offsetBasis = 0xCBF29CE484222325UL;
        const ulong prime = 0x100000001B3UL;

        string ascii = NormalizeToAscii(value);
        ulong hash = offsetBasis;
        for (int i = 0; i < ascii.Length; i++)
        {
            hash *= prime;
            hash ^= (byte)ascii[i];
        }

        return hash;
    }
}
