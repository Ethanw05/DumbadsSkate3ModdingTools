using System.Collections.Concurrent;
using System.Text;
using System.Runtime.InteropServices;
using ArenaBuilder.Build;
using ArenaBuilder.Glb;

namespace ArenaBuilder.WinForms;

/// <summary>
/// Modern dark-theme UI for building tiled PSGs.
/// </summary>
public sealed class MainForm : Form
{
    // The build log is a multiline ReadOnly TextBox (plain Edit), not a RichTextBox. RichEdit
    // and even TextBoxBase.AppendText/ScrollToCaret can play the system default beep on every
    // update (EM_SCROLLCARET with nowhere to go). We append with WM_SETREDRAW + EM_SETSEL +
    // EM_REPLACESEL, then scroll with WM_VSCROLL+SB_BOTTOM only.
    private const int WmVscroll = 0x115;
    private const int SbBottom = 7;
    private const int WmSetredraw = 0x0B;
    private const int EmSetsel = 0x00B1;
    private const int EmReplacesel = 0x00C2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern nint SendMessageW(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern nint SendMessageW(nint hWnd, int msg, nint wParam, string lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(nint hWnd);

    private static void ScrollEditToBottomNoBeep(Control log)
    {
        if (!log.IsHandleCreated) return;
        _ = SendMessageW(log.Handle, WmVscroll, (nint)SbBottom, 0);
    }

    /// <summary>Append to a multiline TextBox using native messages (no <see cref="TextBoxBase.AppendText"/> beep path).</summary>
    private static void AppendToEditNoBeep(TextBox log, string chunk)
    {
        if (string.IsNullOrEmpty(chunk) || !log.IsHandleCreated) return;
        nint h = log.Handle;
        _ = SendMessageW(h, WmSetredraw, 0, 0);
        try
        {
            int len = GetWindowTextLengthW(h);
            _ = SendMessageW(h, EmSetsel, (nint)len, (nint)len);
            _ = SendMessageW(h, EmReplacesel, 0, chunk);
        }
        finally
        {
            _ = SendMessageW(h, WmSetredraw, 1, 0);
        }
        log.Invalidate();
    }
    // Modern dark palette (VS-inspired, easy on the eyes)
    private static readonly Color BackColorMain = Color.FromArgb(37, 37, 38);
    private static readonly Color BackColorSurface = Color.FromArgb(45, 45, 48);
    private static readonly Color BackColorInput = Color.FromArgb(30, 30, 30);
    private static readonly Color ForeColorPrimary = Color.FromArgb(241, 241, 241);
    private static readonly Color ForeColorMuted = Color.FromArgb(180, 180, 180);
    private static readonly Color Accent = Color.FromArgb(0, 122, 204);
    private static readonly Color AccentHover = Color.FromArgb(28, 151, 234);
    private static readonly Color Danger = Color.FromArgb(200, 60, 60);
    private static readonly Color DangerHover = Color.FromArgb(220, 80, 80);
    private static readonly Color BorderMuted = Color.FromArgb(60, 60, 60);

    private static readonly Font FontUi = new("Segoe UI", 9.25f);
    private static readonly Font FontHeading = new("Segoe UI Semibold", 10f);

    private readonly TextBox _folderText;
    private readonly Button _browseButton;
    private readonly TextBox _distOutputText;
    private readonly TextBox _mapNameText;
    private readonly CheckBox _cpresGlobalOnlyCheck;
    private readonly CheckBox _cpresOnlyCheck;
    private readonly CheckBox _csimOnlyCheck;
    private readonly CheckBox _emitNavPowerCheck;
    private readonly ToolTip _uiToolTip = new();
    private readonly Button _previewButton;
    private readonly Button _worldPainterButton;
    private readonly Label _normalPreviewSettingsLabel;
    private readonly Button _buildButton;
    private readonly Button _cancelButton;
    private readonly TextBox _log;
    private CancellationTokenSource? _buildCts;

    // Background-thread-friendly log batching. Worker threads (Parallel.ForEach over GLBs in the
    // texture build) can produce thousands of log lines per second; per-message BeginInvoke to the
    // log saturates the UI message queue. We buffer messages and drain in one pass
    // on a System.Windows.Forms.Timer tick.
    private readonly ConcurrentQueue<string> _pendingLogLines = new();
    private readonly System.Windows.Forms.Timer _logFlushTimer;
    private const int LogFlushIntervalMs = 100;
    private const int LogMaxChars = 500_000;
    private const int LogMaxLinesPerFlush = 4_000;

    public MainForm()
    {
        Text = "ArenaBuilder — Build Tiled Arenas";
        Size = new Size(920, 680);
        MinimumSize = new Size(640, 480);
        BackColor = BackColorMain;
        ForeColor = ForeColorPrimary;
        Font = FontUi;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;

        // Folder row
        _folderText = new TextBox
        {
            ReadOnly = true,
            Dock = DockStyle.Fill,
            BackColor = BackColorInput,
            ForeColor = ForeColorPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = FontUi,
            Margin = new Padding(0, 0, 8, 0)
        };
        _browseButton = CreateAccentButton("Browse…", 100);
        _browseButton.Dock = DockStyle.Right;

        var folderHintLabel = new Label
        {
            Text = "Select folder that contains GLBs and JSONs",
            AutoSize = true,
            ForeColor = ForeColorMuted,
            BackColor = BackColorMain,
            Font = FontUi
        };

        var folderPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Padding = new Padding(PaddingPx, PaddingPx, PaddingPx, PaddingPx),
            ColumnCount = 2,
            RowCount = 2,
            BackColor = BackColorMain
        };
        folderPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        folderPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        folderPanel.Controls.Add(_folderText, 0, 0);
        folderPanel.Controls.Add(_browseButton, 1, 0);
        folderPanel.SetColumnSpan(folderHintLabel, 2);
        folderPanel.Controls.Add(folderHintLabel, 0, 1);

        // DIST output + Map name (DONOTREMOVE is always next to the EXE)
        _distOutputText = new TextBox
        {
            ReadOnly = true,
            BackColor = BackColorInput,
            ForeColor = ForeColorPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = FontUi,
            Dock = DockStyle.Fill
        };
        var distOutputBrowse = CreateAccentButton("Browse…", 80);
        _mapNameText = new TextBox
        {
            BackColor = BackColorInput,
            ForeColor = ForeColorPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = FontUi
        };

        var packPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Padding = new Padding(PaddingPx, PaddingPx, PaddingPx, PaddingPx),
            Margin = new Padding(0, 20, 0, 0),
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = BackColorMain
        };
        packPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        packPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        var distFolderLabel = new Label
        {
            Text = "DIST output folder",
            ForeColor = ForeColorPrimary,
            BackColor = BackColorMain,
            Font = FontUi,
            AutoSize = true
        };
        packPanel.Controls.Add(distFolderLabel, 0, 0);
        packPanel.SetColumnSpan(distFolderLabel, 2);
        packPanel.Controls.Add(_distOutputText, 0, 1);
        packPanel.Controls.Add(distOutputBrowse, 1, 1);
        var mapNameLabel = new Label
        {
            Text = "Map name (DIST_ added automatically)",
            ForeColor = ForeColorPrimary,
            BackColor = BackColorMain,
            Font = FontUi,
            AutoSize = true
        };
        packPanel.Controls.Add(mapNameLabel, 0, 2);
        packPanel.SetColumnSpan(mapNameLabel, 2);
        packPanel.Controls.Add(_mapNameText, 0, 3);
        packPanel.SetColumnSpan(_mapNameText, 2);

        _cpresGlobalOnlyCheck = new CheckBox
        {
            Text = "Global only",
            ForeColor = ForeColorPrimary,
            BackColor = BackColorMain,
            Font = FontUi,
            AutoSize = true,
            Checked = false
        };
        _cpresOnlyCheck = new CheckBox
        {
            Text = "cPres Only",
            ForeColor = ForeColorPrimary,
            BackColor = BackColorMain,
            Font = FontUi,
            AutoSize = true,
            Checked = false
        };
        _csimOnlyCheck = new CheckBox
        {
            Text = "cSim Only",
            ForeColor = ForeColorPrimary,
            BackColor = BackColorMain,
            Font = FontUi,
            AutoSize = true,
            Checked = false
        };
        _emitNavPowerCheck = new CheckBox
        {
            Text = "Emit NavPower PSG",
            ForeColor = ForeColorPrimary,
            BackColor = BackColorMain,
            Font = FontUi,
            AutoSize = true,
            Checked = true
        };
        _uiToolTip.SetToolTip(
            _emitNavPowerCheck,
            "One Skate-style NavPower .psg per collision tile (VersionData + tNavPowerData + TOC), beside collision/WP. " +
            "Disabled when cPres Only is checked (no cSim output).");
        var optionsLabel = new Label
        {
            Text = "Build options",
            ForeColor = ForeColorPrimary,
            BackColor = BackColorMain,
            Font = FontUi,
            AutoSize = true
        };
        packPanel.Controls.Add(optionsLabel, 0, 4);
        packPanel.SetColumnSpan(optionsLabel, 2);
        var optionsFlow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            BackColor = BackColorMain,
            Padding = new Padding(0, 4, 0, 0)
        };
        optionsFlow.Controls.Add(_cpresGlobalOnlyCheck);
        optionsFlow.Controls.Add(_cpresOnlyCheck);
        optionsFlow.Controls.Add(_csimOnlyCheck);
        optionsFlow.Controls.Add(_emitNavPowerCheck);
        packPanel.Controls.Add(optionsFlow, 0, 5);
        packPanel.SetColumnSpan(optionsFlow, 2);

