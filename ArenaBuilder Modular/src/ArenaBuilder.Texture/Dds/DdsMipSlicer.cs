namespace ArenaBuilder.Texture.Dds;

/// <summary>
/// Produces the small (cPres) texture variant by SLICING a deeper mip out of
/// an already-encoded full DDS mip chain — never by re-encoding.
///
/// <para>This is a port of BlenRose.py's <c>_dds_pick_small_variant</c>
/// (≈line 3973). The previous ArenaBuilder path ran a full second BCn encode
/// at a downscaled target (and a redundant first encode purely to probe the
/// source dimensions). Since the full encode already contains the entire mip
/// chain, the downscaled copy the engine wants for the resident cPres tile is
/// just a byte-range starting at a deeper mip level — zero extra encode
/// work.</para>
///
/// <para>Block sizing matches <see cref="DdsReader.GetExpectedPayloadSize"/>:
/// DXT1 = 8 bytes / 4×4 block, DXT5 (and DXT3) = 16. The full encode upscales
/// NPOT sources to the next power of two before generating the chain, so each
/// mip dimension is exactly <c>baseDim &gt;&gt; level</c> and the prefix-sum
/// offset math is exact.</para>
/// </summary>
public static class DdsMipSlicer
{
    /// <summary>
    /// Try to carve the small variant out of <paramref name="full"/>'s mip
    /// chain.
    ///
    /// <para><paramref name="downscaleFactor"/> is the desired linear
    /// reduction (default 8 → mip level 3, i.e. 512² → 64²). The level
    /// <c>k = ceil(log2(downscaleFactor))</c>. <paramref name="minDim"/>
    /// clamps <c>k</c> down so neither axis of the resulting mip falls below
    /// it (keeps DXT 4×4-block encoding valid and avoids unusably tiny
    /// fallbacks).</para>
    ///
    /// <para>Returns <c>false</c> (and leaves <paramref name="small"/> null)
    /// when no usable deeper mip exists — i.e. the source is single-mip
    /// (lightmaps, pass-through .dds with one level), already small enough
    /// that <c>k</c> clamps to 0, or the chain is shorter than <c>k</c>.
    /// In that case the caller should emit the FULL texture into the cPres
    /// tile under the full GUID (BlenRose's <c>_resolve_small → None</c>
    /// behaviour), not a broken tiny re-encode.</para>
    /// </summary>
    public static bool TrySliceSmallVariant(
        DdsTextureInput full,
        int downscaleFactor,
        int minDim,
        out DdsTextureInput? small)
    {
        small = null;
        if (full == null) return false;
        if (full.MipCount <= 1) return false;          // no deeper mip to take
        if (downscaleFactor < 2) return false;          // not actually a downscale
        if (minDim < 1) minDim = 1;

        // k = ceil(log2(downscaleFactor)). Integer form: smallest k with
        // (1 << k) >= downscaleFactor.
        int k = 0;
        while ((1 << k) < downscaleFactor) k++;

        // Clamp k DOWN while either axis at level k would dip below minDim.
        // (BlenRose: `while src_w>>k < min_dim or src_h>>k < min_dim: k -= 1`.)
        while (k > 0 &&
               ((full.Width >> k) < minDim || (full.Height >> k) < minDim))
        {
            k--;
        }

        // Can't exceed the actual chain length.
        if (k > full.MipCount - 1) k = full.MipCount - 1;

        // k == 0 → the "small" variant would be the full texture; no point.
        if (k <= 0) return false;

        bool isDxt1 = full.Ps3Format == TexturePsgConstants.FormatDxt1
                      || full.Ps3Format == TexturePsgConstants.FormatDxt1Alt;
        int bytesPerBlock = isDxt1 ? 8 : 16;

        // Byte offset of mip level k = sum of block bytes for mips 0..k-1.
        int offset = 0;
        int w = full.Width;
        int h = full.Height;
        for (int m = 0; m < k; m++)
        {
            int bw = (w + 3) / 4; if (bw < 1) bw = 1;
            int bh = (h + 3) / 4; if (bh < 1) bh = 1;
            offset += bw * bh * bytesPerBlock;
            w >>= 1; if (w < 1) w = 1;
            h >>= 1; if (h < 1) h = 1;
        }

        byte[] fullPayload = full.Payload;
        if (offset >= fullPayload.Length) return false; // chain shorter than declared

        int smallWidth = full.Width >> k; if (smallWidth < 1) smallWidth = 1;
        int smallHeight = full.Height >> k; if (smallHeight < 1) smallHeight = 1;
        int smallMipCount = full.MipCount - k;

        int sliceLen = fullPayload.Length - offset;
        var slicePayload = new byte[sliceLen];
        System.Array.Copy(fullPayload, offset, slicePayload, 0, sliceLen);

        small = new DdsTextureInput
        {
            Width = smallWidth,
            Height = smallHeight,
            MipCount = smallMipCount,
            Ps3Format = full.Ps3Format,
            Payload = slicePayload,
        };
        return true;
    }
}
