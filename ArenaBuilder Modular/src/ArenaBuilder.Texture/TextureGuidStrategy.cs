using ArenaBuilder.Core;
using ArenaBuilder.Texture.Dds;
using System.IO.Hashing;
using System.Text;

namespace ArenaBuilder.Texture;

/// <summary>
/// Canonical texture key and GUID derivation for the Skate 3 PS3 streamer.
/// </summary>
public static class TextureGuidStrategy
{
    /// <summary>
    /// Bit 62 of the 64-bit asset GUID. Historically toggled to distinguish a small fallback
    /// copy from the full copy; we no longer emit small fallbacks, but the mask is retained
    /// to clear bit 62 on derived GUIDs so mesh references remain stable against legacy data.
    /// </summary>
    public const ulong FullVariantClearMask = 0x4000_0000_0000_0000UL;

    /// <summary>
    /// Canonical full-variant mask for content-addressed GUIDs: clears bit 62 AND HIDWORD bit 0
    /// (<c>0x0000_0001_0000_0000</c>) — the asset-manager entropy bit kept clear on every
    /// shipped texture GUID.
    /// </summary>
    public const ulong ContentFullGuidMask = 0xBFFF_FFFE_FFFF_FFFFUL;

    /// <summary>
    /// Content-addressed FULL-variant GUID. The GUID is derived purely from the ENCODED DDS
    /// (format + dimensions + mip count + hash of the payload), so byte-identical texture
    /// content anywhere in the build collapses to one GUID and one PSG.
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
    /// </summary>
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
    /// clear so this GUID is always safe to use as a mesh-channel reference.
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
    public static ulong FeLocationBaseNameToGuid(string textureBaseName)
    {
        if (string.IsNullOrEmpty(textureBaseName))
            return 0;

        return Fnv1a64(textureBaseName + ".Texture");
    }

    /// <summary>
    /// Returns the full-resolution variant of <paramref name="guid"/> by clearing bit 62.
    /// </summary>
    public static ulong MakeFullVariantGuid(ulong guid) =>
        guid & ~FullVariantClearMask;

    /// <summary>
    /// TOC name string observed in real texture PSGs: "0x&lt;guidLowerHex&gt;.Texture".
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