        // Build DIST (build + pack) / Cancel
        _previewButton = CreateAccentButton("Normal Texture Editor…", 220);
        _previewButton.Dock = DockStyle.None;
        _previewButton.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _worldPainterButton = CreateAccentButton("WorldPainter…", 220);
        _worldPainterButton.Dock = DockStyle.None;
        _worldPainterButton.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _normalPreviewSettingsLabel = new Label
        {
            AutoSize = false,
            Width = 220,
            Height = 54,
            ForeColor = ForeColorPrimary,
            BackColor = BackColorMain,
            Font = FontUi,
            TextAlign = ContentAlignment.BottomLeft
        };
        _normalPreviewSettingsLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _buildButton = CreatePrimaryButton("Build DIST", 48);
        _cancelButton = CreateDangerButton("Cancel build", 36);
        _cancelButton.Enabled = false;

        var buttonPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 128,
            Padding = new Padding(PaddingPx, 8, PaddingPx, 8),
            BackColor = BackColorMain
        };
        buttonPanel.Controls.Add(_previewButton);
        buttonPanel.Controls.Add(_worldPainterButton);
        buttonPanel.Controls.Add(_normalPreviewSettingsLabel);
        buttonPanel.Controls.Add(_buildButton);
        buttonPanel.Controls.Add(_cancelButton);
        int editorColumnX = PaddingPx + 200;
        _normalPreviewSettingsLabel.Location = new Point(editorColumnX, 8);
        _previewButton.Location = new Point(editorColumnX, 68);
        _worldPainterButton.Location = new Point(editorColumnX + 220 + 8, 68);
        _buildButton.Location = new Point(PaddingPx, 8);
        _cancelButton.Location = new Point(PaddingPx, 8 + 48 + 8);

        distOutputBrowse.Click += (_, _) => BrowseDistOutput();

        // Log: multiline TextBox (not RichTextBox) + native append — see AppendToEditNoBeep.
        _log = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            MaxLength = 0,
            HideSelection = true,
            ShortcutsEnabled = false,
            AcceptsReturn = true,
            AcceptsTab = false,
            Dock = DockStyle.Fill,
            BackColor = BackColorInput,
            ForeColor = ForeColorPrimary,
            BorderStyle = BorderStyle.None,
            Font = TryGetMonospaceFont(),
            WordWrap = false,
            ScrollBars = ScrollBars.Both
        };

        _logFlushTimer = new System.Windows.Forms.Timer { Interval = LogFlushIntervalMs };
        _logFlushTimer.Tick += (_, _) => FlushPendingLogLines();
        _logFlushTimer.Start();
        var logPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(PaddingPx, PaddingPx, PaddingPx, PaddingPx),
            BackColor = BackColorMain
        };
        logPanel.Controls.Add(_log);

        var root = new Panel { Dock = DockStyle.Fill, BackColor = BackColorMain };
        root.Controls.Add(logPanel);
        root.Controls.Add(buttonPanel);
        root.Controls.Add(packPanel);
        root.Controls.Add(folderPanel);

        Controls.Add(root);

        _browseButton.Click += (_, _) => BrowseFolder();
        _buildButton.Click += async (_, _) => await BuildDistAsync();
        _cancelButton.Click += (_, _) => CancelBuild();
        _previewButton.Click += (_, _) => OpenTexturePreview();
        _worldPainterButton.Click += (_, _) => OpenWorldPainter();
        _cpresOnlyCheck.CheckedChanged += (_, _) =>
        {
            if (_cpresOnlyCheck.Checked)
                _csimOnlyCheck.Checked = false;
            SyncEmitNavPowerCheckState();
        };
        _csimOnlyCheck.CheckedChanged += (_, _) =>
        {
            if (_csimOnlyCheck.Checked)
                _cpresOnlyCheck.Checked = false;
        };
        SyncEmitNavPowerCheckState();
        RefreshNormalPreviewSettingsLabel();
    }

    private const int PaddingPx = 16;

    private static Font TryGetMonospaceFont()
    {
        try
        {
            return new Font("Cascadia Code", 9f);
        }
        catch
        {
            return new Font("Consolas", 9f);
        }
    }

    private static Button CreateAccentButton(string text, int width)
    {
        var b = new Button
        {
            Text = text,
            Size = new Size(width, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = BackColorSurface,
            ForeColor = Accent,
            FlatAppearance = { BorderColor = BorderMuted, BorderSize = 1 },
            Font = FontUi,
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(52, 52, 54);
        return b;
    }

    private static Button CreatePrimaryButton(string text, int height)
    {
        var b = new Button
        {
            Text = text,
            Width = 180,
            Height = height,
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            FlatAppearance = { BorderSize = 0 },
            Font = FontHeading,
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.MouseOverBackColor = AccentHover;
        return b;
    }

    private static Button CreateDangerButton(string text, int height)
    {
        var b = new Button
        {
            Text = text,
            Width = 160,
            Height = height,
            FlatStyle = FlatStyle.Flat,
            BackColor = Danger,
            ForeColor = Color.White,
            FlatAppearance = { BorderSize = 0 },
            Font = FontUi,
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.MouseOverBackColor = DangerHover;
        return b;
    }

    private static string GetDonotRemovePath()
    {
        string? exeDir = Path.GetDirectoryName(Application.ExecutablePath);
        return Path.Combine(string.IsNullOrEmpty(exeDir) ? "." : exeDir, "DONOTREMOVE");
    }

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog { ShowNewFolderButton = false };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _folderText.Text = dlg.SelectedPath;
    }

    private void BrowseDistOutput()
    {
        using var dlg = new FolderBrowserDialog { ShowNewFolderButton = true };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _distOutputText.Text = dlg.SelectedPath;
    }

    private async Task BuildDistAsync()
    {
        string folder = _folderText.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            Log("Select a valid folder (GLBs and JSONs) first.");
            return;
        }
        string distOutputDir = _distOutputText.Text.Trim();
        if (string.IsNullOrWhiteSpace(distOutputDir) || !Directory.Exists(distOutputDir))
        {
            Log("Select a valid DIST output folder.");
            return;
        }
        string mapName = _mapNameText.Text.Trim();
        if (string.IsNullOrWhiteSpace(mapName))
        {
            Log("Enter a map name.");
            return;
        }
        if (mapName.StartsWith("DIST_", StringComparison.OrdinalIgnoreCase))
            mapName = mapName.Substring(5);
        string donotRemoveDir = GetDonotRemovePath();
        if (!Directory.Exists(donotRemoveDir))
        {
            Log($"DONOTREMOVE folder not found: {donotRemoveDir}. Put it in the same folder as this app.");
            return;
        }
        string streamToolPath = Path.Combine(donotRemoveDir, DistPackRunner.StreamToolExeName);
        if (!File.Exists(streamToolPath))
        {
            Log($"Stream File Tool.exe not found: {streamToolPath}");
            return;
        }

        _buildButton.Enabled = false;
        _cancelButton.Enabled = true;
        _buildCts = new CancellationTokenSource();
        string distRoot = Path.Combine(distOutputDir, "DIST_" + mapName);

        // Log into both the UI and a build.log file inside the DIST root.
        Directory.CreateDirectory(distRoot);
        string logFilePath = Path.Combine(distRoot, "build.log");
        var logLock = new object();

        // AutoFlush=false + periodic flush via the file flush timer; per-message Flush() was
        // serializing all build worker threads through synchronous file I/O and slowing the build.
        // The StreamWriter still flushes on Dispose at the end of the build (using-block).
        using var logWriter = new StreamWriter(logFilePath, append: false) { AutoFlush = false };
        using var fileFlushTimer = new System.Threading.Timer(_ =>
        {
            lock (logLock)
            {
                try { logWriter.Flush(); } catch { }
            }
        }, null, dueTime: 1000, period: 1000);
        void LogToUiAndFile(string message)
        {
            Log(message);
            lock (logLock)
            {
                logWriter.WriteLine(message);
            }
        }

        var buildOptions = GetTileBuildOptions();
        if (mapName.Contains("_Proxy", StringComparison.OrdinalIgnoreCase))
            buildOptions = buildOptions with { FolderSuffix = "_proxy" };
        try
        {
            LogToUiAndFile("Building PSGs...");
            await Task.Run(() => BuildPsGsToDist(folder, distRoot, buildOptions, _buildCts.Token, LogToUiAndFile), _buildCts.Token);
            if (_buildCts.Token.IsCancellationRequested)
                return;
            // Resume on the UI SynchronizationContext: drain the batched log queue completely so
            // "Packing" is not stuck behind 10k+ pending lines, and so build.log is flushed before
            // the long Stream File Tool runs.
            while (!_pendingLogLines.IsEmpty)
                FlushPendingLogLines();
            lock (logLock)
            {
                try { logWriter.Flush(); } catch { }
            }
            LogToUiAndFile("Packing to DIST...");
            await Task.Run(() => DistPackRunner.Run(distRoot, distRoot, donotRemoveDir, streamToolPath, mapName, buildOptions, deleteUnpackedAfterPack: true, LogToUiAndFile, _buildCts.Token), _buildCts.Token);
            if (!_buildCts.Token.IsCancellationRequested)
                BuildMemory.TryCompactManagedHeap();
        }
        catch (OperationCanceledException)
        {
            Log("Build cancelled by user.");
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
        }
        finally
        {
            _buildCts?.Dispose();
            _buildCts = null;
            _cancelButton.Enabled = false;
            _buildButton.Enabled = true;
        }
    }

    private void CancelBuild()
    {
        if (_buildCts == null || _buildCts.IsCancellationRequested)
            return;
        _buildCts.Cancel();
        _cancelButton.Enabled = false;
        Log("Cancellation requested…");
    }

    private void OpenTexturePreview()
    {
        var preview = new TextureSynthPreviewForm();
        preview.SettingsSaved += _ => RefreshNormalPreviewSettingsLabel();
        preview.Show(this);
    }

    private void OpenWorldPainter()
    {
        string folder = _folderText.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            MessageBox.Show(
                this,
                "Select a folder that contains your GLBs (and BlenRose top-down assets) on the main window first.",
                "WorldPainter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (!Directory.Exists(folder))
        {
            MessageBox.Show(this, "The selected folder does not exist.", "WorldPainter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        new WorldPainterForm(folder).Show(this);
    }

    private void RefreshNormalPreviewSettingsLabel()
    {
        var s = NormalSynthSettingsStore.Get();
        _normalPreviewSettingsLabel.Text =
            $"Strength: {s.Strength:0.00}\n" +
            $"Level: {s.Level:0.0}\n" +
            $"Blur: {s.BlurSharp:0.00}";
    }

    private void BuildPsGs(string folder, CancellationToken cancellationToken)
    {
        var glbs = Directory.GetFiles(folder, "*.glb", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (glbs.Length == 0)
        {
            Log("No .glb files found in the selected folder.");
            return;
        }

        const float meshScale = 1f;
        var options = GetTileBuildOptions();
        TileBuildPipeline.Build(folder, glbs, options, meshScale, Log, cancellationToken);
    }

    private void BuildPsGsToDist(string inputFolder, string distRoot, TileBuildOptions buildOptions, CancellationToken cancellationToken, Action<string> log)
    {
        var glbs = Directory.GetFiles(inputFolder, "*.glb", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (glbs.Length == 0)
        {
            log("No .glb files found in the selected folder.");
            return;
        }

        Directory.CreateDirectory(distRoot);

        const float meshScale = 1f;
        TileBuildPipeline.Build(inputFolder, distRoot, glbs, buildOptions, meshScale, log, cancellationToken);
    }

    private TileBuildOptions GetTileBuildOptions()
    {
        var normalSynth = NormalSynthSettingsStore.Get();
        return new TileBuildOptions
        {
            GlobalOnly = _cpresGlobalOnlyCheck.Checked,
            CPresOnly = _cpresOnlyCheck.Checked,
            CSimOnly = _csimOnlyCheck.Checked,
            EmitNavPower = _emitNavPowerCheck.Checked && !_cpresOnlyCheck.Checked,
            NormalSynthStrength = normalSynth.Strength,
            NormalSynthLevel = normalSynth.Level,
            NormalSynthBlurSharp = normalSynth.BlurSharp
        };
    }

    /// <summary>NavPower rides the cSim pipeline; gray out when cPres Only (no collision).</summary>
    private void SyncEmitNavPowerCheckState()
    {
        bool allow = !_cpresOnlyCheck.Checked;
        _emitNavPowerCheck.Enabled = allow;
        if (!allow)
            _emitNavPowerCheck.ForeColor = ForeColorMuted;
        else
            _emitNavPowerCheck.ForeColor = ForeColorPrimary;
    }

    /// <summary>
    /// Thread-safe log entry point. Enqueues the line; <see cref="FlushPendingLogLines"/> drains
    /// the queue on a UI Timer tick (~100 ms). Avoids saturating the UI message pump when worker
    /// threads (e.g. parallel texture build over GLBs) generate thousands of lines per second.
    /// </summary>
    private void Log(string message)
    {
        _pendingLogLines.Enqueue(message);
    }

    /// <summary>
    /// UI-thread drain. Concatenates up to <see cref="LogMaxLinesPerFlush"/> queued lines into one
    /// <see cref="AppendToEditNoBeep"/> call, then <see cref="ScrollEditToBottomNoBeep"/>. Trims if
    /// the buffer exceeds <see cref="LogMaxChars"/>.
    /// </summary>
    private void FlushPendingLogLines()
    {
        if (_pendingLogLines.IsEmpty)
            return;

        var sb = new StringBuilder();
        int drained = 0;
        while (drained < LogMaxLinesPerFlush && _pendingLogLines.TryDequeue(out var line))
        {
            sb.Append(line);
            sb.Append(Environment.NewLine);
            drained++;
        }
        if (sb.Length == 0)
            return;

        AppendToEditNoBeep(_log, sb.ToString());

        if (_log.TextLength > LogMaxChars)
        {
            string full = _log.Text;
            int removeCount = full.Length - (LogMaxChars * 3 / 4);
            int firstNewLineAfterCut = full.IndexOf('\n', removeCount);
            if (firstNewLineAfterCut > 0)
                removeCount = firstNewLineAfterCut + 1;
            removeCount = Math.Max(0, Math.Min(removeCount, full.Length));
            if (removeCount > 0)
                _log.Text = full[removeCount..];
        }

        ScrollEditToBottomNoBeep(_log);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try
        {
            _logFlushTimer?.Stop();
            FlushPendingLogLines();
        }
        catch { }
        base.OnFormClosed(e);
    }
}
