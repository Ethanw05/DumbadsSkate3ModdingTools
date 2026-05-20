using ArenaBuilder.Texture;
using OpenTK.GLControl;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using SharpGLTF.Memory;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ArenaBuilder.WinForms;

internal sealed class TextureSynthPreviewForm : Form
{
    public event Action<DerivedTextureGenerator.NormalSynthSettings>? SettingsSaved;

    private readonly List<string> _glbFiles = new();
    private int _glbIndex = -1;
    private float _cubeAngle;
    private float _cubePitch = 0.35f;
    private bool _isDraggingViewport;
    private Point _lastMousePos;
    private readonly CubeGlRenderer _renderer = new();

    private readonly PictureBox _diffuseBox;
    private readonly PictureBox _normalBox;
    private readonly GLControl _glControl;
    private readonly Label _statusLabel;
    private readonly TrackBar _strengthTrack;
    private readonly TrackBar _levelTrack;
    private readonly TrackBar _blurTrack;
    private readonly Label _strengthValue;
    private readonly Label _levelValue;
    private readonly Label _blurValue;
    private readonly TextBox _strengthInput;
    private readonly TextBox _levelInput;
    private readonly TextBox _blurInput;
    private bool _syncingSliderInputs;

    private Bitmap? _sourceBitmap;
    private Bitmap? _normalBitmap;
    private byte[]? _currentDiffuseBytes;
    private string _currentDiffuseName = "Diffuse";

