using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Collections.Concurrent;
using System.IO.Hashing;

namespace ArenaBuilder.Texture;

/// <summary>
/// Generates derived texture maps (normal/specular) from a source raster image.
/// Formulas are aligned with the behavior used by NormalMap-Online defaults.
/// </summary>
public static class DerivedTextureGenerator
{
    private static readonly ConcurrentDictionary<string, byte[]> DerivedPngCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Normal synthesis settings. BlurSharp follows NormalMap-Online semantics:
    /// amount &lt; 0 => blur, amount &gt; 0 => sharpen, 0 => neutral.
    /// </summary>
    /// <param name="MinTangentSpaceZ">
    /// After unit-length normalize, tangent-space Z is floored to this value (0–1), scaling X/Y so the vector stays unit.
    /// Prevents near-grazing normals at sharp height edges (orange / low-blue in RGB) that compress badly in BC1.
    /// Set to 0 to disable.
    /// </param>
    public readonly record struct NormalSynthSettings(
        float Strength,
        float Level,
        float BlurSharp,
        int MaxWidth,
        int MaxHeight,
        float MinTangentSpaceZ);

    // Default NormalMap-Online-style settings used by the build pipeline.
    public static readonly NormalSynthSettings DefaultNormalSettings = new(
        Strength: 0.22f,
        Level: 7.5f,
        BlurSharp: -0.15f,
        MaxWidth: 256,
        MaxHeight: 256,
        MinTangentSpaceZ: 0.43f);

    private const float SpecularStrength = 0.18f;
    private const float SpecularMean = 1f;
    private const float SpecularRange = 1f;
    private const float SpecularMax = 0.25f;

    /// <summary>
    /// Creates a tangent-space normal map PNG from a source image using a Sobel kernel.
    /// </summary>
    public static byte[] GenerateNormalMapPngFromImage(byte[] encodedImageBytes)
        => GenerateNormalMapPngFromImage(encodedImageBytes, DefaultNormalSettings);

    /// <summary>
    /// Creates a tangent-space normal map PNG using caller-provided settings.
    /// </summary>
    public static byte[] GenerateNormalMapPngFromImage(byte[] encodedImageBytes, NormalSynthSettings settings)
    {
        string cacheKey = BuildCacheKey(encodedImageBytes, "normal", settings);
        if (DerivedPngCache.TryGetValue(cacheKey, out var cachedNormal))
            return cachedNormal;

        var synth = NormalizeSettings(settings);
        using var source = LoadSourceImage(encodedImageBytes, synth.MaxWidth, synth.MaxHeight);
        int width = source.Width;
        int height = source.Height;
        float[] heightLuma = BuildHeightLumaAndAlpha(source, out byte[] alpha);

        if (synth.BlurSharp != 0f)
            heightLuma = ApplyNormalMapOnlineBlurLuma(heightLuma, width, height, synth.BlurSharp);
        float dz = (1f / synth.Strength) * (1f + MathF.Pow(2f, synth.Level));

        int[] xm1 = BuildWrappedOffsetIndex(width, -1);
        int[] xp1 = BuildWrappedOffsetIndex(width, +1);
        int[] ym1 = BuildWrappedOffsetIndex(height, -1);
        int[] yp1 = BuildWrappedOffsetIndex(height, +1);
        byte[] pixels = new byte[width * height * 4];

        Parallel.For(0, height, y =>
        {
            int row = y * width;
            int rowUp = ym1[y] * width;
            int rowDown = yp1[y] * width;
            for (int x = 0; x < width; x++)
            {
                int xl = xm1[x];
                int xr = xp1[x];

                float tl = heightLuma[rowUp + xl];
                float l = heightLuma[row + xl];
                float bl = heightLuma[rowDown + xl];
                float t = heightLuma[rowUp + x];
                float b = heightLuma[rowDown + x];
                float tr = heightLuma[rowUp + xr];
                float r = heightLuma[row + xr];
                float br = heightLuma[rowDown + xr];

                float dx = tl + (2f * l) + bl - tr - (2f * r) - br;
                float dy = tl + (2f * t) + tr - bl - (2f * b) - br;

                // Match shader scaling before normalize (dx/dy * 255).
                float nx = dx * 255f;
                float ny = dy * 255f;
                float nz = dz;
                float invLen = 1f / MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
                nx *= invLen;
                ny *= invLen;
                nz *= invLen;

                if (synth.MinTangentSpaceZ > 0f)
                    ClampMinTangentSpaceZ(ref nx, ref ny, ref nz, synth.MinTangentSpaceZ);

                int outIdx = (row + x) * 4;
                pixels[outIdx + 0] = ToByte((nx * 0.5f) + 0.5f);
                pixels[outIdx + 1] = ToByte((ny * 0.5f) + 0.5f);
                pixels[outIdx + 2] = ToByte((nz * 0.5f) + 0.5f);
                pixels[outIdx + 3] = alpha[row + x];
            }
        });

        using var normal = Image.LoadPixelData<Rgba32>(pixels, width, height);
        using var ms = new MemoryStream();
        normal.SaveAsPng(ms);
        byte[] result = ms.ToArray();
        DerivedPngCache.TryAdd(cacheKey, result);
        return result;
    }

