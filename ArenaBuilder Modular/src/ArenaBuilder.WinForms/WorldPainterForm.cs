using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using ArenaBuilder.WorldPainter;

namespace ArenaBuilder.WinForms;

/// <summary>
/// Paint WPDICT keys on a BlenRose top-down image. Data is stored per layer as a
/// row-major array of <see cref="WpCell"/> values (row 0 = south = min Z).
/// Saves to <c>worldpainter.bin</c>. The "Export PSGs" button writes
/// WorldPainter PSGs directly into every <c>cSim_*_high</c> directory found
/// in <see cref="SourceFolder"/> — no CLI step required.
/// </summary>
internal sealed class WorldPainterForm : Form
{
    public const string TopdownPngFileName = "scene_topdown_2k.png";
    public const string TopdownJsonFileName = "scene_topdown_2k.json";

    private static readonly Color BackColorMain = Color.FromArgb(37, 37, 38);
    private static readonly Color BackColorInput = Color.FromArgb(30, 30, 30);
    private static readonly Color ForeColorPrimary = Color.FromArgb(241, 241, 241);
    private static readonly Color ForeColorMuted = Color.FromArgb(180, 180, 180);
    private static readonly Font FontUi = new("Segoe UI", 9.25f);

    private readonly WorldPainterMapPanel _mapPanel;
    private readonly ComboBox _layerCombo;
    private readonly ComboBox _keyCombo;
    private readonly NumericUpDown _gridColsNud;
    private readonly NumericUpDown _gridRowsNud;
    private readonly NumericUpDown _tileSizeNud;
    private readonly NumericUpDown _boundsMinXNud;
    private readonly NumericUpDown _boundsMaxXNud;
    private readonly NumericUpDown _boundsMinZNud;
    private readonly NumericUpDown _boundsMaxZNud;
    private readonly Label _statusLabel;
    private string? _loadedTopdownJson;
    private bool _paintNorthAtImageTop = true;
    private bool _topdownContractV2;

    /// <summary>Per-layer dense cell array: cells[row * cols + col], row 0 = south.</summary>
    private readonly Dictionary<ulong, WpCell[]> _grids = new();

    public string SourceFolder { get; }

    internal ulong SelectedLayerGuid =>
        _layerCombo.SelectedItem is WorldPainterCatalog.LayerEntry le ? le.Guid : 0UL;

