using ArenaBuilder.WorldPainter;

namespace ArenaBuilder.Tests;

public sealed class WorldPainterGridMathTests
{
    [Fact]
    public void SplitFlatIndex_NegativeOrOob_ReturnsZeroZero()
    {
        WorldPainterGridMath.SplitFlatIndex(-1, 4, 4, out int col, out int row);
        Assert.Equal(0, col);
        Assert.Equal(0, row);

        WorldPainterGridMath.SplitFlatIndex(16, 4, 4, out col, out row);
        Assert.Equal(0, col);
        Assert.Equal(0, row);
    }

    [Fact]
    public void SplitFlatIndex_Valid_RoundTripsWithFlatIndex()
    {
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 5; c++)
            {
                int flat = WorldPainterGridMath.FlatIndex(c, r, 5);
                WorldPainterGridMath.SplitFlatIndex(flat, 5, 3, out int col, out int row);
                Assert.Equal(c, col);
                Assert.Equal(r, row);
            }
    }

    [Fact]
    public void TryWorldToPaintCell_NegativeWorldInsideMap_Works()
    {
        bool ok = WorldPainterGridMath.TryWorldToPaintCell(
            -50.0,
            -30.0,
            mapCenterX: 0,
            mapCenterY: 0,
            mapHalfX: 100,
            mapHalfY: 100,
            gridColumns: 10,
            gridRows: 10,
            paintGridRow0AtWorldNorth: false,
            out int col,
            out int row);
        Assert.True(ok);
        Assert.InRange(col, 0, 9);
        Assert.InRange(row, 0, 9);
    }
}