    private static float[] ApplyNormalMapOnlineBlurLuma(float[] sourceLuma, int width, int height, float amount)
    {
        if (amount == 0f)
            return sourceLuma;

        // Same 9-tap weights as NormalMap-Online's Horizontal/VerticalBlurShader.js.
        float[] w = [0.051f, 0.0918f, 0.12245f, 0.1531f, 0.1633f, 0.1531f, 0.12245f, 0.0918f, 0.051f];
        float hStep = amount / width / 5f;
        float vStep = amount / height / 5f;

        float[] horiz = new float[sourceLuma.Length];
        float[] vert = new float[sourceLuma.Length];
        TapLerpMap hMap = BuildTapLerpMap(width, hStep);
        TapLerpMap vMap = BuildTapLerpMap(height, vStep);

        // Horizontal pass.
        Parallel.For(0, height, y =>
        {
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                float src = sourceLuma[rowBase + x];
                int mapBase = x * 9;
                float sum = 0f;
                for (int tap = 0; tap < 9; tap++)
                {
                    int li = rowBase + hMap.Left[mapBase + tap];
                    int ri = rowBase + hMap.Right[mapBase + tap];
                    float t = hMap.T[mapBase + tap];
                    float sample = sourceLuma[li] + ((sourceLuma[ri] - sourceLuma[li]) * t);
                    sum += w[tap] * sample;
                }

                // Shader behavior: if step > 0, output sharpened (2*src - blur), else output blur.
                if (hStep > 0f)
                    sum = src + src - sum;
                horiz[rowBase + x] = Math.Clamp(sum, 0f, 1f);
            }
        });

