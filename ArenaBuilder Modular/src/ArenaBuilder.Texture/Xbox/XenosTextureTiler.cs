using System.Buffers.Binary;
using ArenaBuilder.Texture.Dds;

namespace ArenaBuilder.Texture.Xbox;

/// <summary>
/// Converts a linear DDS (DXT1/DXT3/DXT5) mip chain into the Xbox 360 Xenos GPU tiled layout and
/// builds the 24-byte <c>GPUTEXTURE_FETCH_CONSTANT</c> the engine samples from.
///
/// Direct port of <c>dds_to_x360.py</c> (5C8/RW4-TextureArena-creator-in-python), compressed-format
/// path only — <see cref="DdsReader"/> accepts only BC1/BC2/BC3, which is all Skate 3 DLC uses.
/// Line references in comments point at that script. All multi-byte output is big-endian.
///
/// The fetch-constant generator (<see cref="GenerateHeader"/>) is verified byte-for-byte against the
/// stock <c>DIST_BlackBoxPark</c> 256×256 DXT1 cTex .rx2 (all six DWORDs match exactly).
/// </summary>
public static class XenosTextureTiler
{
    /// <summary>Tiled payload + the 24-byte fetch constant that describes it.</summary>
    public readonly record struct Result(byte[] FetchConstant, byte[] TiledData);

    // PS3 format byte -> (Xenos format index, bytes-per-block). dds_to_x360.py FOURCC_FORMAT_MAP.
    private static (int Idx, int Pitch) FormatInfo(byte ps3Format) => ps3Format switch
    {
        TexturePsgConstants.FormatDxt1    => (18, 8),   // BC1
        TexturePsgConstants.FormatDxt1Alt => (18, 8),
        TexturePsgConstants.FormatDxt3    => (19, 16),  // BC2
        TexturePsgConstants.FormatDxt5    => (20, 16),  // BC3
        _ => throw new NotSupportedException(
            $"Xenos tiler supports DXT1/DXT3/DXT5 only (got PS3 format byte 0x{ps3Format:X2}).")
    };

    /// <summary>Tiles a parsed DDS input and returns the Xenos payload + fetch constant.</summary>
    public static Result Build(DdsTextureInput dds)
    {
        if (dds == null) throw new ArgumentNullException(nameof(dds));
        return Build(dds.Width, dds.Height, Math.Max(1, dds.MipCount), dds.Ps3Format, dds.Payload);
    }

    /// <summary>
    /// Tiles a linear compressed mip chain. <paramref name="linearPayload"/> is the DDS data past the
    /// 128-byte header: mip 0 then mip 1 … packed tightly, 4-bytes-per-block (DXT1) or 16 (DXT3/5).
    /// </summary>
    public static Result Build(int width, int height, int mips, byte ps3Format, byte[] linearPayload)
    {
        if (linearPayload == null) throw new ArgumentNullException(nameof(linearPayload));
        if (mips < 1) mips = 1;

        var (idx, pitch) = FormatInfo(ps3Format);
        const int blockSize = 4;                  // compressed: 4x4 pixel blocks
        bool isWider = width > height;

        // chunk_size: smallest aligned tiled mip size (dds_to_x360.py:413).
        int chunkSize = 4096 * pitch / blockSize;

        // MipAddress + hdr_pitch from mip0 tiled dimensions (dds_to_x360.py:416-429).
        int mip0Tw  = Align(width, 128);
        int mip0Tbw = mip0Tw / blockSize;
        int mip0Tiled = mip0Tbw * (Align(height, 128) / blockSize) * pitch;
        int hdrPitch = mip0Tbw / 8;
        int mipAddress = mip0Tiled / 4096;

        byte[] fetchConstant = GenerateHeader(width, height, mips, idx, hdrPitch, mipAddress, endian: 1, swizzleY: 2);

        // Per-mip geometry (dds_to_x360.py:438-465).
        var mipInfos = new MipInfo[mips];
        for (int level = 0; level < mips; level++)
        {
            int mw = Math.Max(1, width  >> level);
            int mh = Math.Max(1, height >> level);
            int bw = Math.Max(1, (mw + blockSize - 1) / blockSize);
            int bh = Math.Max(1, (mh + blockSize - 1) / blockSize);
            int tbw = Align(mw, 128) / blockSize;
            int tbh = Align(mh, 128) / blockSize;
            bool isPacked = (mw <= 16 || mh <= 16) && mips > 1;
            mipInfos[level] = new MipInfo(level, mw, mh, bw, bh, bw * bh * pitch, tbw, tbh, tbw * tbh * pitch, isPacked);
        }

        MipInfo? firstPacked = null;
        foreach (var m in mipInfos) if (m.IsPacked) { firstPacked = m; break; }
        var packedOffsets = ComputePackedMipOffsets(mipInfos, isWider, blockSize);

        byte[]? packedChunk = firstPacked is { } fp ? new byte[fp.TiledSize] : null;

        var output = new List<byte[]>();
        int srcOffset = 0;
        foreach (var m in mipInfos)
        {
            byte[] srcSlice = Slice(linearPayload, srcOffset, m.RawSize);
            srcOffset += m.RawSize;

            if (!m.IsPacked)
            {
                byte[] tiled = TileLevel(srcSlice, m.Mw, m.Mh, pitch, sxOffset: 0, syOffset: 0,
                    tiledBlockWidth: m.Tbw, tiledBlockHeight: m.Tbh, dst: null)!;
                // Pad up to a whole number of chunks (dds_to_x360.py:484-485).
                if (tiled.Length >= chunkSize && tiled.Length % chunkSize != 0)
                {
                    int padded = Align(tiled.Length, chunkSize);
                    Array.Resize(ref tiled, padded);
                }
                output.Add(tiled);
            }
            else
            {
                var (sx, sy) = packedOffsets[m.Level];
                TileLevel(srcSlice, m.Mw, m.Mh, pitch, sx, sy,
                    firstPacked!.Value.Tbw, firstPacked!.Value.Tbh, packedChunk);
            }
        }
        if (packedChunk != null) output.Add(packedChunk);

        int total = 0;
        foreach (var c in output) total += c.Length;
        var data = new byte[total];
        int o = 0;
        foreach (var c in output) { Buffer.BlockCopy(c, 0, data, o, c.Length); o += c.Length; }

        return new Result(fetchConstant, data);
    }

