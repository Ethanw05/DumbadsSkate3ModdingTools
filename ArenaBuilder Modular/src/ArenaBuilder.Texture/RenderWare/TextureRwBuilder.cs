using ArenaBuilder.Texture.Dds;
using System.Buffers.Binary;

namespace ArenaBuilder.Texture.RenderWare;

/// <summary>
/// Builds the 40-byte Texture (TextureInformationPS3) object for PS3 texture PSG.
/// Layout matches PSG2DDS TextureInformationPS3; all multi-byte values big-endian.
/// </summary>
public static class TextureRwBuilder
{
    private const int TextureObjectSize = 40;

    /// <summary>
    /// Builds the raw 40-byte texture object from DDS-derived input.
    /// </summary>
    public static byte[] Build(DdsTextureInput input)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));

        var buf = new byte[TextureObjectSize];
        var s = buf.AsSpan();

        // word[0]: format (1), mipmap (1), dimension (1), cubemap (1) -> 0x88 0x01 0x02 0x00 for DXT5 1 mip 2D
        buf[0] = input.Ps3Format;
        buf[1] = (byte)Math.Max(1, input.MipCount);
        buf[2] = TexturePsgConstants.TextureDimension2D;
        buf[3] = 0;

        // word[1]: remap 0x0000AAE4
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(4, 4), TexturePsgConstants.TextureRemap);

        // word[2]: width, height (ushort each, BE)
        BinaryPrimitives.WriteUInt16BigEndian(s.Slice(8, 2), (ushort)input.Width);
        BinaryPrimitives.WriteUInt16BigEndian(s.Slice(10, 2), (ushort)input.Height);

        // word[3]: depth (1), location (0), padding (0) -> 0x00010000
        BinaryPrimitives.WriteUInt16BigEndian(s.Slice(12, 2), TexturePsgConstants.TextureDepth2D);
        buf[14] = 0;
        buf[15] = 0;

        // word[4]: pitch (bytes per row for top mip)
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(16, 4), input.Pitch);

        // word[5], word[6]: offset 0, buffer 0
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(20, 4), 0u);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(24, 4), 0u);

        // word[7]: storeType = 2
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(28, 4), TexturePsgConstants.TextureStoreType);

        // word[8]: storeFlags = 0
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(32, 4), 0u);

        // word[9]: unknown 0x5572, padding2 0, format2 = same as format
        BinaryPrimitives.WriteUInt16BigEndian(s.Slice(36, 2), TexturePsgConstants.TextureUnknown0x5572);
        buf[38] = 0;
        buf[39] = input.Ps3Format;

        return buf;
    }
}
