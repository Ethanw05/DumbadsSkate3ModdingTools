using ArenaBuilder.Collision.Compression;
using System.Numerics;

namespace ArenaBuilder.Tests.RenderWare;

/// <summary>
/// Mirrors RenderWare 6.14.00 tests/clusteredmeshbuilder/test-clusteredmeshbuilderutils.cpp
/// for DetermineCompressionModeAndOffsetForRange (VertexCompression::DetermineCompressionModeAndOffsetForRange).
/// </summary>
public sealed class RenderWareVertexCompressionTests
{
    [Fact]
    public void DetermineCompressionModeAndOffsetForRange_MatchesRwExample()
    {
        // RW inputs (as int32 range):
        // xMin=-256, xMax=256, yMin=-32, yMax=32, zMin=-64, zMax=64
        // Expect: 16-bit compressed, offset = (xMin-1, yMin-1, zMin-1)
        const float granularity = 1.0f;
        var verts = new List<Vector3>
        {
            new(-256, -32, -64),
            new(256,  32,  64),
            new(0, 0, 0)
        };

        var (mode, offset) = DetermineCompressionMode.Execute(verts, granularity);

        Assert.Equal(CompressionConstants.Vertices16BitCompressed, mode);
        Assert.Equal((-257, -33, -65), offset);
    }

    [Fact]
    public void CalculateMinimum16BitGranularityForRange_MatchesRwTest()
    {
        // test-clusteredmeshbuilderutils.cpp TestCalculateMinimum16BitGranularityForRange
        float g = VertexCompression.CalculateMinimum16BitGranularityForRange(-256f, 256f, -32f, 32f, -64f, 64f);
        Assert.Equal(512f / 65535f, g, 5);
    }

    [Fact]
    public void DetermineCompressionModeAndOffsetForRange_TooWide_Selects32Bit()
    {
        // RenderWare tolerance is 65534, and the check is strict "<".
        // So width == 65534 should fail the 16-bit test and force 32-bit.
        const float granularity = 1.0f;
        var verts = new List<Vector3>
        {
            new(0, 0, 0),
            new(65534, 0, 0)
        };

        var (mode, offset) = DetermineCompressionMode.Execute(verts, granularity);

        Assert.Equal(CompressionConstants.Vertices32BitCompressed, mode);
        Assert.Equal((0, 0, 0), offset);
    }
}

