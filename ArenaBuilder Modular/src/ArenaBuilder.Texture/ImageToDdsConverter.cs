using BCnEncoder.Encoder;
using BCnEncoder.ImageSharp;
using BCnEncoder.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;
using CompressionQuality = BCnEncoder.Encoder.CompressionQuality;

namespace ArenaBuilder.Texture;

/// <summary>
/// Converts encoded raster images (PNG/JPG) to DDS block compression.
/// </summary>
public static class ImageToDdsConverter
{
    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private static int NextPowerOfTwo(int value)
    {
        if (value <= 1)
            return 1;

        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
    }

    private static int ClampToPowerOfTwoNotGreater(int value)
    {
        if (value <= 1) return 1;
        int p = 1;
        while ((p << 1) <= value) p <<= 1;
        return p;
    }

    /// <summary>
    /// Low-level helper: converts image bytes to DDS using BC1 (DXT1) or BC3 (DXT5).
    /// When <paramref name="forceOpaqueAlpha"/> is true, source alpha is discarded before encoding.
    /// This is useful for channels like normal maps where incidental PNG alpha can create BC1
    /// transparent-black blocks that do not exist in the shipped game textures.
    /// </summary>
    /// <param name="bc1NormalMapArtifactReduction">
    /// When true and output is BC1, applies a mild blur and uses the slowest BC1 search to reduce
    /// 4×4 block speckle (orange/wrong endpoint artifacts) on tangent-space normals.
    /// </param>
    /// <param name="targetWidth">
    /// When both <paramref name="targetWidth"/> and <paramref name="targetHeight"/> are set (&gt; 0),
    /// the image is scaled to fit inside that size with <see cref="ResizeMode.Pad"/> (letterboxing)
    /// before encoding; power-of-two expansion is skipped.
    /// </param>
    public static byte[] ConvertToDds(
        byte[] encodedImageBytes,
        bool generateMipMaps,
        bool hasAlpha,
        bool forceOpaqueAlpha = false,
        bool bc1NormalMapArtifactReduction = false,
        int? maxDimension = null,
        int? targetWidth = null,
        int? targetHeight = null)
    {
        if (encodedImageBytes == null || encodedImageBytes.Length == 0)
            throw new ArgumentException("Image bytes are required.", nameof(encodedImageBytes));

        using var image = Image.Load<Rgba32>(encodedImageBytes);

        bool fixedTarget = targetWidth is int tw && targetHeight is int th && tw > 0 && th > 0;

        if (!fixedTarget && maxDimension is int cap && cap > 0)
        {
            int sizeCap = ClampToPowerOfTwoNotGreater(cap);
            if (image.Width > sizeCap || image.Height > sizeCap)
            {
                int newW = Math.Min(image.Width, sizeCap);
                int newH = Math.Min(image.Height, sizeCap);
                image.Mutate(x => x.Resize(newW, newH));
            }
        }

        if (fixedTarget)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(targetWidth!.Value, targetHeight!.Value),
                Mode = ResizeMode.Pad,
                PadColor = Color.Black,
            }));
        }
        else if (!IsPowerOfTwo(image.Width) || !IsPowerOfTwo(image.Height))
        {
            int resizeW = NextPowerOfTwo(image.Width);
            int resizeH = NextPowerOfTwo(image.Height);
            image.Mutate(x => x.Resize(resizeW, resizeH));
        }

        if (forceOpaqueAlpha)
            SetOpaqueAlpha(image);

        bool useBc1 = !hasAlpha;
        if (useBc1 && bc1NormalMapArtifactReduction)
        {
            // Soften high-frequency contrast so BC1 endpoint selection does not create isolated bad blocks.
            image.Mutate(x => x.GaussianBlur(NormalBc1PreBlurSigma));
        }

        var encoder = new BcEncoder();
        encoder.OutputOptions.Format = hasAlpha ? CompressionFormat.Bc3 : CompressionFormat.Bc1;
        encoder.OutputOptions.FileFormat = OutputFileFormat.Dds;
        encoder.OutputOptions.GenerateMipMaps = generateMipMaps;
        encoder.OutputOptions.Quality = useBc1 && bc1NormalMapArtifactReduction
            ? CompressionQuality.BestQuality
            : CompressionQuality.Balanced;

        using var ms = new MemoryStream();
        encoder.EncodeToStream(image, ms);
        return ms.ToArray();
    }

    /// <summary>Gaussian sigma (pixels) applied only for BC1 normal-map artifact reduction before encode.</summary>
    private const float NormalBc1PreBlurSigma = 0.55f;

    /// <summary>
    /// Legacy compatibility helper: converts image bytes to DDS using BC3 (DXT5).
    /// Prefer <see cref="ConvertToDds"/> for new call sites.
    /// </summary>
    public static byte[] ConvertToDxt5(byte[] encodedImageBytes, bool generateMipMaps = true)
    {
        return ConvertToDds(encodedImageBytes, generateMipMaps, hasAlpha: true);
    }

    private static void SetOpaqueAlpha(Image<Rgba32> image)
    {
        // ProcessPixelRows = row-span access (single contiguous Span<Rgba32>
        // per row). The previous double-indexer loop did a read+write per
        // pixel via the slow `image[x,y]` path which performs a row+pixel
        // lookup per access. Used per normal-map encode where we discard
        // source alpha — typically 1024² → 1M pixels, so the difference
        // between ~5-10ns and ~50ns per pixel matters.
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    row[x].A = byte.MaxValue;
            }
        });
    }
}

