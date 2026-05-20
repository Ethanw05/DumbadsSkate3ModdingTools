using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using ArenaBuilder.WorldPainter;

namespace ArenaBuilder.WinForms;

/// <summary>
/// BlenRose top-down image + grid overlay. Drag to paint with the active layer/key.
/// Middle-drag pans; mouse wheel zooms (anchored to cursor). Hit-test uses <see cref="WorldPainterGridMath"/>.
/// </summary>
internal sealed class WorldPainterMapPanel : Panel
{
    private readonly WorldPainterForm _owner;
    private Image? _mapImage;
    private int _gridColumns = 64;
    private int _gridRows = 64;
    private double _mapMinX, _mapMaxX, _mapMinZ, _mapMaxZ;
    private float _mapAlignOriginX;
    private float _mapAlignOriginY;
    /// <summary>
    /// When true, image top = +Z (north), bottom = −Z (south), matching typical BlenRose top-down exports.
    /// Independent of grid storage: row index 0 is always south (min Z), matching <see cref="WorldPainterCellQuadTreeBuilder"/>.
    /// </summary>
    private bool _northAtImageTop = true;
    private bool _paintAxesSwapped;
    private bool _hasMapBounds;
    private bool _mouseLeft;
    private bool _mouseRight;
    private (int Col, int Row)? _lastCellTouched;
    private bool _boxSelecting;
    private bool _boxErase;
    private MouseButtons _boxButton;
    private Point _boxStart;
    private Point _boxCurrent;
    /// <summary>Multiplier on the base fit-to-client scale (1 = full bounds visible).</summary>
    private float _viewZoom = 1f;
    /// <summary>Extra client-space offset after centering the world rect (pan).</summary>
    private float _panX;
    private float _panY;
    private bool _panning;
    private Point _panLastClient;
    private const float MinViewZoom = 0.125f;
    private const float MaxViewZoom = 64f;

    public WorldPainterMapPanel(WorldPainterForm owner)
    {
        _owner = owner;
        _mapMinX = -128;
        _mapMaxX = 128;
        _mapMinZ = -128;
        _mapMaxZ = 128;
        _hasMapBounds = true;
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        BackColor = Color.FromArgb(30, 30, 30);
        Cursor = Cursors.Cross;
    }

    public Image? MapImage
    {
        get => _mapImage;
        set
        {
            _mapImage?.Dispose();
            _mapImage = value;
            Invalidate();
        }
    }

    public int GridColumns => _gridColumns;
    public int GridRows => _gridRows;

    /// <summary>True when min/max world bounds are valid (hit-test can run without a loaded PNG).</summary>
    internal bool HasPaintMapBounds => _hasMapBounds;

