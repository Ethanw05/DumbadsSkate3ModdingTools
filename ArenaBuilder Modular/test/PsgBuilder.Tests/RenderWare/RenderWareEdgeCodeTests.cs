using ArenaBuilder.Collision.EdgeCodes;
using System.Numerics;

namespace ArenaBuilder.Tests.RenderWare;

/// <summary>
/// Mirrors RenderWare 6.14.00 tests/clusteredmeshbuilder/test-clusteredmeshbuilderutils.cpp
/// for EdgeCosineToAngleByte + key edge flag behavior.
/// </summary>
public sealed class RenderWareEdgeCodeTests
{
    [Fact]
    public void ComputeExtendedEdgeCosine_OrthogonalNormals_MatchesRwTriangleneighborTests()
    {
        // test-clusteredmeshbuilderutils.cpp TestComputeExtendedEdgeCosine
        var nA = new Vector3(1f, 0f, 0f);
        var nB = new Vector3(0f, 1f, 0f);
        Assert.Equal(0f, EdgeCosines.ComputeExtendedEdgeCosine(nA, nB, new Vector3(0f, 0f, 1f)));
        Assert.Equal(2f, EdgeCosines.ComputeExtendedEdgeCosine(nA, nB, new Vector3(0f, 0f, -1f)));
    }

    [Fact]
    public void EdgeCosineToAngleByte_EdgeCosine3_Produces0()
    {
        // RW test: edgeCosine=3.0f => angleByte=0
        Assert.Equal(0, EdgeCosineToAngleByte.Execute(3f));
    }

    [Fact]
    public void GenerateEdgeCodes_UnmatchedConvex_SetsUnmatchedAndConvexFlags()
    {
        const int numTris = 1;
        int?[][] neighbors = [[null, null, null]];
        float[][] cos = [[0.5f, 0.5f, 0.5f]]; // < 1.0 => convex flag

        var codes = EdgeCodeGenerator.GenerateEdgeCodes(numTris, neighbors, cos)[0];

        Assert.True((codes.E0 & EdgeCodeConstants.EdgeUnmatched) != 0);
        Assert.True((codes.E0 & EdgeCodeConstants.EdgeConvex) != 0);
        Assert.True((codes.E1 & EdgeCodeConstants.EdgeUnmatched) != 0);
        Assert.True((codes.E1 & EdgeCodeConstants.EdgeConvex) != 0);
        Assert.True((codes.E2 & EdgeCodeConstants.EdgeUnmatched) != 0);
        Assert.True((codes.E2 & EdgeCodeConstants.EdgeConvex) != 0);
    }

    [Fact]
    public void GenerateEdgeCodes_UnmatchedSentinelMinusOne_UsesPlanarAngleNibble()
    {
        const int numTris = 1;
        int?[][] neighbors = [[null, null, null]];
        float[][] cos = [[-1f, -1f, -1f]];

        var codes = EdgeCodeGenerator.GenerateEdgeCodes(numTris, neighbors, cos)[0];

        Assert.Equal(26, codes.E0 & 0x1F);
        Assert.True((codes.E0 & EdgeCodeConstants.EdgeUnmatched) != 0);
        Assert.True((codes.E0 & EdgeCodeConstants.EdgeConvex) != 0); // raw -1 < 1
    }

    [Fact]
    public void GenerateEdgeCodes_MatchedVeryConcave_ForcesAngleZero()
    {
        // concaveThreshold = 2 - clamp(-1) = 3, so extendedEdgeCos > 3 forces EdgeAngleZero.
        const int numTris = 1;
        int?[][] neighbors = [[0, 0, 0]];
        float[][] cos = [[3.0001f, 3.5f, 10.0f]];

        var codes = EdgeCodeGenerator.GenerateEdgeCodes(numTris, neighbors, cos)[0];

        Assert.Equal(EdgeCodeConstants.EdgeAngleZero, codes.E0);
        Assert.Equal(EdgeCodeConstants.EdgeAngleZero, codes.E1);
        Assert.Equal(EdgeCodeConstants.EdgeAngleZero, codes.E2);
    }
}

