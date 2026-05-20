using ArenaBuilder.Glb;
using System.Numerics;

namespace ArenaBuilder.Tests;

/// <summary>
/// WPQUAD root must use the same 128 m grid as <see cref="WorldTileGrid.GenTileIdSkate"/> (absolute world), not
/// stream-tile-relative <c>floor((w - origin) / 128)</c>, or negative tiles + non-zero cSim origin mis-bake paint.
/// </summary>
public sealed class WorldTileGridWorldPainterTests
{
    [Fact]
    public void GetWorldPainterQuadRootCenterAxisAbsolute_MatchesFloorWorldGrid()
    {
        Assert.Equal(-64f, WorldTileGrid.GetWorldPainterQuadRootCenterAxisAbsolute(-50f));
        Assert.Equal(64f, WorldTileGrid.GetWorldPainterQuadRootCenterAxisAbsolute(50f));
        Assert.Equal(64f, WorldTileGrid.GetWorldPainterQuadRootCenterAxisAbsolute(0f));
    }

    [Fact]
    public void GetWorldPainterQuadRootCenterAxisForStreamBounds_SingleCell_NegativeAxis()
    {
        float c = WorldTileGrid.GetWorldPainterQuadRootCenterAxisForStreamBounds(-100f, 0f);
        Assert.Equal(-64f, c);
    }

    [Fact]
    public void GetWorldPainterQuadRoot_ForNegativeTile_WithNonZeroStreamOrigin_UsesAbsolute128Grid()
    {
        // 100 m tile U=-2: X in [-100, 0) with OriginX=100. Old origin-shifted snap used k=-2 → center -92; GenTileId
        // for that point still used floor(world/128) and disagreed with GetTileHandle for many samples.
        var tile = new WorldTileGrid.TileKey(-2, 0);
        Vector2 wp = WorldTileGrid.GetWorldPainterQuadRootCenter(tile, 100f, 100f, 0f);
        Assert.Equal(-64f, wp.X, 3);
    }

    [Fact]
    public void GetWorldPainterQuadRootCenterAxis_IgnoresOrigin_ParameterStillAccepted()
    {
        float withZero = WorldTileGrid.GetWorldPainterQuadRootCenterAxis(50f, 0f);
        float withHundred = WorldTileGrid.GetWorldPainterQuadRootCenterAxis(50f, 100f);
        Assert.Equal(withZero, withHundred);
        Assert.Equal(64f, withZero);
    }

    [Fact]
    public void DecodeGenTileIdSkate_RoundTripsPackedKeys()
    {
        foreach (uint id in new uint[] { 0u, 0xFFFFFFFFu, 99_999u, 100_000u, unchecked((uint)-100_000) })
        {
            WorldTileGrid.DecodeGenTileIdSkate(id, out int ix, out int iy);
            float half = 64f;
            float wx = ix * 128f + half;
            float wz = iy * 128f + half;
            Assert.Equal(id, WorldTileGrid.GenTileIdSkate(wx, wz));
        }
    }

    [Fact]
    public void GenTileIdSkate_UserSample_neg6_14_9_7_MatchesPackedNeg1()
    {
        uint id = WorldTileGrid.GenTileIdSkate(-6.14f, 9.7f);
        Assert.Equal(0xFFFFFFFFu, id);
        WorldTileGrid.DecodeGenTileIdSkate(id, out int ix, out int iy);
        Assert.Equal(-1, ix);
        Assert.Equal(0, iy);
    }

    [Fact]
    public void GetWorldPainterQuadRootCenterForGenTileId_MatchesUnionWhenUnionAlreadyConsistent()
    {
        uint cellId = WorldTileGrid.GenTileIdSkate(-6.14f, 9.7f);
        var unionRoot = WorldTileGrid.GetWorldPainterQuadRootCenterForBounds(-120f, -1f, 1f, 120f);
        Assert.Equal(cellId, WorldTileGrid.GenTileIdSkate(unionRoot.X, unionRoot.Y));
        var snapped = WorldTileGrid.GetWorldPainterQuadRootCenterForGenTileId(cellId);
        Assert.Equal(unionRoot.X, snapped.X, 3);
        Assert.Equal(unionRoot.Y, snapped.Y, 3);
    }

    /// <summary>
    /// Union-of-bounds root can sit in a different 128 m cell than points still inside that union footprint.
    /// TileBuildPipeline snaps to <see cref="WorldTileGrid.GetWorldPainterQuadRootCenterForGenTileId"/> when union root re-hashes away from <c>wpTileId</c>.
    /// </summary>
    [Fact]
    public void GetWorldPainterQuadRootCenterForBounds_AsymmetricUnionCanRehashAwayFromInteriorPoint()
    {
        var root = WorldTileGrid.GetWorldPainterQuadRootCenterForBounds(-10f, 200f, 0f, 200f);
        uint inWestCell = WorldTileGrid.GenTileIdSkate(-6f, 50f);
        uint atUnionRoot = WorldTileGrid.GenTileIdSkate(root.X, root.Y);
        Assert.Equal(0xFFFFFFFFu, inWestCell);
        Assert.Equal(0u, atUnionRoot);
        Assert.NotEqual(inWestCell, atUnionRoot);
    }
}
