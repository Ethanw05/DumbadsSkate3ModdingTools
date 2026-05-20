namespace ArenaBuilder.WorldPainter;

/// <summary>
/// Maps game horizontal X / Z to paint grid Col / Row (same convention as pegasus WorldPainter paint).
/// </summary>
public static class WorldPainterGridMath
{
    /// <summary>
    /// Listener positions slightly past the saved map rectangle (float drift, half-open max edge, or tiny export mismatch)
    /// snap to the nearest edge cell instead of falling through to the layer default — avoids wrong ambience along borders
    /// and on the east / high-Z edges where strict max-edge rejection used to miss.
    /// </summary>
    public const double DefaultEdgeSnapMeters = 2.0;
    /// <summary>
    /// Map rectangle in X / Z: uses the same snapping as <see cref="TryWorldToPaintCell"/> (see <see cref="DefaultEdgeSnapMeters"/>).
    /// When <paramref name="paintGridRow0AtWorldNorth"/> is true, JSON row 0 is the northern band (high world Z / maxY side).
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="TryWorldToPaintCell"/> in UI and tools: when the world point is outside the AABB, this method
    /// leaves <paramref name="col"/> and <paramref name="row"/> as 0 (same as an in-bounds hit on column 0, row 0).
    /// </remarks>
    public static void WorldToPaintCell(
        double worldX,
        double worldZ,
        double mapCenterX,
        double mapCenterY,
        double mapHalfX,
        double mapHalfY,
        int gridColumns,
        int gridRows,
        bool paintGridRow0AtWorldNorth,
        out int col,
        out int row)
    {
        _ = TryWorldToPaintCell(
            worldX,
            worldZ,
            mapCenterX,
            mapCenterY,
            mapHalfX,
            mapHalfY,
            gridColumns,
            gridRows,
            paintGridRow0AtWorldNorth,
            out col,
            out row);
    }

    /// <summary>
    /// Resolves a world X/Z sample to a paint cell. Points slightly outside the saved map snap to the nearest edge cell;
    /// points far outside return <c>false</c>.
    /// </summary>
    public static bool TryWorldToPaintCell(
        double worldX,
        double worldZ,
        double mapCenterX,
        double mapCenterY,
        double mapHalfX,
        double mapHalfY,
        int gridColumns,
        int gridRows,
        bool paintGridRow0AtWorldNorth,
        out int col,
        out int row) =>
        TryWorldToPaintCell(
            worldX,
            worldZ,
            mapCenterX,
            mapCenterY,
            mapHalfX,
            mapHalfY,
            gridColumns,
            gridRows,
            paintGridRow0AtWorldNorth,
            DefaultEdgeSnapMeters,
            out col,
            out row);

    /// <param name="edgeSnapMeters">How far outside the map AABB we still snap to the nearest cell (0 = strict half-open box only).</param>
    public static bool TryWorldToPaintCell(
        double worldX,
        double worldZ,
        double mapCenterX,
        double mapCenterY,
        double mapHalfX,
        double mapHalfY,
        int gridColumns,
        int gridRows,
        bool paintGridRow0AtWorldNorth,
        double edgeSnapMeters,
        out int col,
        out int row)
    {
        double minX = mapCenterX - mapHalfX;
        double maxX = mapCenterX + mapHalfX;
        double minY = mapCenterY - mapHalfY;
        double maxY = mapCenterY + mapHalfY;
        col = row = 0;
        if (gridColumns <= 0 || gridRows <= 0)
            return false;

        double spanX = maxX - minX;
        double spanY = maxY - minY;
        if (spanX <= 0 || spanY <= 0)
            return false;

        double snap = Math.Max(0.0, edgeSnapMeters);
        double tolX = Math.Max(1e-9, 1e-9 * spanX);
        double tolY = Math.Max(1e-9, 1e-9 * spanY);

        double wx = worldX;
        if (wx < minX)
        {
            if (wx < minX - snap)
                return false;
            wx = minX;
        }
        else if (wx >= maxX)
        {
            if (wx > maxX + snap)
                return false;
            wx = maxX - tolX;
        }

        double wz = worldZ;
        if (wz < minY)
        {
            if (wz < minY - snap)
                return false;
            wz = minY;
        }
        else if (wz >= maxY)
        {
            if (wz > maxY + snap)
                return false;
            wz = maxY - tolY;
        }

        int c = (int)Math.Floor((wx - minX) / spanX * gridColumns);
        int rGeom = (int)Math.Floor((wz - minY) / spanY * gridRows);
        c = Math.Clamp(c, 0, gridColumns - 1);
        rGeom = Math.Clamp(rGeom, 0, gridRows - 1);
        row = paintGridRow0AtWorldNorth ? gridRows - 1 - rGeom : rGeom;
        col = c;
        return true;
    }

    /// <summary>
    /// Center of paint cell (<paramref name="col"/>, <paramref name="row"/>) in game X / Z (horizontal plane),
    /// using the same map rectangle and row flip as <see cref="TryWorldToPaintCell"/>.
    /// </summary>
    public static void PaintCellCenterWorld(
        int col,
        int row,
        double mapCenterX,
        double mapCenterY,
        double mapHalfX,
        double mapHalfY,
        int gridColumns,
        int gridRows,
        bool paintGridRow0AtWorldNorth,
        out double worldX,
        out double worldZ)
    {
        double minX = mapCenterX - mapHalfX;
        double maxX = mapCenterX + mapHalfX;
        double minY = mapCenterY - mapHalfY;
        double maxY = mapCenterY + mapHalfY;
        double spanX = maxX - minX;
        double spanY = maxY - minY;
        worldX = minX + (col + 0.5) / gridColumns * spanX;
        int rGeom = paintGridRow0AtWorldNorth ? gridRows - 1 - row : row;
        worldZ = minY + (rGeom + 0.5) / gridRows * spanY;
    }

    public static int FlatIndex(int col, int row, int gridColumns) => row * gridColumns + col;

    public static void SplitFlatIndex(int flatIndex, int gridColumns, int gridRows, out int col, out int row)
    {
        col = row = 0;
        if (gridColumns <= 0 || gridRows <= 0)
            return;
        int cellCount = gridColumns * gridRows;
        // C# truncates integer division toward zero and keeps % sign with the dividend; negative or OOB indices
        // would map to the wrong cell if we only clamped after / and %.
        if (flatIndex < 0 || flatIndex >= cellCount)
            return;
        row = flatIndex / gridColumns;
        col = flatIndex % gridColumns;
    }
}