    private readonly record struct MipInfo(
        int Level, int Mw, int Mh, int Bw, int Bh, int RawSize, int Tbw, int Tbh, int TiledSize, bool IsPacked);

    // dds_to_x360.py:127-139 get_xbox360_tiled_offset.
    private static int GetTiledOffset(int x, int y, int width, int logBpb)
    {
        int alignedWidth = Align(width, 32);
        int macro = ((x >> 5) + (y >> 5) * (alignedWidth >> 5)) << (logBpb + 7);
        int micro = ((x & 7) + ((y & 0xE) << 2)) << logBpb;
        int offset = macro + ((micro & ~0xF) << 1) + (micro & 0xF) + ((y & 1) << 4);
        int address =
            ((offset & ~0x1FF) << 3) +
            ((y & 16) << 7) +
            ((offset & 0x1C0) << 2) +
            (((((y & 8) >> 2) + (x >> 3)) & 3) << 6) +
            (offset & 0x3F);
        return address >> logBpb;
    }

    // dds_to_x360.py:141-182 tile_level. Compressed only -> swaps each 16-bit pair within a block.
    private static byte[]? TileLevel(byte[] src, int width, int height, int pitch,
        int sxOffset, int syOffset, int tiledBlockWidth, int tiledBlockHeight, byte[]? dst)
    {
        const int blockSize = 4;
        int origBw = Math.Max(1, width  / blockSize);
        int origBh = Math.Max(1, height / blockSize);

        int outSize = tiledBlockWidth * tiledBlockHeight * pitch;
        dst ??= new byte[outSize];

        int logBpb = Log2(pitch);

        for (int dy = 0; dy < origBh; dy++)
        {
            for (int dx = 0; dx < origBw; dx++)
            {
                int swz = GetTiledOffset(dx + sxOffset, dy + syOffset, tiledBlockWidth, logBpb);
                int dstIdx = swz * pitch;
                int srcIdx = (dy * origBw + dx) * pitch;
                if (srcIdx + pitch <= src.Length && dstIdx + pitch <= dst.Length)
                {
                    // Endian mode 1: swap each 16-bit pair (block-compressed). dds_to_x360.py:175-177.
                    for (int i = 0; i < pitch; i += 2)
                    {
                        dst[dstIdx + i]     = src[srcIdx + i + 1];
                        dst[dstIdx + i + 1] = src[srcIdx + i];
                    }
                }
            }
        }
        return dst;
    }