    public void SetGridSize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 4, 256);
        rows = Math.Clamp(rows, 4, 256);
        if (columns == _gridColumns && rows == _gridRows)
            return;
        _gridColumns = columns;
        _gridRows = rows;
        _owner.OnGridSizeChanged(columns, rows);
        Invalidate();
    }

    public void ConfigurePaintSpace(
        double mapCenterX,
        double mapCenterY,
        double mapHalfX,
        double mapHalfY,
        int gridColumns,
        int gridRows,
        bool northAtImageTop = true)
    {
        _gridColumns = Math.Clamp(gridColumns, 4, 256);
        _gridRows = Math.Clamp(gridRows, 4, 256);
        _mapMinX = mapCenterX - mapHalfX;
        _mapMaxX = mapCenterX + mapHalfX;
        _mapMinZ = mapCenterY - mapHalfY;
        _mapMaxZ = mapCenterY + mapHalfY;
        _mapAlignOriginX = 0f;
        _mapAlignOriginY = 0f;
        _northAtImageTop = northAtImageTop;
        _paintAxesSwapped = false;
        _hasMapBounds = _mapMaxX > _mapMinX && _mapMaxZ > _mapMinZ;
        _viewZoom = 1f;
        _panX = _panY = 0f;
        Invalidate();
    }

    /// <summary>
    /// Maps normalized image vertical <paramref name="v"/> (0 = top of bitmap, 1 = bottom) to game Z.
    /// When <see cref="_northAtImageTop"/> is true (default), screen top → max Z (north), matching BlenRose PNGs.
    /// Grid row indices always use row 0 = south (min Z); see <see cref="TryGetCell"/> (always uses south-up storage to match the WP bake).
    /// </summary>
    private double ClientVToWorldZ(double v, double spanZ)
    {
        double az = _mapAlignOriginY;
        if (_northAtImageTop)
            return _mapMaxZ - v * spanZ + az;
        return _mapMinZ + v * spanZ + az;
    }

    /// <summary>Inverse of <see cref="ClientVToWorldZ"/> for drawing cell rectangles.</summary>
    private double WorldZToClientV(double zWorld, double spanZ)
    {
        double az = _mapAlignOriginY;
        if (_northAtImageTop)
            return (_mapMaxZ + az - zWorld) / spanZ;
        return (zWorld - _mapMinZ - az) / spanZ;
    }

    public (double CenterX, double CenterY, double HalfX, double HalfY) GetPaintMapBounds()
    {
        double cx = (_mapMinX + _mapMaxX) * 0.5;
        double cy = (_mapMinZ + _mapMaxZ) * 0.5;
        double hx = (_mapMaxX - _mapMinX) * 0.5;
        double hy = (_mapMaxZ - _mapMinZ) * 0.5;
        return (cx, cy, hx, hy);
    }

    public bool NorthAtImageTop => _northAtImageTop;

    public static Color ColorForKey(uint lo, uint hi)
    {
        unchecked
        {
            uint h = lo * 2246822519u ^ hi * 3266489917u;
            int r = 70 + (int)(h & 0x5Fu);
            int g = 70 + (int)((h >> 8) & 0x5Fu);
            int b = 70 + (int)((h >> 16) & 0x5Fu);
            return Color.FromArgb(150, r, g, b);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.None;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        if (_mapImage is null)
        {
            using var f = new Font("Segoe UI", 10f);
            const string msg = "No top-down image (add scene_topdown_2k.png next to your GLBs)";
            var sz = g.MeasureString(msg, f);
            g.DrawString(msg, f, Brushes.Gray, (ClientSize.Width - sz.Width) / 2f, (ClientSize.Height - sz.Height) / 2f);
            if (!_hasMapBounds)
                return;
            // Still draw grid / overlay so painting works when PNG is missing but bounds are set.
        }

        var worldDisp = GetWorldDisplayRectangle(ClientSize);
        if (worldDisp.Width <= 0f || worldDisp.Height <= 0f)
            return;
        if (_mapImage is not null)
        {
            int iw = _mapImage.Width;
            int ih = _mapImage.Height;
            var imageDisp = GetUniformImageDrawRectangle(worldDisp, iw, ih);
            g.DrawImage(_mapImage, imageDisp);
        }

        ulong layer = _owner.SelectedLayerGuid;
        if (layer != 0)
        {
            using var brush = new SolidBrush(Color.White);
            for (int r = 0; r < _gridRows; r++)
            {
                for (int c = 0; c < _gridColumns; c++)
                {
                    var cell = _owner.GetCell(layer, c, r);
                    if (cell.Lo == 0 && cell.Hi == 0)
                        continue;
                    brush.Color = ColorForKey(cell.Lo, cell.Hi);
                    g.FillRectangle(brush, GetCellRect(c, r, worldDisp));
                }
            }
        }

        if (_hasMapBounds && _gridColumns > 0 && _gridRows > 0)
        {
            using var gridPen = new Pen(Color.FromArgb(100, 220, 220, 220), 1f);
            float gridLeft = GetCellRect(0, _gridRows - 1, worldDisp).Left;
            float gridRight = GetCellRect(_gridColumns - 1, _gridRows - 1, worldDisp).Right;
            float gridTop = GetCellRect(0, _gridRows - 1, worldDisp).Top;
            float gridBottom = GetCellRect(0, 0, worldDisp).Bottom;

            for (int c = 0; c <= _gridColumns; c++)
            {
                float x = c == _gridColumns
                    ? GetCellRect(_gridColumns - 1, 0, worldDisp).Right
                    : GetCellRect(c, 0, worldDisp).Left;
                g.DrawLine(gridPen, x, gridTop, x, gridBottom);
            }

            for (int r = _gridRows - 1; r >= 0; r--)
            {
                float y = GetCellRect(0, r, worldDisp).Top;
                g.DrawLine(gridPen, gridLeft, y, gridRight, y);
            }

            g.DrawLine(gridPen, gridLeft, gridBottom, gridRight, gridBottom);
        }

        if (_boxSelecting)
        {
            Rectangle box = NormalizeSelectionRect(_boxStart, _boxCurrent);
            using var pen = new Pen(Color.FromArgb(220, 255, 220, 120), 1.5f) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
        }

        if (_hasMapBounds)
            DrawCsimAxisOverlay(g, worldDisp);
    }

    /// <summary>
    /// Same horizontal plane as <see cref="ArenaBuilder.Glb.WorldTileGrid"/> / cSim.
    /// Label placement follows <see cref="_northAtImageTop"/> so +Z matches the map edge (schema v2: north at bottom).
    /// </summary>
    private void DrawCsimAxisOverlay(Graphics g, RectangleF disp)
    {
        const string northLabel = "+Z  north";
        const string southEdgeLabel = "min Z (south)";
        const string rightLabel = "+X";
        using var fontEdge = new Font("Segoe UI", 9f, FontStyle.Bold);
        using var brushText = new SolidBrush(Color.FromArgb(245, 245, 245));
        using var sfTop = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };
        using var sfBottom = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far };
        using var sfRight = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };

        float pad = 6f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_northAtImageTop)
        {
            g.DrawString(northLabel, fontEdge, brushText, new RectangleF(disp.X, disp.Y + pad, disp.Width, 22f), sfTop);
        }
        else
        {
            // Top row of PNG / v→0 is min Z (row 0 = south in storage); +Z is toward the bottom of the panel.
            g.DrawString(southEdgeLabel, fontEdge, brushText, new RectangleF(disp.X, disp.Y + pad, disp.Width, 22f), sfTop);
            g.DrawString(northLabel, fontEdge, brushText, new RectangleF(disp.X, disp.Bottom - 26f - pad, disp.Width, 26f), sfBottom);
        }
        g.DrawString(rightLabel, fontEdge, brushText, new RectangleF(disp.Right - 40f - pad, disp.Y, 40f + pad, disp.Height), sfRight);
        g.SmoothingMode = SmoothingMode.None;
    }

    /// <summary>
    /// World XZ under the cursor (cSim horizontal plane). True when over the top-down image, even if outside the paint AABB.
    /// </summary>
    internal bool TryGetWorldXzFromClient(Point client, out double worldX, out double worldZ)
    {
        worldX = worldZ = 0;
        if (!_hasMapBounds)
            return false;
        var disp = GetWorldDisplayRectangle(ClientSize);
        if (disp.Width <= 0f || disp.Height <= 0f || !disp.Contains(client.X, client.Y))
            return false;

        double u = (client.X - disp.X) / disp.Width;
        double v = (client.Y - disp.Y) / disp.Height;
        u = Math.Clamp(u, 0.0, 1.0);
        v = Math.Clamp(v, 0.0, 1.0);
        double spanX = _mapMaxX - _mapMinX;
        double spanZ = _mapMaxZ - _mapMinZ;
        if (_paintAxesSwapped)
        {
            worldX = ClientVToWorldZ(v, spanX);
            worldZ = _mapMinZ + u * spanZ + _mapAlignOriginY;
        }
        else
        {
            worldX = _mapMinX + u * spanX + _mapAlignOriginX;
            worldZ = ClientVToWorldZ(v, spanZ);
        }
        return true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button == MouseButtons.Middle)
        {
            _panning = true;
            _panLastClient = e.Location;
            Capture = true;
            Cursor = Cursors.SizeAll;
            return;
        }

        bool shift = (ModifierKeys & Keys.Shift) != 0;
        if (shift && (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right))
        {
            _boxSelecting = true;
            _boxErase = e.Button == MouseButtons.Right;
            _boxButton = e.Button;
            _boxStart = _boxCurrent = e.Location;
            Capture = true;
            _lastCellTouched = null;
            Invalidate();
            return;
        }

        if (e.Button == MouseButtons.Left)
            _mouseLeft = true;
        if (e.Button == MouseButtons.Right)
            _mouseRight = true;
        _lastCellTouched = null;
        ApplyBrushAt(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panning)
        {
            int dx = e.X - _panLastClient.X;
            int dy = e.Y - _panLastClient.Y;
            _panLastClient = e.Location;
            if (dx != 0 || dy != 0)
            {
                _panX += dx;
                _panY += dy;
                Invalidate();
            }
            return;
        }

        if (_boxSelecting)
        {
            _boxCurrent = e.Location;
            Invalidate();
            return;
        }

        if (_mouseLeft || _mouseRight)
            ApplyBrushAt(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Middle && _panning)
        {
            _panning = false;
            if (!_boxSelecting)
                Capture = false;
            Cursor = Cursors.Cross;
            return;
        }

        if (_boxSelecting && e.Button == _boxButton)
        {
            _boxCurrent = e.Location;
            ApplyBoxSelection();
            _boxSelecting = false;
            Capture = false;
            Invalidate();
            return;
        }

        if (e.Button == MouseButtons.Left)
            _mouseLeft = false;
        if (e.Button == MouseButtons.Right)
            _mouseRight = false;
        _lastCellTouched = null;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!Capture)
        {
            _mouseLeft = false;
            _mouseRight = false;
            _lastCellTouched = null;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_hasMapBounds && ApplyViewZoomFromWheel(e.Delta, e.Location))
            return;
        base.OnMouseWheel(e);
    }

    /// <summary>
    /// Wheel events go to the focused control; handle <see cref="WM_MOUSEWHEEL"/> when the pointer is over this panel so zoom works without a prior click.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEWHEEL = 0x020A;
        if (m.Msg == WM_MOUSEWHEEL && _hasMapBounds && IsHandleCreated)
        {
            int delta = (short)(((uint)(nint)m.WParam >> 16) & 0xFFFF);
            var screen = new Point(
                (short)((uint)(nint)m.LParam & 0xFFFF),
                (short)(((uint)(nint)m.LParam >> 16) & 0xFFFF));
            var client = PointToClient(screen);
            if (ClientRectangle.Contains(client) && ApplyViewZoomFromWheel(delta, client))
            {
                m.Result = (nint)0;
                return;
            }
        }

        base.WndProc(ref m);
    }

    /// <returns>True if zoom was applied (caller should not propagate).</returns>
    private bool ApplyViewZoomFromWheel(int wheelDelta, Point client)
    {
        if (wheelDelta == 0)
            return false;

        float cw = Math.Max(1, ClientSize.Width);
        float ch = Math.Max(1, ClientSize.Height);
        double worldW = _mapMaxX - _mapMinX;
        double worldH = _mapMaxZ - _mapMinZ;
        if (worldW <= 0 || worldH <= 0)
            return false;

        float k0 = (float)Math.Min(cw / worldW, ch / worldH);
        var dispBefore = GetWorldDisplayRectangle(ClientSize);
        if (dispBefore.Width <= 0f || dispBefore.Height <= 0f)
            return false;

        // Normalized coords unclamped so zoom stays anchored to the cursor even outside the map rect.
        double u = (client.X - dispBefore.X) / dispBefore.Width;
        double v = (client.Y - dispBefore.Y) / dispBefore.Height;

        float factor = wheelDelta > 0 ? 1.12f : 1f / 1.12f;
        float newZoom = Math.Clamp(_viewZoom * factor, MinViewZoom, MaxViewZoom);
        if (Math.Abs(newZoom - _viewZoom) < 1e-6f)
            return false;

        _viewZoom = newZoom;

        float kAfter = k0 * _viewZoom;
        float rwAfter = (float)(worldW * kAfter);
        float rhAfter = (float)(worldH * kAfter);
        _panX = client.X - (float)u * rwAfter - (cw - rwAfter) / 2f;
        _panY = client.Y - (float)v * rhAfter - (ch - rhAfter) / 2f;

        Invalidate();
        return true;
    }

    private void ApplyBrushAt(Point client)
    {
        if (!TryGetCell(client, out int col, out int row))
            return;
        if (_lastCellTouched is { } last && last.Col == col && last.Row == row)
            return;
        _lastCellTouched = (col, row);

        if (_mouseRight)
        {
            _owner.EraseCell(col, row);
            Invalidate();
            return;
        }

        if (!_mouseLeft)
            return;

        if (_owner.TryGetBrushCell(out var cell))
        {
            _owner.PaintCell(col, row, cell);
            Invalidate();
        }
    }

    private void ApplyBoxSelection()
    {
        if (!_hasMapBounds)
            return;
        Rectangle sel = NormalizeSelectionRect(_boxStart, _boxCurrent);
        var selF = new RectangleF(sel.X, sel.Y, sel.Width, sel.Height);
        var disp = GetWorldDisplayRectangle(ClientSize);

        if (_boxErase)
        {
            for (int c = 0; c < _gridColumns; c++)
            {
                for (int r = 0; r < _gridRows; r++)
                {
                    if (!selF.IntersectsWith(GetCellRect(c, r, disp)))
                        continue;
                    _owner.EraseCell(c, r);
                }
            }
        }
        else if (_owner.TryGetBrushCell(out var cell))
        {
            for (int c = 0; c < _gridColumns; c++)
            {
                for (int r = 0; r < _gridRows; r++)
                {
                    if (!selF.IntersectsWith(GetCellRect(c, r, disp)))
                        continue;
                    _owner.PaintCell(c, r, cell);
                }
            }
        }

        Invalidate();
    }

    internal bool TryGetPaintGridCell(Point client, out int col, out int row) => TryGetCell(client, out col, out row);

    private bool TryGetCell(Point client, out int col, out int row)
    {
        col = row = -1;
        if (!_hasMapBounds)
            return false;
        var disp = GetWorldDisplayRectangle(ClientSize);
        if (disp.Width <= 0f || disp.Height <= 0f || !disp.Contains(client.X, client.Y))
            return false;

        double u = (client.X - disp.X) / disp.Width;
        double v = (client.Y - disp.Y) / disp.Height;
        const double eps = 1e-9;
        u = Math.Clamp(u, eps, 1.0 - eps);
        v = Math.Clamp(v, eps, 1.0 - eps);

        double spanX = _mapMaxX - _mapMinX;
        double spanZ = _mapMaxZ - _mapMinZ;
        double ax = _mapAlignOriginX;
        double worldX;
        double worldZ;
        if (_paintAxesSwapped)
        {
            worldX = ClientVToWorldZ(v, spanX);
            worldZ = _mapMinZ + u * spanZ + _mapAlignOriginY;
        }
        else
        {
            worldX = _mapMinX + u * spanX + ax;
            worldZ = ClientVToWorldZ(v, spanZ);
        }
        double cx = (_mapMinX + _mapMaxX) * 0.5;
        double cy = (_mapMinZ + _mapMaxZ) * 0.5;

        // Row 0 = south (min Z) in saved grid — must match WorldPainterCellQuadTreeBuilder / bake pipeline.
        if (!WorldPainterGridMath.TryWorldToPaintCell(
                worldX,
                worldZ,
                cx,
                cy,
                spanX * 0.5,
                spanZ * 0.5,
                _gridColumns,
                _gridRows,
                paintGridRow0AtWorldNorth: false,
                out col,
                out row))
        {
            col = row = -1;
            return false;
        }

        return true;
    }

    private RectangleF GetCellRect(int c, int r, RectangleF disp)
    {
        if (!_hasMapBounds || _gridColumns <= 0 || _gridRows <= 0)
            return RectangleF.Empty;
        double spanX = _mapMaxX - _mapMinX;
        double spanZ = _mapMaxZ - _mapMinZ;
        if (spanX <= 0 || spanZ <= 0)
            return RectangleF.Empty;
        double cw = spanX / _gridColumns;
        double ch = spanZ / _gridRows;
        // Storage row r: 0 = south (min Z), r increases north — same as baker (no flip).
        int rowGeom = r;
        double xWorld0 = _mapMinX + c * cw;
        double xWorld1 = _mapMinX + (c + 1) * cw;
        double zWorld0 = _mapMinZ + rowGeom * ch;
        double zWorld1 = _mapMinZ + (rowGeom + 1) * ch;
        // Inverse of TryGetCell: V mapping uses _northAtImageTop; Z bands use rowGeom = south-up rows.
        double ax = _mapAlignOriginX;
        float u0, u1, v0, v1;
        if (_paintAxesSwapped)
        {
            u0 = (float)((zWorld0 - _mapMinZ - _mapAlignOriginY) / spanZ);
            u1 = (float)((zWorld1 - _mapMinZ - _mapAlignOriginY) / spanZ);
            v0 = (float)WorldZToClientV(xWorld0, spanX);
            v1 = (float)WorldZToClientV(xWorld1, spanX);
        }
        else
        {
            u0 = (float)((xWorld0 - _mapMinX - ax) / spanX);
            u1 = (float)((xWorld1 - _mapMinX - ax) / spanX);
            v0 = (float)WorldZToClientV(zWorld0, spanZ);
            v1 = (float)WorldZToClientV(zWorld1, spanZ);
        }
        float uLo = Math.Min(u0, u1);
        float uHi = Math.Max(u0, u1);
        float vLo = Math.Min(v0, v1);
        float vHi = Math.Max(v0, v1);
        uLo = Math.Clamp(uLo, 0f, 1f);
        uHi = Math.Clamp(uHi, 0f, 1f);
        vLo = Math.Clamp(vLo, 0f, 1f);
        vHi = Math.Clamp(vHi, 0f, 1f);
        if (uHi <= uLo || vHi <= vLo)
            return RectangleF.Empty;
        return RectangleF.FromLTRB(
            disp.X + uLo * disp.Width,
            disp.Y + vLo * disp.Height,
            disp.X + uHi * disp.Width,
            disp.Y + vHi * disp.Height);
    }

    /// <summary>
    /// World AABB in client space: same meters-per-pixel scale on X and Z (uniform <c>k</c>), aspect = world width / world height.
    /// Hit testing and the paint grid use this rectangle; it matches <c>scene_topdown_2k.json</c> extents, not bitmap pixel aspect.
    /// </summary>
    private RectangleF GetWorldDisplayRectangle(Size client)
    {
        float cw = Math.Max(1, client.Width);
        float ch = Math.Max(1, client.Height);
        double worldW = _mapMaxX - _mapMinX;
        double worldH = _mapMaxZ - _mapMinZ;
        if (!_hasMapBounds || worldW <= 0 || worldH <= 0)
            return RectangleF.Empty;

        float k0 = (float)Math.Min(cw / worldW, ch / worldH);
        float k = k0 * _viewZoom;
        float rw = (float)(worldW * k);
        float rh = (float)(worldH * k);
        float ox = (cw - rw) / 2f + _panX;
        float oy = (ch - rh) / 2f + _panY;
        return new RectangleF(ox, oy, rw, rh);
    }

    /// <summary>
    /// Uniform scale (no axis stretch). Anchored to the world rectangle's <b>top-left</b> so image pixel (0,0)
    /// aligns with world (min X, top edge Z per <see cref="_northAtImageTop"/>), matching BlenRose ortho corners.
    /// Letterbox unused bands only on the right/bottom when PNG aspect differs slightly from world.
    /// </summary>
    private static RectangleF GetUniformImageDrawRectangle(RectangleF worldRect, int imgW, int imgH)
    {
        if (imgW <= 0 || imgH <= 0)
            return worldRect;
        float scale = Math.Min(worldRect.Width / imgW, worldRect.Height / imgH);
        float iw = imgW * scale;
        float ih = imgH * scale;
        return new RectangleF(worldRect.X, worldRect.Y, iw, ih);
    }

    private static Rectangle NormalizeSelectionRect(Point a, Point b)
    {
        int x1 = Math.Min(a.X, b.X);
        int y1 = Math.Min(a.Y, b.Y);
        int x2 = Math.Max(a.X, b.X);
        int y2 = Math.Max(a.Y, b.Y);
        int w = Math.Max(1, x2 - x1);
        int h = Math.Max(1, y2 - y1);
        return new Rectangle(x1, y1, w, h);
    }
}