    public WorldPainterForm(string sourceFolder)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder))
            throw new ArgumentException("Folder required.", nameof(sourceFolder));
        sourceFolder = sourceFolder.Trim();
        if (!Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException(sourceFolder);

        SourceFolder = sourceFolder;
        Text = "WorldPainter — " + Path.GetFileName(sourceFolder.TrimEnd(Path.DirectorySeparatorChar));
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 480);
        Size = new Size(1040, 640);
        BackColor = BackColorMain;
        ForeColor = ForeColorPrimary;
        Font = FontUi;
        DoubleBuffered = true;

        _layerCombo = CreateCombo();
        _keyCombo = CreateCombo();
        _keyCombo.DropDownHeight = 400;
        _gridColsNud = new NumericUpDown
        {
            Minimum = 4, Maximum = 256, Value = 64, Width = 56,
            BackColor = BackColorInput, ForeColor = ForeColorPrimary
        };
        _gridRowsNud = new NumericUpDown
        {
            Minimum = 4, Maximum = 256, Value = 64, Width = 56,
            BackColor = BackColorInput, ForeColor = ForeColorPrimary
        };
        _tileSizeNud = new NumericUpDown
        {
            Minimum = 1, Maximum = 1024, Value = 100, Width = 70, DecimalPlaces = 1,
            BackColor = BackColorInput, ForeColor = ForeColorPrimary
        };
        _boundsMinXNud = MakeBoundsNud(-128);
        _boundsMaxXNud = MakeBoundsNud(128);
        _boundsMinZNud = MakeBoundsNud(-128);
        _boundsMaxZNud = MakeBoundsNud(128);
        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 22,
            Padding = new Padding(12, 4, 12, 0),
            ForeColor = ForeColorMuted,
            BackColor = BackColorMain,
            Text = "Hover map · left drag = paint · right = erase · Shift+drag = box selection"
        };

        _mapPanel = new WorldPainterMapPanel(this)
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle
        };
        _layerCombo.SelectedIndexChanged += (_, _) =>
        {
            OnLayerChanged();
            _mapPanel.Invalidate();
        };

        var sidebar = BuildSidebar();
        sidebar.Dock = DockStyle.Left;

        var imagePanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 12, 12, 0),
            BackColor = BackColorMain
        };
        imagePanel.Controls.Add(_mapPanel);

        Controls.Add(_statusLabel);
        Controls.Add(imagePanel);
        Controls.Add(sidebar);

        Shown += (_, _) =>
        {
            LoadTopdownAssets();
            PopulateLayerCombo();
            TryLoadData(silent: true);
            ApplyTopdownBoundsFromJson();
            EnsureAllLayerGrids();
            _mapPanel.Invalidate();
        };

        _mapPanel.MouseMove += (_, e) => UpdateHoverStatus(e.Location);
    }

    // ── Paint API used by WorldPainterMapPanel ────────────────────────────────

    internal WpCell GetCell(ulong layerGuid, int col, int row)
    {
        if (!_grids.TryGetValue(layerGuid, out var g))
            return default;
        int i = WorldPainterGridMath.FlatIndex(col, row, _mapPanel.GridColumns);
        if ((uint)i >= (uint)g.Length)
            return default;
        return g[i];
    }

    internal void PaintCell(int col, int row, WpCell cell)
    {
        ulong g = SelectedLayerGuid;
        if (g == 0) return;
        var grid = EnsureGrid(g);
        int i = WorldPainterGridMath.FlatIndex(col, row, _mapPanel.GridColumns);
        if ((uint)i < (uint)grid.Length)
            grid[i] = cell;
    }

    internal void EraseCell(int col, int row) => PaintCell(col, row, default);

    internal bool TryGetBrushCell(out WpCell cell)
    {
        if (_keyCombo.SelectedItem is not WorldPainterCatalog.KeyEntry ke)
        {
            cell = default;
            return false;
        }
        cell = new WpCell(ke.Lo, ke.Hi);
        return true;
    }

    internal void OnGridSizeChanged(int columns, int rows)
    {
        _grids.Clear();
        EnsureAllLayerGrids();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private WpCell[] EnsureGrid(ulong layerGuid)
    {
        int n = _mapPanel.GridColumns * _mapPanel.GridRows;
        if (_grids.TryGetValue(layerGuid, out var existing) && existing.Length == n)
            return existing;
        var arr = new WpCell[n];
        _grids[layerGuid] = arr;
        return arr;
    }

    private void EnsureAllLayerGrids()
    {
        foreach (var layer in WorldPainterCatalog.Layers)
            EnsureGrid(layer.Guid);
    }

    private void UpdateHoverStatus(Point client)
    {
        string noPngHint = _mapPanel.MapImage is null ? " · (no PNG — add scene_topdown_2k.png for texture) " : "";

        string xzSuffix = "";
        if (_mapPanel.TryGetWorldXzFromClient(client, out double wx, out double wz))
            xzSuffix = $" · cSim X={wx:F1} Z={wz:F1} m";
        if (!_topdownContractV2)
            xzSuffix += " · Re-export BlenRose for scene_topdown_2k.json schema v2";

        if (!_mapPanel.TryGetPaintGridCell(client, out int col, out int row))
        {
            if (_mapPanel.MapImage is null && !_mapPanel.HasPaintMapBounds)
                _statusLabel.Text = "Add scene_topdown_2k.png and bounds (BlenRose export).";
            else
                _statusLabel.Text = "Outside paint AABB" + xzSuffix + noPngHint;
            return;
        }

        ulong g = SelectedLayerGuid;
        var c = GetCell(g, col, row);
        _statusLabel.Text =
            $"col={col} row={row} idx={WorldPainterGridMath.FlatIndex(col, row, _mapPanel.GridColumns)} · " +
            $"Lo=0x{c.Lo:X8} Hi=0x{c.Hi:X8}" + xzSuffix + noPngHint;
    }

    // ── Sidebar layout ────────────────────────────────────────────────────────

    private Panel BuildSidebar()
    {
        const int w = 328;
        var panel = new Panel { Width = w, Padding = new Padding(12, 12, 8, 12), BackColor = BackColorMain };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 17,
            BackColor = BackColorMain
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        float[] rowHeightsPx =
        [
            20,  // 0 Layer caption
            29,  // 1 layer combo
            20,  // 2 Key caption
            29,  // 3 key combo
            22,  // 4 Grid caption
            36,  // 5 W/H nuds
            20,  // 6 Tile size caption
            36,  // 7 tile size nud
            20,  // 8 World bounds caption
            36,  // 9 MinX / MaxX row
            36,  // 10 MinZ / MaxZ row
            36,  // 11 Save button
            36,  // 12 Reload button
            36,  // 13 Export button
        ];
        foreach (float h in rowHeightsPx)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 14 help
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // 15 spacer

        void AddLabel(string text, int row)
        {
            layout.Controls.Add(new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                ForeColor = ForeColorMuted,
                BackColor = BackColorMain,
                TextAlign = ContentAlignment.BottomLeft
            }, 0, row);
        }

        AddLabel("Layer", 0);
        layout.Controls.Add(_layerCombo, 0, 1);
        AddLabel("Key (WPDICT halves)", 2);
        layout.Controls.Add(_keyCombo, 0, 3);
        AddLabel("Grid (W × H cells)", 4);

        var gridFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = BackColorMain
        };
        gridFlow.Controls.Add(new Label { Text = "W", AutoSize = true, ForeColor = ForeColorPrimary, BackColor = BackColorMain, Margin = new Padding(0, 6, 4, 0) });
        gridFlow.Controls.Add(_gridColsNud);
        gridFlow.Controls.Add(new Label { Text = "H", AutoSize = true, ForeColor = ForeColorPrimary, BackColor = BackColorMain, Margin = new Padding(8, 6, 4, 0) });
        gridFlow.Controls.Add(_gridRowsNud);
        layout.Controls.Add(gridFlow, 0, 5);
        _gridColsNud.ValueChanged += (_, _) => SyncGridFromNud();
        _gridRowsNud.ValueChanged += (_, _) => SyncGridFromNud();

        AddLabel("Stream tile size (m)", 6);
        var tsFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = BackColorMain
        };
        tsFlow.Controls.Add(_tileSizeNud);
        layout.Controls.Add(tsFlow, 0, 7);

        AddLabel("World bounds (m)", 8);

        static Label BoundsLbl(string t) => new()
        {
            Text = t, AutoSize = true,
            ForeColor = Color.FromArgb(200, 200, 200),
            BackColor = Color.FromArgb(37, 37, 38),
            Margin = new Padding(0, 6, 4, 0)
        };
        var xFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, BackColor = BackColorMain };
        xFlow.Controls.Add(BoundsLbl("X min"));
        xFlow.Controls.Add(_boundsMinXNud);
        xFlow.Controls.Add(BoundsLbl("max"));
        xFlow.Controls.Add(_boundsMaxXNud);
        layout.Controls.Add(xFlow, 0, 9);

        var zFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, BackColor = BackColorMain };
        zFlow.Controls.Add(BoundsLbl("Z min"));
        zFlow.Controls.Add(_boundsMinZNud);
        zFlow.Controls.Add(BoundsLbl("max"));
        zFlow.Controls.Add(_boundsMaxZNud);
        layout.Controls.Add(zFlow, 0, 10);

        _boundsMinXNud.ValueChanged += OnBoundsNudChanged;
        _boundsMaxXNud.ValueChanged += OnBoundsNudChanged;
        _boundsMinZNud.ValueChanged += OnBoundsNudChanged;
        _boundsMaxZNud.ValueChanged += OnBoundsNudChanged;

        var save = CreateFlatButton("Save  worldpainter.bin");
        save.MinimumSize = new Size(0, 32);
        save.Click += (_, _) => SaveData();
        layout.Controls.Add(save, 0, 11);

        var reload = CreateFlatButton("Reload from file");
        reload.MinimumSize = new Size(0, 32);
        reload.Click += (_, _) =>
        {
            LoadTopdownAssets();
            TryLoadData(silent: false);
            ApplyTopdownBoundsFromJson();
            EnsureAllLayerGrids();
            _mapPanel.Invalidate();
        };
        layout.Controls.Add(reload, 0, 12);

        var exportBtn = CreateFlatButton("Export PSGs (standalone debug)");
        exportBtn.MinimumSize = new Size(0, 32);
        exportBtn.BackColor = Color.FromArgb(50, 50, 70);
        exportBtn.Click += (_, _) => BuildAndExportPsgs();
        layout.Controls.Add(exportBtn, 0, 13);

        var help = new Label
        {
            Text =
                "Game horizontal plane: X = east, Z = north.\n" +
                "Row 0 = south (min Z), row increases northward.\n\n" +
                "Load scene_topdown_2k.json (schema v2) from BlenRose for 1:1 bounds.\n\n" +
                "Left-drag = paint · Right-drag = erase\n" +
                "Shift+drag = box paint/erase",
            Dock = DockStyle.Fill,
            AutoSize = true,
            MaximumSize = new Size(w - 24, 0),
            ForeColor = ForeColorMuted,
            BackColor = BackColorMain
        };
        layout.Controls.Add(help, 0, 14);

        panel.Controls.Add(layout);
        return panel;
    }

    private void SyncGridFromNud() =>
        _mapPanel.SetGridSize((int)_gridColsNud.Value, (int)_gridRowsNud.Value);

    private NumericUpDown MakeBoundsNud(double defaultVal) => new()
    {
        // Wide range so BlenRose / large arenas are not clamped (±32k was a silent bounds corruptor).
        Minimum = -10_000_000, Maximum = 10_000_000, Value = (decimal)defaultVal,
        DecimalPlaces = 1, Width = 70,
        BackColor = BackColorInput, ForeColor = ForeColorPrimary
    };

    private void SetBoundsUi(double minX, double maxX, double minZ, double maxZ)
    {
        static decimal Clamp(double v, decimal lo, decimal hi) =>
            (decimal)Math.Clamp(v, (double)lo, (double)hi);

        _boundsMinXNud.ValueChanged -= OnBoundsNudChanged;
        _boundsMaxXNud.ValueChanged -= OnBoundsNudChanged;
        _boundsMinZNud.ValueChanged -= OnBoundsNudChanged;
        _boundsMaxZNud.ValueChanged -= OnBoundsNudChanged;

        _boundsMinXNud.Value = Clamp(minX, _boundsMinXNud.Minimum, _boundsMinXNud.Maximum);
        _boundsMaxXNud.Value = Clamp(maxX, _boundsMaxXNud.Minimum, _boundsMaxXNud.Maximum);
        _boundsMinZNud.Value = Clamp(minZ, _boundsMinZNud.Minimum, _boundsMinZNud.Maximum);
        _boundsMaxZNud.Value = Clamp(maxZ, _boundsMaxZNud.Minimum, _boundsMaxZNud.Maximum);

        _boundsMinXNud.ValueChanged += OnBoundsNudChanged;
        _boundsMaxXNud.ValueChanged += OnBoundsNudChanged;
        _boundsMinZNud.ValueChanged += OnBoundsNudChanged;
        _boundsMaxZNud.ValueChanged += OnBoundsNudChanged;

        SyncBoundsToPanel();
    }

    private void OnBoundsNudChanged(object? sender, EventArgs e) => SyncBoundsToPanel();

    private void SyncBoundsToPanel()
    {
        double minX = (double)_boundsMinXNud.Value;
        double maxX = (double)_boundsMaxXNud.Value;
        double minZ = (double)_boundsMinZNud.Value;
        double maxZ = (double)_boundsMaxZNud.Value;
        if (maxX <= minX || maxZ <= minZ) return;
        double cx = (minX + maxX) * 0.5;
        double cz = (minZ + maxZ) * 0.5;
        double hx = (maxX - minX) * 0.5;
        double hz = (maxZ - minZ) * 0.5;
        _mapPanel.ConfigurePaintSpace(cx, cz, hx, hz,
            (int)_gridColsNud.Value, (int)_gridRowsNud.Value,
            northAtImageTop: _paintNorthAtImageTop);
        _mapPanel.Invalidate();
    }

    private Button CreateFlatButton(string text)
    {
        var b = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = BackColorInput,
            ForeColor = ForeColorPrimary,
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(60, 60, 60);
        b.FlatAppearance.BorderSize = 1;
        return b;
    }

    private ComboBox CreateCombo() => new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat,
        BackColor = BackColorInput,
        ForeColor = ForeColorPrimary,
        IntegralHeight = false
    };

    // ── Catalog / layer combo ─────────────────────────────────────────────────

    private void PopulateLayerCombo()
    {
        _layerCombo.BeginUpdate();
        _layerCombo.Items.Clear();
        foreach (var l in WorldPainterCatalog.Layers)
            _layerCombo.Items.Add(l);
        _layerCombo.EndUpdate();
        if (_layerCombo.Items.Count > 0)
            _layerCombo.SelectedIndex = 0;
        else
            OnLayerChanged();
    }

    private void OnLayerChanged()
    {
        _keyCombo.BeginUpdate();
        _keyCombo.Items.Clear();
        if (_layerCombo.SelectedItem is WorldPainterCatalog.LayerEntry le)
        {
            foreach (var k in WorldPainterCatalog.KeysFor(le.Guid))
                _keyCombo.Items.Add(k);
        }
        _keyCombo.EndUpdate();
        if (_keyCombo.Items.Count > 0)
            _keyCombo.SelectedIndex = 0;
    }

    // ── Top-down image ────────────────────────────────────────────────────────

    private void LoadTopdownAssets()
    {
        string pngPath = Path.Combine(SourceFolder, TopdownPngFileName);
        if (File.Exists(pngPath))
        {
            try
            {
                // Decode from stream + copy so the file is not locked (BlenRose can overwrite while editor is open).
                using var fs = new FileStream(pngPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var decoded = Image.FromStream(fs, false, false);
                _mapPanel.MapImage = new Bitmap(decoded);
            }
            catch
            {
                _mapPanel.MapImage = null;
            }
        }
        else
            _mapPanel.MapImage = null;

        _loadedTopdownJson = null;
        string jsonPath = Path.Combine(SourceFolder, TopdownJsonFileName);
        if (File.Exists(jsonPath))
        {
            try { _loadedTopdownJson = File.ReadAllText(jsonPath); }
            catch { _loadedTopdownJson = null; }
        }
    }

    /// <summary>
    /// Applies <c>scene_topdown_2k.json</c> world bounds after <see cref="TryLoadData"/> so paint grids from
    /// <c>worldpainter.bin</c> are kept while collision-space min/max track BlenRose (schema v2).
    /// </summary>
    private void ApplyTopdownBoundsFromJson()
    {
        _topdownContractV2 = false;
        if (!TryParseBlenRoseTopdownV2(out double minX, out double maxX, out double minZ, out double maxZ, out bool northAtImageTop))
        {
            _paintNorthAtImageTop = true;
            SyncBoundsToPanel();
            return;
        }
        _topdownContractV2 = true;
        _paintNorthAtImageTop = northAtImageTop;
        SetBoundsUi(minX, maxX, minZ, maxZ);
    }

    private bool TryParseBlenRoseTopdownV2(
        out double minX, out double maxX, out double minZ, out double maxZ, out bool northAtImageTop)
    {
        minX = maxX = minZ = maxZ = 0;
        northAtImageTop = true;
        string? json = _loadedTopdownJson;
        if (string.IsNullOrWhiteSpace(json))
        {
            string path = Path.Combine(SourceFolder, TopdownJsonFileName);
            if (!File.Exists(path)) return false;
            try { json = File.ReadAllText(path); }
            catch { return false; }
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!TryGetJsonSchemaVersion(root, out int schema) || schema < 2)
                return false;
            if (!root.TryGetProperty("worldpainter", out var wp))
                return false;
            if (!wp.TryGetProperty("world_min_xz", out var wmin)
                || wmin.ValueKind != JsonValueKind.Array || wmin.GetArrayLength() < 2)
                return false;
            if (!wp.TryGetProperty("world_max_xz", out var wmax)
                || wmax.ValueKind != JsonValueKind.Array || wmax.GetArrayLength() < 2)
                return false;
            minX = wmin[0].GetDouble();
            minZ = wmin[1].GetDouble();
            maxX = wmax[0].GetDouble();
            maxZ = wmax[1].GetDouble();
            if (maxX <= minX || maxZ <= minZ)
                return false;
            if (!(double.IsFinite(minX) && double.IsFinite(maxX) && double.IsFinite(minZ) && double.IsFinite(maxZ)))
                return false;
            northAtImageTop = true;
            if (wp.TryGetProperty("north_at_image_top", out var north))
            {
                if (north.ValueKind == JsonValueKind.False)
                    northAtImageTop = false;
                else if (north.ValueKind == JsonValueKind.True)
                    northAtImageTop = true;
            }
            return true;
        }
        catch { return false; }
    }

    private static bool TryGetJsonSchemaVersion(JsonElement root, out int schema)
    {
        schema = 0;
        if (!root.TryGetProperty("schema_version", out var el))
            return false;
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                schema = (int)Math.Round(el.GetDouble());
                return true;
            case JsonValueKind.String:
                return int.TryParse(el.GetString(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out schema);
            default:
                return false;
        }
    }

    // ── Save / Load ───────────────────────────────────────────────────────────

    private void SaveData()
    {
        EnsureAllLayerGrids();
        double minX = (double)_boundsMinXNud.Value;
        double maxX = (double)_boundsMaxXNud.Value;
        double minZ = (double)_boundsMinZNud.Value;
        double maxZ = (double)_boundsMaxZNud.Value;

        var layers = new List<WpSimpleLayer>();
        foreach (var layer in WorldPainterCatalog.Layers.OrderBy(l => l.Guid))
        {
            var grid = EnsureGrid(layer.Guid);
            var painted = new List<WpSparseCell>();
            for (int i = 0; i < grid.Length; i++)
            {
                var c = grid[i];
                if (!c.IsEmpty)
                    painted.Add(new WpSparseCell((uint)i, c.Lo, c.Hi));
            }
            if (painted.Count > 0)
                layers.Add(new WpSimpleLayer(layer.Guid, painted));
        }

        var doc = new WpSimpleDocument(
            _mapPanel.GridColumns, _mapPanel.GridRows,
            minX, minZ, maxX, maxZ, layers);

        try
        {
            WpSimpleFile.Save(SourceFolder, doc);
            MessageBox.Show(this,
                $"Saved:\n{Path.Combine(SourceFolder, WpSimpleFile.FileName)}\n\n" +
                $"{layers.Count} layer(s), bounds X[{minX:F1}..{maxX:F1}] Z[{minZ:F1}..{maxZ:F1}]",
                "WorldPainter", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "WorldPainter — Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool TryLoadData(bool silent)
    {
        var doc = WpSimpleFile.TryLoad(SourceFolder, out string? err);
        if (doc is null)
        {
            if (!silent)
                MessageBox.Show(this, err ?? "File not found.", "WorldPainter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        _gridColsNud.Value = Math.Clamp(doc.Cols, (int)_gridColsNud.Minimum, (int)_gridColsNud.Maximum);
        _gridRowsNud.Value = Math.Clamp(doc.Rows, (int)_gridRowsNud.Minimum, (int)_gridRowsNud.Maximum);
        SetBoundsUi(doc.MinX, doc.MaxX, doc.MinZ, doc.MaxZ);

        _grids.Clear();
        int n = doc.Cols * doc.Rows;
        foreach (var layer in doc.Layers)
        {
            var arr = new WpCell[n];
            foreach (var s in layer.Painted)
                if (s.Idx < (uint)n)
                    arr[s.Idx] = new WpCell(s.Lo, s.Hi);
            _grids[layer.Guid] = arr;
        }
        EnsureAllLayerGrids();

        if (!silent)
            MessageBox.Show(this,
                $"Loaded {doc.Layers.Count} layer(s), {doc.Cols}×{doc.Rows} cells.",
                "WorldPainter", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return true;
    }

    // ── PSG export ────────────────────────────────────────────────────────────

    private void BuildAndExportPsgs()
    {
        EnsureAllLayerGrids();
        double minX = (double)_boundsMinXNud.Value;
        double maxX = (double)_boundsMaxXNud.Value;
        double minZ = (double)_boundsMinZNud.Value;
        double maxZ = (double)_boundsMaxZNud.Value;

        if (maxX <= minX || maxZ <= minZ)
        {
            MessageBox.Show(this, "Paint map bounds are invalid (zero or negative size).\nLoad a scene_topdown_2k.json first.", "WorldPainter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var grids = new Dictionary<ulong, WpCell[]>();
        foreach (var layer in WorldPainterCatalog.Layers)
        {
            var g = EnsureGrid(layer.Guid);
            if (g.Any(c => !c.IsEmpty))
                grids[layer.Guid] = g;
        }

        float tileSize = (float)_tileSizeNud.Value;
        var logs = new List<string>();

        WorldPainterExporter.ExportResult result;
        try
        {
            result = WorldPainterExporter.Export(
                SourceFolder,
                _mapPanel.GridColumns,
                _mapPanel.GridRows,
                minX, minZ, maxX, maxZ,
                grids,
                tileSize,
                log: s => logs.Add(s));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "WorldPainter — Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        MessageBox.Show(this,
            string.Join("\n", logs),
            $"WorldPainter Export — {result.Emitted} PSG(s) written",
            MessageBoxButtons.OK,
            result.Emitted > 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static bool TryParseGuid(string text, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out value);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _mapPanel.MapImage = null;
        base.OnFormClosed(e);
    }
}