    // dds_to_x360.py:299-349 compute_packed_mip_offsets (compressed: block_size=4).
    private static Dictionary<int, (int Sx, int Sy)> ComputePackedMipOffsets(MipInfo[] mips, bool isWider, int blockSize)
    {
        var packed = new List<MipInfo>();
        foreach (var m in mips) if (m.IsPacked) packed.Add(m);
        var offsets = new Dictionary<int, (int, int)>();
        if (packed.Count == 0) return offsets;

        int firstBw = packed[0].Bw;
        int firstBh = packed[0].Bh;

        if (isWider)
        {
            int log2Bh = firstBh > 0 ? Log2(firstBh) : 0;
            for (int pl = 0; pl < packed.Count; pl++)
            {
                int sy = firstBh >> pl;
                int sx = sy == 0 ? (firstBw >> (pl - log2Bh)) : 0;
                offsets[packed[pl].Level] = (sx, sy);
            }
        }
        else
        {
            int firstBwPx = firstBw * blockSize;
            int plTail = firstBwPx >= 4 ? Log2(firstBwPx / 4) + 1 : 0;
            for (int pl = 0; pl < packed.Count; pl++)
            {
                int sx, sy;
                if (pl < plTail)
                {
                    sx = (firstBwPx >> pl) / blockSize;
                    sy = 0;
                }
                else
                {
                    int tailPl = pl - plTail;
                    sy = firstBh >> (tailPl + 1);
                    sx = 0;
                }
                offsets[packed[pl].Level] = (sx, sy);
            }
        }
        return offsets;
    }

    // dds_to_x360.py:15-117 generate_header. Returns 24 bytes (6 DWORDs).
    private static byte[] GenerateHeader(int width, int height, int mips, int dataFormat,
        int hdrPitch, int mipAddress, int endian, int swizzleY)
    {
        // DWORD 0: Tiled=1, Pitch=hdrPitch, Type=2; all clamp/sign/multisample fields zero.
        uint dw0 = (BReverse(1, 1) << 0)
                 | (BReverse((uint)hdrPitch, 9) << 1)
                 | (BReverse(2, 2) << 30);

        // DWORD 1: Endian + DataFormat; BaseAddress and policy fields zero.
        uint dw1 = (BReverse((uint)endian, 2) << 24)
                 | (BReverse((uint)dataFormat, 6) << 26);

        // DWORD 2: packed big-endian directly, NOT bit-reversed.
        uint dw2 = (uint)(((width - 1) & 0x1FFF) | (((height - 1) & 0x1FFF) << 13));

        // DWORD 3: SwizzleZ=3, SwizzleY, SwizzleX=1; filters zero.
        uint dw3 = (BReverse(3, 3) << 19)
                 | (BReverse((uint)swizzleY, 3) << 22)
                 | (BReverse(1, 3) << 25);

        // DWORD 4: MaxMipLevel = mips-1; rest zero.
        uint dw4 = BReverse((uint)Math.Max(0, mips - 1), 4) << 22;

        // DWORD 5: MipAddress, PackedMips=1, Dimension=1.
        uint dw5 = (BReverse((uint)mipAddress, 20) << 0)
                 | (BReverse(1, 1) << 20)
                 | (BReverse(1, 2) << 21);

        var res = new byte[24];
        WriteFinalizedDw(res.AsSpan(0, 4), dw0);
        WriteFinalizedDw(res.AsSpan(4, 4), dw1);
        BinaryPrimitives.WriteUInt32BigEndian(res.AsSpan(8, 4), dw2);
        WriteFinalizedDw(res.AsSpan(12, 4), dw3);
        WriteFinalizedDw(res.AsSpan(16, 4), dw4);
        WriteFinalizedDw(res.AsSpan(20, 4), dw5);
        return res;
    }

    /// <summary>Reverse the low <paramref name="size"/> bits of <paramref name="val"/>. (breverse)</summary>
    private static uint BReverse(uint val, int size)
    {
        uint r = 0;
        for (int i = 0; i < size; i++) { r = (r << 1) | (val & 1u); val >>= 1; }
        return r;
    }

    /// <summary>finalize_xenos_dw: reverse all 32 bits, then write big-endian.</summary>
    private static void WriteFinalizedDw(Span<byte> dst, uint dw)
    {
        uint r = 0;
        for (int i = 0; i < 32; i++) { r = (r << 1) | (dw & 1u); dw >>= 1; }
        BinaryPrimitives.WriteUInt32BigEndian(dst, r);
    }

    private static int Align(int v, int a) => (v + a - 1) & ~(a - 1);

    private static int Log2(int n) // app_log2: floor(log2 n), n a power of two here (pitch 8/16).
    {
        int r = -1;
        while (n > 0) { r++; n >>= 1; }
        return r;
    }

    private static byte[] Slice(byte[] src, int start, int length)
    {
        var buf = new byte[length];
        int avail = Math.Max(0, Math.Min(length, src.Length - start));
        if (avail > 0) Buffer.BlockCopy(src, start, buf, 0, avail);
        return buf;
    }
}
