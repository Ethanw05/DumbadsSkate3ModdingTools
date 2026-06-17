using System.Buffers.Binary;

namespace ArenaBuilder.Texture.Xbox;

/// <summary>
/// Builds the 0x34-byte X360 texture-information struct (RW type 0x000200E8).
///
/// Layout (verified byte-for-byte against stock cTex .rx2): seven big-endian u32 followed by the
/// 24-byte GPUTEXTURE_FETCH_CONSTANT:
///   [0x00] 0x00000003   [0x04] 0x00000001   [0x08] 0   [0x0C] 0   [0x10] 0
///   [0x14] 0xFFFF0000    [0x18] 0xFFFF0000   [0x1C] fetch constant (24 B)
/// The leading template fields are D3DBaseTexture runtime state the loader overwrites; only the
/// fetch constant carries texture description. Matches rw4_writer.py _build_ti XBOX branch.
/// </summary>
public static class XboxTextureInfoBuilder
{
    public const int Size = 0x34;
    private const int FetchConstantSize = 0x18;

    public static byte[] Build(byte[] fetchConstant)
    {
        if (fetchConstant == null) throw new ArgumentNullException(nameof(fetchConstant));
        if (fetchConstant.Length != FetchConstantSize)
            throw new ArgumentException($"Fetch constant must be {FetchConstantSize} bytes, got {fetchConstant.Length}.", nameof(fetchConstant));

        var buf = new byte[Size];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x00, 4), 0x00000003);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x04, 4), 0x00000001);
        // 0x08, 0x0C, 0x10 stay zero.
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x14, 4), 0xFFFF0000);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0x18, 4), 0xFFFF0000);
        fetchConstant.CopyTo(s.Slice(0x1C, FetchConstantSize));
        return buf;
    }
}
