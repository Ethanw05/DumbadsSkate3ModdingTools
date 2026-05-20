namespace ArenaBuilder.Collision.Compression;

/// <summary>
/// RenderWare <c>meshbuilder::VertexCompression</c> (<c>vertexcompression.cpp</c> / <c>vertexcompression.h</c>).
/// </summary>
public static class VertexCompression
{
    /// <summary>
    /// <c>VertexCompression::CalculateMinimum16BitGranularityForRange</c> (<c>vertexcompression.cpp</c> lines 20-42).
    /// </summary>
    public static float CalculateMinimum16BitGranularityForRange(
        float xMin, float xMax, float yMin, float yMax, float zMin, float zMax)
    {
        const float granularityExtent = 65535f;
        float minimumGranularity = (xMax - xMin) / granularityExtent;
        if ((yMax - yMin) / granularityExtent > minimumGranularity)
            minimumGranularity = (yMax - yMin) / granularityExtent;
        if ((zMax - zMin) / granularityExtent > minimumGranularity)
            minimumGranularity = (zMax - zMin) / granularityExtent;
        return minimumGranularity;
    }

    /// <summary>
    /// <c>VertexCompression::DetermineCompressionModeAndOffsetForRange</c> (<c>vertexcompression.cpp</c> lines 46-74).
    /// Integer extents are quantized grid coordinates (same as RW after <c>(int32_t)(pos / granularity)</c>).
    /// </summary>
    public static (byte CompressionMode, (int X, int Y, int Z) Offset) DetermineCompressionModeAndOffsetForRange(
        int xMin, int xMax, int yMin, int yMax, int zMin, int zMax)
    {
        if (xMax - xMin < CompressionConstants.GranularityTolerance &&
            yMax - yMin < CompressionConstants.GranularityTolerance &&
            zMax - zMin < CompressionConstants.GranularityTolerance)
        {
            return (CompressionConstants.Vertices16BitCompressed, (xMin - 1, yMin - 1, zMin - 1));
        }

        return (CompressionConstants.Vertices32BitCompressed, (0, 0, 0));
    }
}
