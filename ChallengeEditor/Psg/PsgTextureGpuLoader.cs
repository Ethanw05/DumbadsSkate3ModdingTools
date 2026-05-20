using System;
using System.IO;
using Veldrid;

namespace ChallengeEditor.Psg;

/// <summary>
/// Loads a standalone texture PSG (DXT payload in BaseResource) into GPU BC formats.
/// PS3 format byte matches BlenRose: 0x86 = DXT1, 0x87 = DXT3, 0x88 = DXT5.
/// </summary>
public static class PsgTextureGpuLoader
{
    /// <summary>Returns false if the file is not a readable texture PSG.</summary>
    public static bool TryCreateSampledTexture(GraphicsDevice gd, string path, out Texture texture, out TextureView view)
    {
        texture = null!;
        view = null!;
        if (!File.Exists(path)) return false;

        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch { return false; }

        return TryCreateSampledTexture(gd, bytes, out texture, out view);
    }

    /// <summary>
    /// Same as file load — used for texture PSG bytes extracted from a PSF chunk (alongside mesh chunks).
    /// </summary>
    public static bool TryCreateSampledTexture(GraphicsDevice gd, byte[] psgBytes, out Texture texture, out TextureView view)
        => TryCreateSampledTexture(gd, psgBytes, preferredTextureDictIndex: null, out texture, out view);

    /// <summary>
    /// Loads a texture PSG with an optional preferred TEXTURE dictionary index (0-based),
    /// matching Blender importer behavior from TOC m_pObject decoding.
    /// </summary>
    public static bool TryCreateSampledTexture(
        GraphicsDevice gd, byte[] psgBytes, int? preferredTextureDictIndex, out Texture texture, out TextureView view)
    {
        texture = null!;
        view = null!;
        if (psgBytes.Length < 12 || !PsgReader.LooksLikePsg(psgBytes)) return false;

        PsgReader psg;
        try
        {
            psg = new PsgReader(psgBytes);
            psg.Parse();
        }
        catch { return false; }

        PsgReader.DictEntry? texStruct = null;
        PsgReader.DictEntry? brEntry = null;

        if (preferredTextureDictIndex is int idx &&
            idx >= 0 && idx < psg.DictEntries.Count &&
            psg.DictEntries[idx].TypeId == PsgReader.TypeIds.Texture)
        {
            texStruct = psg.DictEntries[idx];
        }

        foreach (var e in psg.DictEntries)
        {
            if (e.TypeId == PsgReader.TypeIds.Texture && texStruct is null) texStruct = e;
            if (e.IsBaseResource && brEntry is null) brEntry = e;
        }

        if (texStruct is null || brEntry is null) return false;

        long texOff = texStruct.IsBaseResource ? psg.MainBase + texStruct.Ptr : texStruct.Ptr;
        if (texOff + 12 > psg.Data.Length) return false;

        byte ps3Fmt = psg.Data[(int)texOff];
        ushort width = psg.U16Be((int)texOff + 8);
        ushort height = psg.U16Be((int)texOff + 10);
        if (width == 0 || height == 0) return false;

        long payloadOff = psg.MainBase + brEntry.Ptr;
        uint payloadLen = brEntry.Size;
        if (payloadOff + payloadLen > psg.Data.Length) return false;

        PixelFormat pixelFmt = ps3Fmt switch
        {
            0x86 => PixelFormat.BC1_Rgba_UNorm,
            0x87 => PixelFormat.BC2_UNorm,
            0x88 => PixelFormat.BC3_UNorm,
            _ => PixelFormat.BC3_UNorm,
        };

        bool bc1 = pixelFmt == PixelFormat.BC1_Rgba_UNorm;
        int blockBytes = bc1 ? 8 : 16;
        int mip0Bytes = System.Math.Max(1, (width + 3) / 4) * System.Math.Max(1, (height + 3) / 4) * blockBytes;
        int copyLen = (int)System.Math.Min(payloadLen, mip0Bytes);
        if (copyLen <= 0) return false;

        var mip0 = new byte[copyLen];
        Buffer.BlockCopy(psg.Data, (int)payloadOff, mip0, 0, copyLen);

        ResourceFactory f = gd.ResourceFactory;
        texture = f.CreateTexture(TextureDescription.Texture2D(
            width, height, mipLevels: 1, arrayLayers: 1, pixelFmt,
            TextureUsage.Sampled));

        gd.UpdateTexture(texture, mip0, 0, 0, 0, width, height, 1, 0, 0);

        view = f.CreateTextureView(texture);
        return true;
    }
}