        // Vertical pass.
        Parallel.For(0, height, y =>
        {
            int mapBaseY = y * 9;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                float src = horiz[rowBase + x];
                float sum = 0f;
                for (int tap = 0; tap < 9; tap++)
                {
                    int li = (vMap.Left[mapBaseY + tap] * width) + x;
                    int ri = (vMap.Right[mapBaseY + tap] * width) + x;
                    float t = vMap.T[mapBaseY + tap];
                    float sample = horiz[li] + ((horiz[ri] - horiz[li]) * t);
                    sum += w[tap] * sample;
                }

                if (vStep > 0f)
                    sum = src + src - sum;
                vert[rowBase + x] = Math.Clamp(sum, 0f, 1f);
            }
        });

        return vert;
    }

    /// <summary>
    /// Creates a grayscale specular map PNG from a source image using
    /// mean/range linear falloff (NormalMap-Online default style).
    /// </summary>
    public static byte[] GenerateSpecularMapPngFromImage(byte[] encodedImageBytes)
    {
        string cacheKey = BuildCacheKey(encodedImageBytes, "specular");
        if (DerivedPngCache.TryGetValue(cacheKey, out var cachedSpecular))
            return cachedSpecular;

        using var source = LoadSourceImage(encodedImageBytes, DefaultNormalSettings.MaxWidth, DefaultNormalSettings.MaxHeight);
        int width = source.Width;
        int height = source.Height;
        float[] heightLuma = BuildHeightLumaAndAlpha(source, out byte[] alpha);
        byte[] pixels = new byte[width * height * 4];

        Parallel.For(0, height, y =>
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                float height = heightLuma[row + x];
                float pctDistanceToMean = (SpecularRange - MathF.Abs(height - SpecularMean)) / SpecularRange;
                float intensity = MathF.Max(0f, pctDistanceToMean) * SpecularStrength;
                intensity = MathF.Min(intensity, SpecularMax);

                byte v = ToByte(intensity);
                int outIdx = (row + x) * 4;
                pixels[outIdx + 0] = v;
                pixels[outIdx + 1] = v;
                pixels[outIdx + 2] = v;
                pixels[outIdx + 3] = alpha[row + x];
            }
        });

        using var specular = Image.LoadPixelData<Rgba32>(pixels, width, height);
        using var ms = new MemoryStream();
        specular.SaveAsPng(ms);
        byte[] result = ms.ToArray();
        DerivedPngCache.TryAdd(cacheKey, result);
        return result;
    }

    private static Image<Rgba32> LoadSourceImage(byte[] encodedImageBytes, int maxWidth, int maxHeight)
    {
        if (encodedImageBytes == null || encodedImageBytes.Length == 0)
            throw new ArgumentException("Source image bytes are required.", nameof(encodedImageBytes));

        try
        {
            var image = Image.Load<Rgba32>(encodedImageBytes);
            if (image.Width > maxWidth || image.Height > maxHeight)
            {
                image.Mutate(op => op.Resize(new ResizeOptions
                {
                    Size = new Size(maxWidth, maxHeight),
                    Mode = ResizeMode.Max,
                    Sampler = KnownResamplers.Lanczos3
                }));
            }

            return image;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to decode source image for derived map generation. " +
                "Only raster image formats supported by ImageSharp are currently accepted.",
                ex);
        }
    }

    private static float[] BuildHeightLumaAndAlpha(Image<Rgba32> image, out byte[] alpha)
    {
        int width = image.Width;
        int height = image.Height;
        var result = new float[width * height];
        alpha = new byte[width * height];
        // ProcessPixelRows lets us read each row as a Span<Rgba32> in
        // contiguous memory — 5-10x faster than the `image[x,y]` indexer
        // which does a row+pixel lookup per access. Source images are often
        // 1024-2048px → tens of millions of indexer calls per autogen
        // call; ProcessPixelRows is the canonical fast path.
        float[] resultLocal = result;
        byte[] alphaLocal = alpha;
        int widthLocal = width;
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                int rowBase = y * widthLocal;
                for (int x = 0; x < row.Length; x++)
                {
                    Rgba32 p = row[x];
                    int i = rowBase + x;
                    resultLocal[i] = ((0.2126f * p.R) + (0.7152f * p.G) + (0.0722f * p.B)) / 255f;
                    alphaLocal[i] = p.A;
                }
            }
        });

        return result;
    }

    private static int[] BuildWrappedOffsetIndex(int size, int offset)
    {
        var map = new int[size];
        for (int i = 0; i < size; i++)
            map[i] = WrapCoordinate(i + offset, size);
        return map;
    }

    private static TapLerpMap BuildTapLerpMap(int size, float normalizedStep)
    {
        var left = new int[size * 9];
        var right = new int[size * 9];
        var t = new float[size * 9];

        for (int p = 0; p < size; p++)
        {
            float coord = p + 0.5f;
            int baseIdx = p * 9;
            for (int tap = 0; tap < 9; tap++)
            {
                float offset = (tap - 4) * normalizedStep * size;
                float sample = coord + offset - 0.5f;
                int i0 = (int)MathF.Floor(sample);
                float frac = sample - i0;
                left[baseIdx + tap] = WrapCoordinate(i0, size);
                right[baseIdx + tap] = WrapCoordinate(i0 + 1, size);
                t[baseIdx + tap] = frac;
            }
        }

        return new TapLerpMap(left, right, t);
    }

    private static int WrapCoordinate(int value, int size)
    {
        int mod = value % size;
        return mod < 0 ? mod + size : mod;
    }

    private static byte ToByte(float normalizedValue)
    {
        float clamped = Math.Clamp(normalizedValue, 0f, 1f);
        return (byte)MathF.Round(clamped * 255f);
    }

    private static string BuildCacheKey(byte[] encodedImageBytes, string mode, NormalSynthSettings? normalSettings = null)
    {
        // xxHash128 — non-cryptographic process-local cache key. Replaced
        // SHA256 (~10-20x faster on multi-MB image buffers; we hash the
        // FULL source image here, which can be the largest single cost in
        // a build dominated by texture work). 128-bit width keeps collision
        // probability negligible within a single build.
        Span<byte> hash = stackalloc byte[16];
        XxHash128.Hash(encodedImageBytes, hash);
        string hashHex = Convert.ToHexString(hash);
        return mode switch
        {
            "normal" => normalSettings.HasValue
                ? $"normal|{normalSettings.Value.Strength}|{normalSettings.Value.Level}|{normalSettings.Value.BlurSharp}|{normalSettings.Value.MaxWidth}|{normalSettings.Value.MaxHeight}|{normalSettings.Value.MinTangentSpaceZ}|{hashHex}"
                : $"normal|{DefaultNormalSettings.Strength}|{DefaultNormalSettings.Level}|{DefaultNormalSettings.BlurSharp}|{DefaultNormalSettings.MaxWidth}|{DefaultNormalSettings.MaxHeight}|{DefaultNormalSettings.MinTangentSpaceZ}|{hashHex}",
            "specular" => $"specular|{SpecularStrength}|{SpecularMean}|{SpecularRange}|{SpecularMax}|{hashHex}",
            _ => $"{mode}|{hashHex}"
        };
    }

    private static NormalSynthSettings NormalizeSettings(NormalSynthSettings settings)
    {
        float strength = settings.Strength <= 0f ? DefaultNormalSettings.Strength : settings.Strength;
        float level = settings.Level;
        float blurSharp = settings.BlurSharp;
        int maxWidth = settings.MaxWidth <= 0 ? DefaultNormalSettings.MaxWidth : settings.MaxWidth;
        int maxHeight = settings.MaxHeight <= 0 ? DefaultNormalSettings.MaxHeight : settings.MaxHeight;
        float minZ = settings.MinTangentSpaceZ;
        if (float.IsNaN(minZ))
            minZ = DefaultNormalSettings.MinTangentSpaceZ;
        minZ = Math.Clamp(minZ, 0f, 0.995f);
        return new NormalSynthSettings(strength, level, blurSharp, maxWidth, maxHeight, minZ);
    }

    /// <summary>
    /// If Z is below <paramref name="minNz"/>, scale X/Y so the vector is unit with Z = minNz.
    /// </summary>
    private static void ClampMinTangentSpaceZ(ref float nx, ref float ny, ref float nz, float minNz)
    {
        if (nz >= minNz)
            return;

        float xyLenSq = (nx * nx) + (ny * ny);
        if (xyLenSq < 1e-12f)
        {
            nx = 0f;
            ny = 0f;
            nz = 1f;
            return;
        }

        float xyLen = MathF.Sqrt(xyLenSq);
        float maxXy = MathF.Sqrt(MathF.Max(0f, 1f - (minNz * minNz)));
        float scale = maxXy / xyLen;
        nx *= scale;
        ny *= scale;
        nz = minNz;
    }

    private readonly record struct TapLerpMap(int[] Left, int[] Right, float[] T);
}
