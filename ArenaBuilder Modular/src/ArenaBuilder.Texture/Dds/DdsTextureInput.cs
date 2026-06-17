namespace ArenaBuilder.Texture.Dds;

/// <summary>
/// DDS ingest contract for building PS3 texture PSGs.
/// Accepted formats: DXT1, DXT5 (and optionally DXT3). Mip chain required for compatibility.
/// </summary>
public sealed class DdsTextureInput
{
    /// <summary>Width in pixels (top mip).</summary>
    public int Width { get; init; }

    /// <summary>Height in pixels (top mip).</summary>
    public int Height { get; init; }

    /// <summary>Number of mip levels (at least 1).</summary>
    public int MipCount { get; init; }

    /// <summary>PS3 format byte: 0x86 = DXT1, 0x88 = DXT5, 0x87 = DXT3.</summary>
    public byte Ps3Format { get; init; }

    private byte[] _payload = Array.Empty<byte>();

    /// <summary>Raw texture payload (DDS payload from offset 128: all mips, no DDS header).</summary>
    public byte[] Payload
    {
        get => _payload;
        init => _payload = value ?? Array.Empty<byte>();
    }

    /// <summary>
    /// Drops retained payload bytes after the PSG has been written (dedupe registry no longer needs the mips in memory).
    /// </summary>
    public void ReleasePayloadBytes() => _payload = Array.Empty<byte>();

    /// <summary>
    /// Pitch for the top mip (bytes per row). For DXT1: ((Width+3)/4)*8; for DXT5: ((Width+3)/4)*16.
    /// Used in TextureInformationPS3.
    /// </summary>
    public uint Pitch => Ps3Format == TexturePsgConstants.FormatDxt1 || Ps3Format == TexturePsgConstants.FormatDxt1Alt
        ? (uint)(((Width + 3) / 4) * 8)
        : (uint)(((Width + 3) / 4) * 16);
}
