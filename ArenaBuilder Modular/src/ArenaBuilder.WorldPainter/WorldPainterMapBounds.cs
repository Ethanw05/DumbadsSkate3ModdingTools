namespace ArenaBuilder.WorldPainter;

/// <summary>
/// Aligns editor / top-down map half-extents to Skate <c>WorldPainter::LayerMan</c> registration cells (128 m grid on X / Z)
/// without moving the map center. Works for any asymmetric <c>centerX</c>/<c>centerY</c> from BlenRose schema v2 JSON.
/// </summary>
public static class WorldPainterMapBounds
{
    /// <summary>Default horizontal cell size for GenTile / LayerMan alignment (meters).</summary>
    public const double DefaultAlignmentCellMeters = 128.0;

    /// <summary>
    /// Grows <paramref name="halfX"/> / <paramref name="halfY"/> so the map edges lie on the <paramref name="cellMeters"/> grid
    /// while keeping <paramref name="centerX"/> and <paramref name="centerY"/> fixed (usually 0, 0 from BlenRose top-down).
    /// </summary>
    public static void ExpandToAlignedTileFootprint(
        ref double centerX,
        ref double centerY,
        ref double halfX,
        ref double halfY,
        double cellMeters = DefaultAlignmentCellMeters)
    {
        if (cellMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(cellMeters), "Cell size must be > 0.");
        if (halfX <= 0 || halfY <= 0)
            throw new ArgumentOutOfRangeException(nameof(halfX), "Half extents must be > 0.");

        double minX = centerX - halfX;
        double maxX = centerX + halfX;
        double minY = centerY - halfY;
        double maxY = centerY + halfY;

        double gridMinX = Math.Floor(minX / cellMeters) * cellMeters;
        double gridMaxX = Math.Ceiling(maxX / cellMeters) * cellMeters;
        double gridMinY = Math.Floor(minY / cellMeters) * cellMeters;
        double gridMaxY = Math.Ceiling(maxY / cellMeters) * cellMeters;

        double needHalfX = Math.Max(centerX - gridMinX, gridMaxX - centerX);
        double needHalfY = Math.Max(centerY - gridMinY, gridMaxY - centerY);
        if (needHalfX <= 0 || needHalfY <= 0)
            throw new InvalidOperationException("Aligned half extents computed non-positive; check map center and half extents.");

        halfX = needHalfX;
        halfY = needHalfY;
    }
}
