using System.Buffers.Binary;

namespace ArenaBuilder.Texture.Dds;

/// <summary>
/// Reads DDS header and payload for PS3 texture building.
/// Payload starts at offset 128. Supports DXT1 (FourCC 'DXT1') and DXT5 ('DXT5').
/// </summary>
public static class DdsReader
{
    private const uint DdsMagic = 0x20534444; // 'DDS '
    private const int HeaderSize = 128;
    private const int DdsHeaderSize = 124;

    // FourCC (little-endian in file)
    private const uint FourCC_DXT1 = 0x31545844;
    private const uint FourCC_DXT5 = 0x35545844;
    private const uint FourCC_DXT3 = 0x33545844;

    /// <summary>
    /// Expected payload size in bytes for the given format and mip chain.
    /// DXT1: 8 bytes per 4x4 block; DXT5: 16 bytes per 4x4 block.
    /// </summary>
    public static int GetExpectedPayloadSize(int width, int height, int mipCount, bool isDxt1)
    {
        int bytesPerBlock = isDxt1 ? 8 : 16;
        int total = 0;
        int w = width;
        int h = height;
        for (int m = 0; m < mipCount && (w > 0 || h > 0); m++)
        {
            int bw = (w + 3) / 4;
            int bh = (h + 3) / 4;
            if (bw < 1) bw = 1;
            if (bh < 1) bh = 1;
            total += bw * bh * bytesPerBlock;
            w >>= 1;
            h >>= 1;
        }
        return total;
    }

    /// <summary>
    /// Parses DDS and returns a <see cref="DdsTextureInput"/> for building a texture PSG.
    /// Throws if format is not DXT1/DXT5 (or DXT3), header is invalid, or payload size is too small (would cause game to hang on load).
    /// </summary>
    public static DdsTextureInput Read(byte[] ddsFile)
    {
        if (ddsFile == null || ddsFile.Length < HeaderSize)
            throw new ArgumentException("DDS file too small (need at least 128 bytes).", nameof(ddsFile));

        var span = ddsFile.AsSpan();
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(span);
        if (magic != DdsMagic)
            throw new ArgumentException("Invalid DDS magic.", nameof(ddsFile));

        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
        if (headerSize != DdsHeaderSize)
            throw new ArgumentException($"Unexpected DDS header size {headerSize}, expected {DdsHeaderSize}.", nameof(ddsFile));

        // DDS header layout (file offsets):
        // 0x08=dwFlags, 0x0C=dwHeight, 0x10=dwWidth, 0x1C=dwMipMapCount.
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12, 4));
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16, 4));
        uint mipCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(28, 4));
        if (mipCount == 0) mipCount = 1;

        // DDS_PIXELFORMAT at file offset 76 (header offset 72); fourCC at file offset 84
        uint pfSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(76, 4));
        if (pfSize != 32)
            throw new ArgumentException($"Unexpected DDS pixel format size {pfSize}, expected 32.", nameof(ddsFile));
        uint fourCC = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(84, 4));

        byte ps3Format = fourCC switch
        {
            FourCC_DXT1 => TexturePsgConstants.FormatDxt1,
            FourCC_DXT5 => TexturePsgConstants.FormatDxt5,
            FourCC_DXT3 => TexturePsgConstants.FormatDxt3,
            _ => throw new NotSupportedException($"DDS FourCC 0x{fourCC:X8} is not supported; use DXT1, DXT3, or DXT5.")
        };

        int payloadLength = ddsFile.Length - HeaderSize;
        if (payloadLength <= 0)
            throw new ArgumentException("DDS has no payload after header.", nameof(ddsFile));

        bool isDxt1 = (ps3Format == TexturePsgConstants.FormatDxt1 || ps3Format == TexturePsgConstants.FormatDxt1Alt);
        int expectedSize = GetExpectedPayloadSize((int)width, (int)height, (int)mipCount, isDxt1);
        if (payloadLength < expectedSize)
            throw new ArgumentException(
                $"DDS payload too small: got {payloadLength} bytes, need at least {expectedSize} for {width}x{height} mips={mipCount} {(isDxt1 ? "DXT1" : "DXT5")}. Game may hang on load.",
                nameof(ddsFile));

        byte[] payload = new byte[payloadLength];
        ddsFile.AsSpan(HeaderSize, payloadLength).CopyTo(payload);

        return new DdsTextureInput
        {
            Width = (int)width,
            Height = (int)height,
            MipCount = (int)mipCount,
            Ps3Format = ps3Format,
            Payload = payload
        };
    }
}