    public TextureSynthPreviewForm()
    {
        Text = "Normal Texture Editor (Diffuse -> Normal -> Render)";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1140, 760);
        Size = new Size(1300, 840);

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 150,
            Padding = new Padding(8),
            ColumnCount = 6
        };
        top.RowCount = 2;
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        var loadButton = new Button { Text = "Load GLB...", Width = 110, Height = 28, Dock = DockStyle.Fill };
        var prevButton = new Button { Text = "< Prev", Width = 70, Height = 28, Dock = DockStyle.Fill };
        var nextButton = new Button { Text = "Next >", Width = 70, Height = 28, Dock = DockStyle.Fill };
        var saveSettingsButton = new Button { Text = "Save Settings", Width = 120, Height = 28, Dock = DockStyle.Fill };
        var resetDefaultsButton = new Button { Text = "Reset Defaults", Width = 120, Height = 28, Dock = DockStyle.Fill };
        _statusLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Text = "Load a GLB to start."
        };

        top.Controls.Add(loadButton, 0, 0);
        top.Controls.Add(prevButton, 1, 0);
        top.Controls.Add(nextButton, 2, 0);
        top.Controls.Add(saveSettingsButton, 3, 0);
        top.Controls.Add(resetDefaultsButton, 4, 0);
        top.Controls.Add(_statusLabel, 5, 0);

        var sliders = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 4
        };
        sliders.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        sliders.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        sliders.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        sliders.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        sliders.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        sliders.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        sliders.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

        _strengthTrack = new TrackBar { Dock = DockStyle.Fill, Minimum = 1, Maximum = 500, TickFrequency = 25, Value = 22 };
        _levelTrack = new TrackBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 150, TickFrequency = 10, Value = 75 };
        _blurTrack = new TrackBar { Dock = DockStyle.Fill, Minimum = -100, Maximum = 100, TickFrequency = 10, Value = -15 };
        _strengthValue = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        _levelValue = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        _blurValue = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        _strengthInput = new TextBox { Dock = DockStyle.Fill };
        _levelInput = new TextBox { Dock = DockStyle.Fill };
        _blurInput = new TextBox { Dock = DockStyle.Fill };

        sliders.Controls.Add(new Label { Text = "Strength", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 0, 0);
        sliders.Controls.Add(new Label { Text = "Level", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 1, 0);
        sliders.Controls.Add(new Label { Text = "Blur/Sharp", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 2, 0);
        sliders.Controls.Add(_strengthTrack, 0, 1);
        sliders.Controls.Add(_levelTrack, 1, 1);
        sliders.Controls.Add(_blurTrack, 2, 1);
        sliders.Controls.Add(_strengthValue, 0, 2);
        sliders.Controls.Add(_levelValue, 1, 2);
        sliders.Controls.Add(_blurValue, 2, 2);
        sliders.Controls.Add(_strengthInput, 0, 3);
        sliders.Controls.Add(_levelInput, 1, 3);
        sliders.Controls.Add(_blurInput, 2, 3);

        top.Controls.Add(sliders, 0, 1);
        top.SetColumnSpan(sliders, 6);

        var previews = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            Padding = new Padding(8)
        };
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        previews.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        previews.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        previews.Controls.Add(new Label { Text = "Diffuse", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter }, 0, 0);
        previews.Controls.Add(new Label { Text = "Normal", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter }, 1, 0);
        previews.Controls.Add(new Label { Text = "Render", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter }, 2, 0);

        _diffuseBox = new PictureBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(24, 24, 24) };
        _normalBox = new PictureBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(24, 24, 24) };
        _glControl = new GLControl(new GLControlSettings
        {
            API = ContextAPI.OpenGL,
            APIVersion = new Version(3, 3),
            Profile = ContextProfile.Core,
            Flags = ContextFlags.Default
        })
        { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 24, 24) };

        previews.Controls.Add(_diffuseBox, 0, 1);
        previews.Controls.Add(_normalBox, 1, 1);
        previews.Controls.Add(_glControl, 2, 1);

        Controls.Add(previews);
        Controls.Add(top);

        _glControl.Load += (_, _) =>
        {
            _renderer.Initialize();
            if (_sourceBitmap != null && _normalBitmap != null)
                _renderer.SetTextures(_sourceBitmap, _normalBitmap);
        };
        _glControl.Resize += (_, _) =>
        {
            _renderer.Resize(_glControl.Width, _glControl.Height);
            _glControl.Invalidate();
        };
        _glControl.Paint += (_, _) =>
        {
            _renderer.Render(_cubeAngle, _cubePitch);
            _glControl.SwapBuffers();
        };
        _glControl.MouseEnter += (_, _) => _glControl.Focus();
        _glControl.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
                return;
            _isDraggingViewport = true;
            _lastMousePos = e.Location;
            _glControl.Capture = true;
        };
        _glControl.MouseMove += (_, e) =>
        {
            if (!_isDraggingViewport)
                return;
            int dx = e.X - _lastMousePos.X;
            int dy = e.Y - _lastMousePos.Y;
            _lastMousePos = e.Location;

            _cubeAngle += dx * 0.01f;
            _cubePitch = Math.Clamp(_cubePitch + (dy * 0.01f), -1.3f, 1.3f);
            _glControl.Invalidate();
        };
        _glControl.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
                return;
            _isDraggingViewport = false;
            _glControl.Capture = false;
        };
        _glControl.MouseLeave += (_, _) =>
        {
            _isDraggingViewport = false;
            _glControl.Capture = false;
        };
        _glControl.MouseWheel += (_, e) =>
        {
            float notches = e.Delta / 120f;
            _renderer.AdjustZoom(-0.35f * notches); // Wheel up => zoom in, wheel down => zoom out.
            _glControl.Invalidate();
        };

        loadButton.Click += (_, _) => LoadGlb();
        prevButton.Click += (_, _) => StepGlb(-1);
        nextButton.Click += (_, _) => StepGlb(+1);
        saveSettingsButton.Click += (_, _) => SaveCurrentSettings();
        resetDefaultsButton.Click += (_, _) => ResetToDefaultSettings();
        _strengthTrack.ValueChanged += (_, _) => RebuildNormalPreview();
        _levelTrack.ValueChanged += (_, _) => RebuildNormalPreview();
        _blurTrack.ValueChanged += (_, _) => RebuildNormalPreview();
        _strengthInput.Leave += (_, _) => ApplyTextInputToSlider(_strengthInput, _strengthTrack, 0.01f);
        _levelInput.Leave += (_, _) => ApplyTextInputToSlider(_levelInput, _levelTrack, 0.1f);
        _blurInput.Leave += (_, _) => ApplyTextInputToSlider(_blurInput, _blurTrack, 0.01f);
        _strengthInput.KeyDown += (_, e) => ApplyTextInputOnEnter(e, _strengthInput, _strengthTrack, 0.01f);
        _levelInput.KeyDown += (_, e) => ApplyTextInputOnEnter(e, _levelInput, _levelTrack, 0.1f);
        _blurInput.KeyDown += (_, e) => ApplyTextInputOnEnter(e, _blurInput, _blurTrack, 0.01f);

        ApplySettingsToControls(NormalSynthSettingsStore.Get());
        UpdateSliderLabels();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _sourceBitmap?.Dispose();
        _normalBitmap?.Dispose();
        _renderer.Dispose();
        base.OnFormClosed(e);
    }

    private void LoadGlb()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "GLB files (*.glb)|*.glb",
            Title = "Choose GLB for texture preview"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        BuildGlbListFromSelection(dlg.FileName);
        LoadCurrentGlb();
    }

    private void BuildGlbListFromSelection(string selectedGlbPath)
    {
        _glbFiles.Clear();
        string fullSelected = Path.GetFullPath(selectedGlbPath);
        string? dir = Path.GetDirectoryName(fullSelected);
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            foreach (string p in Directory.GetFiles(dir, "*.glb").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                _glbFiles.Add(Path.GetFullPath(p));
        }

        if (_glbFiles.Count == 0)
            _glbFiles.Add(fullSelected);

        _glbIndex = _glbFiles.FindIndex(p => string.Equals(p, fullSelected, StringComparison.OrdinalIgnoreCase));
        if (_glbIndex < 0) _glbIndex = 0;
    }

    private void StepGlb(int delta)
    {
        if (_glbFiles.Count == 0)
            return;
        _glbIndex = (_glbIndex + delta) % _glbFiles.Count;
        if (_glbIndex < 0)
            _glbIndex += _glbFiles.Count;
        LoadCurrentGlb();
    }

    private void LoadCurrentGlb()
    {
        if (_glbFiles.Count == 0 || _glbIndex < 0 || _glbIndex >= _glbFiles.Count)
            return;

        string glbPath = _glbFiles[_glbIndex];
        try
        {
            var model = SharpGLTF.Schema2.ModelRoot.Load(glbPath);
            var baseColorImage = TryGetFirstBaseColorImage(model);
            if (baseColorImage == null)
            {
                _currentDiffuseBytes = null;
                _sourceBitmap?.Dispose();
                _sourceBitmap = null;
                _normalBitmap?.Dispose();
                _normalBitmap = null;
                _diffuseBox.Image = null;
                _normalBox.Image = null;
                _renderer.SetBackgroundGray(0.094f);
                _statusLabel.Text = $"GLB {_glbIndex + 1}/{_glbFiles.Count}: {Path.GetFileName(glbPath)} has no BaseColor-connected texture.";
                return;
            }

            byte[] bytes = ReadAllBytes(baseColorImage.Content);
            if (bytes.Length == 0)
            {
                _statusLabel.Text = $"GLB {_glbIndex + 1}/{_glbFiles.Count}: BaseColor texture is empty/unreadable.";
                return;
            }

            _currentDiffuseBytes = bytes;
            _currentDiffuseName = string.IsNullOrWhiteSpace(baseColorImage.Name)
                ? "BaseColor"
                : baseColorImage.Name;

            _sourceBitmap?.Dispose();
            _normalBitmap?.Dispose();
            _sourceBitmap = BytesToBitmap(_currentDiffuseBytes);
            _diffuseBox.Image = _sourceBitmap;
            ApplyBestContrastRenderBackground(_sourceBitmap);
            _statusLabel.Text = $"GLB {_glbIndex + 1}/{_glbFiles.Count}: {Path.GetFileName(glbPath)} (BaseColor: {_currentDiffuseName})";
            RebuildNormalPreview();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Failed to load GLB: {ex.Message}";
        }
    }

    private static byte[] ReadAllBytes(MemoryImage memoryImage)
    {
        using var stream = memoryImage.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private void RebuildNormalPreview()
    {
        if (_currentDiffuseBytes == null)
            return;

        UpdateSliderLabels();
        try
        {
            var settings = GetCurrentSettings();
            byte[] normalPng = DerivedTextureGenerator.GenerateNormalMapPngFromImage(_currentDiffuseBytes, settings);
            _normalBitmap?.Dispose();
            _normalBitmap = BytesToBitmap(normalPng);
            _normalBox.Image = _normalBitmap;
            if (_sourceBitmap != null)
                _renderer.SetTextures(_sourceBitmap, _normalBitmap);
            _glControl.Invalidate();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Normal synth failed for '{_currentDiffuseName}': {ex.Message}";
        }
    }

    private static SharpGLTF.Schema2.Image? TryGetFirstBaseColorImage(SharpGLTF.Schema2.ModelRoot model)
    {
        foreach (var mat in model.LogicalMaterials)
        {
            object? channel = mat.FindChannel("BaseColor");
            if (channel == null)
                continue;

            var image = TryExtractImageFromChannel(channel);
            if (image == null)
                continue;
            return image;
        }
        return null;
    }

    private static SharpGLTF.Schema2.Image? TryExtractImageFromChannel(object channel)
    {
        // Check direct image-like properties first.
        var direct = TryExtractImageFromObject(channel);
        if (direct != null)
            return direct;

        // Then inspect common nested holders across SharpGLTF versions.
        var t = channel.GetType();
        foreach (var propName in new[] { "TextureInfo", "Texture" })
        {
            var p = t.GetProperty(propName);
            if (p == null) continue;
            try
            {
                object? nested = p.GetValue(channel);
                var img = TryExtractImageFromObject(nested);
                if (img != null)
                    return img;
            }
            catch
            {
                // Keep searching.
            }
        }
        return null;
    }

    private static SharpGLTF.Schema2.Image? TryExtractImageFromObject(object? obj)
    {
        if (obj == null) return null;
        var t = obj.GetType();
        foreach (var propName in new[] { "PrimaryImage", "FallbackImage", "Image" })
        {
            var p = t.GetProperty(propName);
            if (p == null) continue;
            try
            {
                if (p.GetValue(obj) is SharpGLTF.Schema2.Image img)
                    return img;
            }
            catch
            {
                // Keep searching.
            }
        }
        return null;
    }

    private DerivedTextureGenerator.NormalSynthSettings GetCurrentSettings()
    {
        var saved = NormalSynthSettingsStore.Get();
        return new DerivedTextureGenerator.NormalSynthSettings(
            Strength: _strengthTrack.Value / 100f,
            Level: _levelTrack.Value / 10f,
            BlurSharp: _blurTrack.Value / 100f,
            MaxWidth: 256,
            MaxHeight: 256,
            MinTangentSpaceZ: saved.MinTangentSpaceZ);
    }

    private void ApplySettingsToControls(DerivedTextureGenerator.NormalSynthSettings settings)
    {
        _strengthTrack.Value = Math.Clamp((int)MathF.Round(settings.Strength * 100f), _strengthTrack.Minimum, _strengthTrack.Maximum);
        _levelTrack.Value = Math.Clamp((int)MathF.Round(settings.Level * 10f), _levelTrack.Minimum, _levelTrack.Maximum);
        _blurTrack.Value = Math.Clamp((int)MathF.Round(settings.BlurSharp * 100f), _blurTrack.Minimum, _blurTrack.Maximum);
    }

    private void SaveCurrentSettings()
    {
        var settings = GetCurrentSettings();
        NormalSynthSettingsStore.Save(settings);
        SettingsSaved?.Invoke(settings);
        _statusLabel.Text = $"Saved settings: Strength {settings.Strength:0.00}, Level {settings.Level:0.0}, Blur/Sharp {settings.BlurSharp:0.00}";
    }

    private void ResetToDefaultSettings()
    {
        ApplySettingsToControls(DerivedTextureGenerator.DefaultNormalSettings);
        RebuildNormalPreview();
        _statusLabel.Text = "Reset to default normal synth settings.";
    }

    private void UpdateSliderLabels()
    {
        var settings = GetCurrentSettings();
        _syncingSliderInputs = true;
        _strengthValue.Text = $"Value: {settings.Strength:0.00}";
        _levelValue.Text = $"Value: {settings.Level:0.0}";
        _blurValue.Text = $"Value: {settings.BlurSharp:0.00}";
        _strengthInput.Text = settings.Strength.ToString("0.00");
        _levelInput.Text = settings.Level.ToString("0.0");
        _blurInput.Text = settings.BlurSharp.ToString("0.00");
        _syncingSliderInputs = false;
    }

    private void ApplyTextInputOnEnter(KeyEventArgs e, TextBox input, TrackBar track, float step)
    {
        if (e.KeyCode != Keys.Enter) return;
        ApplyTextInputToSlider(input, track, step);
        e.SuppressKeyPress = true;
    }

    private void ApplyTextInputToSlider(TextBox input, TrackBar track, float step)
    {
        if (_syncingSliderInputs)
            return;

        if (!float.TryParse(input.Text, out float parsed))
        {
            UpdateSliderLabels();
            return;
        }

        int sliderValue = (int)MathF.Round(parsed / step);
        sliderValue = Math.Clamp(sliderValue, track.Minimum, track.Maximum);
        if (track.Value != sliderValue)
        {
            track.Value = sliderValue; // Triggers RebuildNormalPreview.
        }
        else
        {
            // Keep text normalized when value doesn't change.
            UpdateSliderLabels();
        }
    }

    private static Bitmap BytesToBitmap(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var img = System.Drawing.Image.FromStream(ms);
        var bmp = new Bitmap(img.Width, img.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.DrawImage(img, 0, 0, img.Width, img.Height);
        // Align preview orientation with expected UI/GL texture direction.
        bmp.RotateFlip(RotateFlipType.RotateNoneFlipY);
        return bmp;
    }

    private void ApplyBestContrastRenderBackground(Bitmap diffuseBitmap)
    {
        float meanLuma = ComputeMeanLuma(diffuseBitmap);
        // Pick dark vs light grayscale background based on which gives stronger contrast.
        float bgGray = meanLuma >= 0.5f ? 0.08f : 0.92f;
        _renderer.SetBackgroundGray(bgGray);
        int c = (int)MathF.Round(bgGray * 255f);
        _glControl.BackColor = Color.FromArgb(c, c, c);
    }

    private static float ComputeMeanLuma(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int bytes = Math.Abs(data.Stride) * bitmap.Height;
            byte[] buf = new byte[bytes];
            Marshal.Copy(data.Scan0, buf, 0, bytes);

            double sum = 0.0;
            int count = bitmap.Width * bitmap.Height;
            for (int y = 0; y < bitmap.Height; y++)
            {
                int row = y * data.Stride;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int i = row + (x * 4);
                    float b = buf[i + 0] / 255f;
                    float g = buf[i + 1] / 255f;
                    float r = buf[i + 2] / 255f;
                    sum += (0.2126f * r) + (0.7152f * g) + (0.0722f * b);
                }
            }
            return count > 0 ? (float)(sum / count) : 0.5f;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private sealed class CubeGlRenderer : IDisposable
    {
        private const float MinCameraDistance = 2.0f;
        private const float MaxCameraDistance = 12.0f;
        private bool _initialized;
        private int _program;
        private int _vao;
        private int _vbo;
        private int _ebo;
        private int _diffuseTex;
        private int _normalTex;
        private int _width = 1;
        private int _height = 1;
        private int _uModel;
        private int _uView;
        private int _uProj;
        private int _uLightDir;
        private float _cameraDistance = 5.2f;
        private float _bgGray = 0.094f;

        public void Initialize()
        {
            if (_initialized) return;

            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(TriangleFace.Back);
            GL.ClearColor(_bgGray, _bgGray, _bgGray, 1f);

            _program = CreateProgram(VertexShaderSource, FragmentShaderSource);
            _uModel = GL.GetUniformLocation(_program, "uModel");
            _uView = GL.GetUniformLocation(_program, "uView");
            _uProj = GL.GetUniformLocation(_program, "uProj");
            _uLightDir = GL.GetUniformLocation(_program, "uLightDir");

            BuildCubeBuffers();
            _initialized = true;
        }

        public void Resize(int width, int height)
        {
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            GL.Viewport(0, 0, _width, _height);
        }

        public void SetTextures(Bitmap diffuse, Bitmap normal)
        {
            if (!_initialized) return;
            _diffuseTex = UploadTexture(_diffuseTex, diffuse);
            _normalTex = UploadTexture(_normalTex, normal);
        }

        public void AdjustZoom(float deltaDistance)
        {
            _cameraDistance = Math.Clamp(_cameraDistance + deltaDistance, MinCameraDistance, MaxCameraDistance);
        }

        public void SetBackgroundGray(float gray)
        {
            _bgGray = Math.Clamp(gray, 0f, 1f);
            if (_initialized)
                GL.ClearColor(_bgGray, _bgGray, _bgGray, 1f);
        }

        public void Render(float yaw, float pitch)
        {
            if (!_initialized) return;
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            if (_diffuseTex == 0 || _normalTex == 0) return;

            GL.UseProgram(_program);
            GL.BindVertexArray(_vao);

            Matrix4 model = Matrix4.CreateRotationX(pitch) * Matrix4.CreateRotationY(yaw);
            Matrix4 view = Matrix4.LookAt(new Vector3(0, 0, _cameraDistance), Vector3.Zero, Vector3.UnitY);
            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45f), _width / (float)_height, 0.1f, 100f);
            GL.UniformMatrix4(_uModel, false, ref model);
            GL.UniformMatrix4(_uView, false, ref view);
            GL.UniformMatrix4(_uProj, false, ref proj);
            GL.Uniform3(_uLightDir, Vector3.Normalize(new Vector3(0.45f, 0.55f, 1f)));

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _diffuseTex);
            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, _normalTex);
            GL.Uniform1(GL.GetUniformLocation(_program, "uDiffuseTex"), 0);
            GL.Uniform1(GL.GetUniformLocation(_program, "uNormalTex"), 1);

            GL.DrawElements(OpenTK.Graphics.OpenGL4.PrimitiveType.Triangles, 36, DrawElementsType.UnsignedInt, 0);
        }

        private void BuildCubeBuffers()
        {
            float[] verts =
            [
                // pos                normal          tangent         uv
                // front (+Z)
                -1,-1, 1,  0,0,1,    1,0,0,         0,1,
                 1,-1, 1,  0,0,1,    1,0,0,         1,1,
                 1, 1, 1,  0,0,1,    1,0,0,         1,0,
                -1, 1, 1,  0,0,1,    1,0,0,         0,0,
                // right (+X)
                 1,-1, 1,  1,0,0,    0,0,-1,        0,1,
                 1,-1,-1,  1,0,0,    0,0,-1,        1,1,
                 1, 1,-1,  1,0,0,    0,0,-1,        1,0,
                 1, 1, 1,  1,0,0,    0,0,-1,        0,0,
                // back (-Z)
                 1,-1,-1,  0,0,-1,   -1,0,0,        0,1,
                -1,-1,-1,  0,0,-1,   -1,0,0,        1,1,
                -1, 1,-1,  0,0,-1,   -1,0,0,        1,0,
                 1, 1,-1,  0,0,-1,   -1,0,0,        0,0,
                // left (-X)
                -1,-1,-1, -1,0,0,    0,0,1,         0,1,
                -1,-1, 1, -1,0,0,    0,0,1,         1,1,
                -1, 1, 1, -1,0,0,    0,0,1,         1,0,
                -1, 1,-1, -1,0,0,    0,0,1,         0,0,
                // top (+Y)
                -1, 1, 1,  0,1,0,    1,0,0,         0,1,
                 1, 1, 1,  0,1,0,    1,0,0,         1,1,
                 1, 1,-1,  0,1,0,    1,0,0,         1,0,
                -1, 1,-1,  0,1,0,    1,0,0,         0,0,
                // bottom (-Y)
                -1,-1,-1,  0,-1,0,   1,0,0,         0,1,
                 1,-1,-1,  0,-1,0,   1,0,0,         1,1,
                 1,-1, 1,  0,-1,0,   1,0,0,         1,0,
                -1,-1, 1,  0,-1,0,   1,0,0,         0,0,
            ];

            uint[] indices =
            [
                0,1,2, 0,2,3,
                4,5,6, 4,6,7,
                8,9,10, 8,10,11,
                12,13,14, 12,14,15,
                16,17,18, 16,18,19,
                20,21,22, 20,22,23
            ];

            _vao = GL.GenVertexArray();
            _vbo = GL.GenBuffer();
            _ebo = GL.GenBuffer();
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);

            const int stride = 11 * sizeof(float);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
            GL.EnableVertexAttribArray(3);
            GL.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, stride, 9 * sizeof(float));
        }

        private static int UploadTexture(int existingTex, Bitmap bitmap)
        {
            if (existingTex == 0)
                existingTex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, existingTex);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)OpenTK.Graphics.OpenGL4.TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)OpenTK.Graphics.OpenGL4.TextureWrapMode.Repeat);

            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, bitmap.Width, bitmap.Height, 0, OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return existingTex;
        }

        private static int CreateProgram(string vs, string fs)
        {
            int v = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(v, vs);
            GL.CompileShader(v);
            GL.GetShader(v, ShaderParameter.CompileStatus, out int okV);
            if (okV == 0) throw new InvalidOperationException("Vertex shader compile failed: " + GL.GetShaderInfoLog(v));

            int f = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(f, fs);
            GL.CompileShader(f);
            GL.GetShader(f, ShaderParameter.CompileStatus, out int okF);
            if (okF == 0) throw new InvalidOperationException("Fragment shader compile failed: " + GL.GetShaderInfoLog(f));

            int p = GL.CreateProgram();
            GL.AttachShader(p, v);
            GL.AttachShader(p, f);
            GL.LinkProgram(p);
            GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int okP);
            if (okP == 0) throw new InvalidOperationException("Program link failed: " + GL.GetProgramInfoLog(p));
            GL.DetachShader(p, v);
            GL.DetachShader(p, f);
            GL.DeleteShader(v);
            GL.DeleteShader(f);
            return p;
        }

        public void Dispose()
        {
            if (_diffuseTex != 0) GL.DeleteTexture(_diffuseTex);
            if (_normalTex != 0) GL.DeleteTexture(_normalTex);
            if (_vbo != 0) GL.DeleteBuffer(_vbo);
            if (_ebo != 0) GL.DeleteBuffer(_ebo);
            if (_vao != 0) GL.DeleteVertexArray(_vao);
            if (_program != 0) GL.DeleteProgram(_program);
            _initialized = false;
        }

        private const string VertexShaderSource = """
            #version 330 core
            layout(location=0) in vec3 aPos;
            layout(location=1) in vec3 aNormal;
            layout(location=2) in vec3 aTangent;
            layout(location=3) in vec2 aUv;

            uniform mat4 uModel;
            uniform mat4 uView;
            uniform mat4 uProj;
            out vec2 vUv;
            out mat3 vTbn;

            void main()
            {
                vec3 T = normalize(mat3(uModel) * aTangent);
                vec3 N = normalize(mat3(uModel) * aNormal);
                vec3 B = normalize(cross(N, T));
                vTbn = mat3(T, B, N);
                vUv = aUv;
                gl_Position = uProj * uView * uModel * vec4(aPos, 1.0);
            }
            """;

        private const string FragmentShaderSource = """
            #version 330 core
            in vec2 vUv;
            in mat3 vTbn;
            out vec4 FragColor;

            uniform sampler2D uDiffuseTex;
            uniform sampler2D uNormalTex;
            uniform vec3 uLightDir;

            void main()
            {
                vec3 albedo = texture(uDiffuseTex, vUv).rgb;
                vec3 nTex = texture(uNormalTex, vUv).rgb * 2.0 - 1.0;
                vec3 nWorld = normalize(vTbn * normalize(nTex));
                float ndotl = max(dot(nWorld, normalize(uLightDir)), 0.0);
                float shade = 0.35 + 0.65 * ndotl;
                FragColor = vec4(albedo * shade, 1.0);
            }
            """;
    }
}

