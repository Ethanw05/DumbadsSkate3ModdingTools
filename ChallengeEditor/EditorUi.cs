using System.Diagnostics;
using System.Linq;
using System.Numerics;
using ChallengeEditor.Sk8;
using ChallengeEditor.Rendering;
using DlcBuilder.Modules.DlcManifest;
using DlcBuilder.Modules.Packing;
using ImGuiNET;
using Veldrid;
using Key = Veldrid.Key;

namespace ChallengeEditor;

public sealed class EditorUi
{
    private const float MenuBarHeight = 22f;
    private const float StatusBarHeight = 24f;
    /// <summary>Lower bound for the left column; below this the Scene/Inspector
    /// panel headers and "+/- DIST" buttons stop fitting cleanly.</summary>
    private const float MinLeftPanelWidth = 220f;
    /// <summary>Cap so a pathological wide entry (long mesh / locator name)
    /// can't eat the whole window. Set in <see cref="Draw"/> as a fraction
    /// of current window width.</summary>
    private const float MaxLeftPanelFraction = 0.5f;
    /// <summary>Auto-fit width for the Scene + Inspector column. Recomputed
    /// each frame from the rightmost item rendered in either panel (see
    /// <see cref="_frameLeftPanelMaxX"/>). Starts at the minimum and grows.</summary>
    private float _measuredLeftPanelWidth = MinLeftPanelWidth;
    /// <summary>Per-frame accumulator for the rightmost item X in the left
    /// column. Reset to 0 at the start of <see cref="Draw"/>; both Scene and
    /// Inspector update it via <see cref="TrackLeftPanelExtent"/>. Used to
    /// resize the column on the NEXT frame (one-frame lag, visually stable).</summary>
    private float _frameLeftPanelMaxX;
    /// <summary>Fraction of vertical space above the inspector reserved for the Scene tree.</summary>
    private const float LeftColumnSceneFraction = 0.54f;

    private readonly EditorScene _scene;
    private readonly Renderer3D _renderer;
    private readonly OrbitCamera _camera;
    private readonly ImGuiRenderer _imguiRenderer;
    /// <summary>Marshals work from background threads (GLB import parse, future
    /// ArenaBuilder builds) back onto the render thread for Veldrid + scene
    /// mutation. Drained once per frame at the top of <see cref="Draw"/>.</summary>
    private readonly MainThreadDispatcher _dispatcher = new();

    /// Per-frame render-rate target. Performance leans on aggressive
    /// throttling for low idle GPU; High FPS gives smoother interaction
    /// at the cost of more frequent redraws.
    public enum RenderRateMode { Performance, HighFps }
    private RenderRateMode _renderRateMode = RenderRateMode.Performance;
    public RenderRateMode CurrentRenderRateMode => _renderRateMode;
    /// Milliseconds-per-frame target while actively interacting (camera move,
    /// gizmo drag, inspector edit, etc.). Lower = smoother + more GPU.
    public double ActiveFrameTargetMs => _renderRateMode switch
    {
        RenderRateMode.HighFps     => 1000.0 / 120.0, //  ~8.3 ms — smooth like a game
        _                          => 1000.0 / 30.0,  //  ~33.3 ms — fine for editor feel
    };
    /// Milliseconds-per-frame target when the 3D viewport is idle (no input,
    /// no scene mutation). Caps idle GPU near zero.
    public double IdleFrameTargetMs => _renderRateMode switch
    {
        RenderRateMode.HighFps     => 1000.0 / 30.0, //  33 ms — still feels alive
        _                          => 1000.0 / 10.0, // 100 ms — slow heartbeat
    };

    /// True when the 3D viewport's offscreen framebuffer needs to be redrawn
    /// this frame. Set whenever something visible to the 3D render changes
    /// (camera moved, scene mutated, selection / gizmo / tool changed, mesh
    /// import completed, viewport resized). Idle frames (no input, no
    /// mutation) keep the previous FB content — ImGui still re-renders, but
    /// the heavy 3D pass is skipped, dropping editor GPU usage to near-zero
    /// when the user isn't interacting.
    private bool _needs3DRedraw = true;
    public bool Needs3DRedraw => _needs3DRedraw;
    public void Consume3DRedraw() { _needs3DRedraw = false; _framesTotal++; _frames3D++; }
    public void Consume3DSkip()   { _needs3DRedraw = false; _framesTotal++; }
    public void Invalidate3D() => _needs3DRedraw = true;

    /// <summary>
    /// True when the user is in the middle of an interactive gesture that
    /// must keep the editor at the active frame rate even on frames where
    /// nothing actually changed. Examples: dragging a translate/rotate/scale
    /// gizmo (mouse can pause briefly between movements), holding the camera
    /// orbit / pan button, or fly-camera mode (mouse-look + WASD where any
    /// frame could need to advance).
    ///
    /// Drives the active-vs-idle classification in Program.cs's frame loop:
    /// the loop was previously dropping to the idle target between sub-pixel
    /// mouse movements during a gizmo drag, making the UI feel "asleep".
    /// </summary>
    public bool IsActivelyInteracting =>
        _activeGizmoAxis != GizmoAxis.None
        || _orbiting
        || _panning
        || _camera.FlyMode;

    // Rolling 1-second window of total frames vs. 3D-render frames. Drives
    // the HUD overlay so we can see at a glance whether the dirty flag is
    // actually skipping work. Counter pair resets every second.
    private int _framesTotal;
    private int _frames3D;
    private float _frameCounterAccum;
    private int _lastFramesTotal;
    private int _lastFrames3D;

    /// Camera + viewport state snapshot from last frame. Compared against
    /// the current frame's state to detect "camera moved" / "viewport size
    /// changed" without each individual camera mutation site having to
    /// remember to call <see cref="Invalidate3D"/>.
    private (Vector3 Eye, Vector3 Target, float Yaw, float Pitch, float Dist, float VpW, float VpH, EditorTool Tool, GizmoAxis HoverGiz, GizmoAxis ActiveGiz, object? Selected) _lastRenderState;

    /// Case-insensitive substring filter for the GLB Materials list.
    /// Persisted across map switches so the typed filter survives toggling
    /// which map is active.
    private string _glbMaterialSearchText = "";
    private object? _selected;
    public object? Selected => _selected;

    private string _statusMessage = "Ready. Fly toggle (default F2 or your bind) · Orbit: RMB orbit · MMB pan · scroll zoom · Fly: RMB look · WASD/QE move · scroll = speed · Shift fast · Del · M/R/T · Esc.";
    private float _windowWidth;
    private float _windowHeight;
    private Guid? _forceOpenChallengeId;
    private Guid? _forceOpenChallengeLocatorsId;
    private Guid? _forceOpenChallengeVisualsId;
    private Guid? _forceOpenChallengeVolumesId;
    private const int MaxUndoStates = 20;
    private readonly List<UndoState> _undoStates = new();

    /// Path of the currently-open scene file (null = unsaved/untitled).
    /// Drives whether `Save Scene` overwrites in place or prompts for a name.
    private string? _currentScenePath;

    private bool _orbiting;
    private bool _panning;
    private Vector2 _lastMousePos;

    private EditorKeybinds _binds;
    private bool _showKeybindsWindow;
    /// <summary>When set, next key or mouse button (except modifiers/Esc) assigns this bind.</summary>
    private string? _keybindCaptureProperty;

    /// <summary>Set while the Scene viewport image is hovered (for mouse hotkeys).</summary>
    private bool _viewportSceneImageHovered;

    /// <summary>Last Scene-tab viewport size (px), for view-center spawn raycasts from menus / inspectors.</summary>
    private Vector2 _lastSceneViewportPanelSize;

    /// <summary>Per-frame mouse state from the prior frame (for press-edge detection). <see cref="InputSnapshot.MouseEvents"/> is not always populated.</summary>
    private readonly bool[] _mouseDownPrevFrame = new bool[13];

    private readonly HashSet<Key> _heldKeys = new();
    public float CameraMoveSpeed { get; set; } = 12f;
    public float CameraMoveSpeedFast { get; set; } = 48f;

    // Tool / gizmo state
    private EditorTool _tool = EditorTool.Select;
    public EditorTool Tool => _tool;

    /// <summary>Gizmo axis tint when a challenge ribbon/chevron locator is selected.</summary>
    public LocatorGizmoAccent SelectedLocatorGizmoAccent
    {
        get
        {
            if (_selected is not Locator l || !_scene.HasActiveMap || l.Owner != OwnerKind.Challenge)
                return LocatorGizmoAccent.None;
            IMap m = _scene.ActiveMap!;
            foreach (Challenge c in m.Challenges)
            {
                if (c.Id != l.OwnerChallengeId) continue;
                if (c.ChevronLocatorIds.Contains(l.Id))
                    return LocatorGizmoAccent.Chevron;
                if (c.VisualSignupLocatorId == l.Id || c.InChallengeRibbonArrowLocatorIds.Contains(l.Id))
                    return LocatorGizmoAccent.RibbonArrow;
            }
            return LocatorGizmoAccent.None;
        }
    }

    /// World = drag along world axes. Local = drag along the selected object's rotated axes.
    /// Cycles via M while already in Move tool. Only Move honors this; Rotate/Scale stay world.
    private GizmoSpace _moveSpace = GizmoSpace.World;
    public GizmoSpace MoveSpace => _moveSpace;

    /// Orientation matrix the renderer should apply to the active gizmo's axes.
    ///   Move + World    → Identity
    ///   Move + Local    → selected object's rotation
    ///   Scale (always)  → selected object's rotation (HalfExtents are stored in local space)
    ///   Rotate          → Identity (rings already rotate via per-axis angle math)
    public Matrix4x4 ActiveGizmoOrientation =>
        (_tool == EditorTool.Move && _moveSpace == GizmoSpace.Local) || _tool == EditorTool.Scale || _tool == EditorTool.Rotate
            ? SelectedRotationMatrix()
            : Matrix4x4.Identity;

    private GizmoAxis _hoveredGizmoAxis = GizmoAxis.None;
    public GizmoAxis HoveredGizmoAxis => _hoveredGizmoAxis;

    private GizmoAxis _activeGizmoAxis = GizmoAxis.None;
    public GizmoAxis ActiveGizmoAxis => _activeGizmoAxis;

    private Vector3 _dragStartCenter;
    private float _dragStartAxisT;

    // Rotate-drag state — full 3-axis Euler.
    private Vector3 _dragStartRotationDeg;
    private Quaternion _dragStartRotationQ;
    private Vector3 _dragRingU;
    private Vector3 _dragRingV;
    private Vector3 _dragRingN;
    private float _dragStartRingAngle;

    // Scale-drag state (gizmo)
    private Vector3 _dragStartHalfExtents;
    private float _dragStartAxisDistance;
    private bool _gizmoUndoCaptured;

    public EditorUi(EditorScene scene, Renderer3D renderer, OrbitCamera camera, ImGuiRenderer imguiRenderer)
    {
        _scene = scene;
        _renderer = renderer;
        _camera = camera;
        _imguiRenderer = imguiRenderer;
        _binds = EditorKeybinds.LoadOrDefaults();
    }

    /// <summary>
    /// Call once per frame after <see cref="Veldrid.Sdl2.Sdl2Window.PumpEvents"/> and <strong>before</strong>
    /// <c>ImGuiRenderer.Update</c>. Veldrid.ImGui clears keyboard edge events from the snapshot when updating ImGui;
    /// this keeps remapped keys and keybind capture working.
    /// </summary>
    public void ProcessInputBeforeImGui(InputSnapshot input)
    {
        foreach (KeyEvent ke in input.KeyEvents)
        {
            if (ke.Down) _heldKeys.Add(ke.Key);
            else _heldKeys.Remove(ke.Key);
        }

        ProcessKeybindCapture(input);
        if (_keybindCaptureProperty == null)
        {
            ProcessGlobalKeys(input);
            // Thumb / side buttons (SDL X1/X2): Veldrid feeds ImGui via AddMouseButtonEvent but ImGui.IsMouseClicked often never fires for indices 3–4.
            ProcessGlobalMouseFlyToggleThumbFromSnapshot(input);
        }

        foreach (MouseButton b in Enum.GetValues<MouseButton>())
        {
            int i = (int)b;
            if (i >= 0 && i < _mouseDownPrevFrame.Length)
                _mouseDownPrevFrame[i] = input.IsMouseDown(b);
        }
    }

    public void Draw(InputSnapshot input, float dt)
    {
        Vector2 displaySize = ImGui.GetIO().DisplaySize;
        _windowWidth = displaySize.X;
        _windowHeight = displaySize.Y;
        _viewportSceneImageHovered = false;

        // Run anything posted from background tasks (GLB import completion etc.)
        // before the frame draws — buffers freshly uploaded this frame are visible
        // in the viewport immediately.
        _dispatcher.Drain();
        if (_dispatcher.LastException is Exception dispatcherEx)
        {
            _statusMessage = $"Background task failed: {dispatcherEx.GetType().Name}: {dispatcherEx.Message}";
            _dispatcher.LastException = null;
        }

        DrawMenuBar();

        float bodyTop = MenuBarHeight;
        float bodyHeight = _windowHeight - MenuBarHeight - StatusBarHeight;

        float leftSceneH = bodyHeight * LeftColumnSceneFraction;
        float leftInspectorH = bodyHeight - leftSceneH;
        // Auto-fit the left column to its content. We use last frame's
        // measurement (one-frame lag, visually stable — the column "settles"
        // as you expand subtrees). The measurement accumulates from items
        // submitted in both panels via TrackLeftPanelExtent(); reset here
        // before this frame's draw.
        _frameLeftPanelMaxX = 0f;
        float leftPanelW = Math.Clamp(_measuredLeftPanelWidth, MinLeftPanelWidth, _windowWidth * MaxLeftPanelFraction);
        DrawSceneTree(0, bodyTop, leftPanelW, leftSceneH);
        DrawInspector(0, bodyTop + leftSceneH, leftPanelW, leftInspectorH);
        DrawViewport(leftPanelW, bodyTop, _windowWidth - leftPanelW, bodyHeight, input, dt);
        // After both panels rendered, derive next-frame width from the
        // observed content extent. Add ImGui's scrollbar + window padding so
        // the rightmost item isn't flush against the panel edge / hidden
        // behind the scrollbar.
        var style = ImGui.GetStyle();
        float pad = style.WindowPadding.X * 2f + style.ScrollbarSize + 6f;
        if (_frameLeftPanelMaxX > 0f)
            _measuredLeftPanelWidth = _frameLeftPanelMaxX + pad;
        DrawStatusBar();
        DrawBuildLogWindow();
        DrawKeybindsWindow(input);
        DrawChallengeTypePicker();
        ProcessGlobalMouseFlyToggleAfterImGuiUi();

        DetectRenderStateChanges();

        // Roll the per-second frame counters so the HUD shows a stable
        // total/3D ratio updated once per second instead of jittering each
        // frame.
        _frameCounterAccum += dt;
        if (_frameCounterAccum >= 1f)
        {
            _lastFramesTotal = _framesTotal;
            _lastFrames3D = _frames3D;
            _framesTotal = 0;
            _frames3D = 0;
            _frameCounterAccum = 0f;
        }
    }

    /// <summary>
    /// Compares the current camera + viewport + selection / tool / gizmo
    /// state against last frame's snapshot. If anything that affects the 3D
    /// render output differs, sets the dirty flag so the next frame re-renders
    /// the offscreen FB. Individual scene mutation paths (add / delete /
    /// gizmo drag / inspector edit / mesh import) call <see cref="Invalidate3D"/>
    /// directly — they touch things this hash can't see (volume positions,
    /// challenge data, etc.).
    /// </summary>
    private void DetectRenderStateChanges()
    {
        var state = (
            _camera.Position,
            _camera.Target,
            _camera.YawRadians,
            _camera.PitchRadians,
            _camera.Distance,
            _lastSceneViewportPanelSize.X,
            _lastSceneViewportPanelSize.Y,
            _tool,
            _hoveredGizmoAxis,
            _activeGizmoAxis,
            _selected);
        if (!state.Equals(_lastRenderState))
        {
            _needs3DRedraw = true;
            _lastRenderState = state;
        }
    }

    private void ProcessKeybindCapture(InputSnapshot input)
    {
        if (_keybindCaptureProperty == null) return;
        foreach (KeyEvent ke in input.KeyEvents)
        {
            if (!ke.Down) continue;
            if (ke.Key == Key.Escape)
            {
                _keybindCaptureProperty = null;
                _statusMessage = "Keybind capture canceled.";
                return;
            }
            if (ke.Key is Key.ControlLeft or Key.ControlRight or Key.ShiftLeft or Key.ShiftRight
                or Key.AltLeft or Key.AltRight or Key.WinLeft or Key.WinRight)
                continue;
            _binds.SetBinding(_keybindCaptureProperty, EditorInputBinding.FromKey(ke.Key));
            _keybindCaptureProperty = null;
            try { _binds.WriteToDisk(); }
            catch (Exception ex) { _statusMessage = $"Saved keybind in memory only: {ex.Message}"; return; }
            _statusMessage = "Keybinds saved.";
            return;
        }

        // Use press edges from IsMouseDown vs previous frame — MouseEvents is often empty even when SDL updates button state.
        foreach (MouseButton b in Enum.GetValues<MouseButton>())
        {
            int i = (int)b;
            if (i < 0 || i >= _mouseDownPrevFrame.Length) continue;
            if (!input.IsMouseDown(b) || _mouseDownPrevFrame[i]) continue;

            _binds.SetBinding(_keybindCaptureProperty, EditorInputBinding.FromMouse(b));
            _keybindCaptureProperty = null;
            try { _binds.WriteToDisk(); }
            catch (Exception ex) { _statusMessage = $"Saved keybind in memory only: {ex.Message}"; return; }
            _statusMessage = "Keybinds saved.";
            return;
        }
    }

    private void ProcessGlobalKeys(InputSnapshot input)
    {
        bool textActive = ImGui.GetIO().WantTextInput;
        if (textActive) return;

        bool ctrl = _heldKeys.Contains(Key.ControlLeft) || _heldKeys.Contains(Key.ControlRight);
        bool shift = _heldKeys.Contains(Key.ShiftLeft) || _heldKeys.Contains(Key.ShiftRight);

        foreach (KeyEvent ke in input.KeyEvents)
        {
            if (!ke.Down) continue;

            // Ctrl-modified shortcuts: Save / SaveAs / Open. We check these
            // first so Ctrl+S doesn't fall through to the Move-camera-down
            // binding on plain S.
            if (ctrl)
            {
                if (_binds.FileSave.IsKey && ke.Key == _binds.FileSave.Key) { SaveScene(saveAs: shift); continue; }
                if (_binds.FileOpen.IsKey && ke.Key == _binds.FileOpen.Key) { OpenSceneViaPicker(); continue; }
                if (_binds.FileUndo.IsKey && ke.Key == _binds.FileUndo.Key) { PerformUndo(); continue; }
            }

            if (!ctrl && _binds.ToggleFlyCamera.IsKey && ke.Key == _binds.ToggleFlyCamera.Key)
            {
                ToggleFlyCameraCore();
                continue;
            }

            if (_binds.ToolMove.IsKey && ke.Key == _binds.ToolMove.Key)
            {
                if (_tool == EditorTool.Move)
                {
                    _moveSpace = _moveSpace == GizmoSpace.World ? GizmoSpace.Local : GizmoSpace.World;
                    _statusMessage = $"Move space: {_moveSpace}";
                }
                else
                {
                    _tool = EditorTool.Move;
                    _statusMessage = $"Tool: Move ({_moveSpace})";
                }
                continue;
            }
            if (_binds.ToolRotate.IsKey && ke.Key == _binds.ToolRotate.Key) { _tool = EditorTool.Rotate; _statusMessage = "Tool: Rotate"; continue; }
            if (_binds.ToolScale.IsKey && ke.Key == _binds.ToolScale.Key) { _tool = EditorTool.Scale; _statusMessage = "Tool: Scale"; continue; }
            if (_binds.FrameSelection.IsKey && ke.Key == _binds.FrameSelection.Key) { FrameSelectedOrAll(); continue; }
            if (_binds.Deselect.IsKey && ke.Key == _binds.Deselect.Key)
            {
                if (_activeGizmoAxis != GizmoAxis.None) CancelGizmoDrag();
                else _selected = null;
                continue;
            }
            if ((_binds.DeleteSelected.IsKey && ke.Key == _binds.DeleteSelected.Key)
                || (_binds.DeleteSelectedAlt.IsKey && ke.Key == _binds.DeleteSelectedAlt.Key))
            {
                DeleteSelected();
                continue;
            }
        }
    }

    /// <summary>
    /// Fly toggle bound to mouse thumb buttons (Veldrid <see cref="MouseButton.Button1"/> / <see cref="MouseButton.Button2"/>).
    /// Same press-edge logic as keybind capture; do not rely on <see cref="ImGui.IsMouseClicked"/> for these buttons.
    /// </summary>
    private void ProcessGlobalMouseFlyToggleThumbFromSnapshot(InputSnapshot input)
    {
        if (ImGui.GetIO().WantTextInput) return;
        if (!_binds.ToggleFlyCamera.IsMouse) return;
        MouseButton mb = _binds.ToggleFlyCamera.Mouse;
        if (mb != MouseButton.Button1 && mb != MouseButton.Button2) return;

        int i = (int)mb;
        if (i < 0 || i >= _mouseDownPrevFrame.Length) return;
        if (!input.IsMouseDown(mb) || _mouseDownPrevFrame[i]) return;

        ToggleFlyCameraCore();
    }

    private void ToggleFlyCameraCore()
    {
        _camera.SetFlyMode(!_camera.FlyMode);
        _orbiting = false;
        _panning = false;
        _statusMessage = _camera.FlyMode ? "Camera: Fly mode" : "Camera: Orbit mode";
    }

    /// <summary>Mouse wheel in fly mode: scale WASD/QE move speeds (preserves normal vs Shift ratio).</summary>
    private void AdjustFlyMoveSpeedFromWheel(float wheel)
    {
        if (wheel == 0f) return;
        const float stepPerUnit = 1.06f;
        float factor = MathF.Pow(stepPerUnit, wheel);
        float ratio = CameraMoveSpeedFast / Math.Max(CameraMoveSpeed, 1e-4f);
        if (!float.IsFinite(ratio) || ratio <= 0f) ratio = 4f;

        float newBase = Math.Clamp(CameraMoveSpeed * factor, 1f, 512f);
        float newFast = Math.Clamp(newBase * ratio, 4f, 4096f);
        CameraMoveSpeed = newBase;
        CameraMoveSpeedFast = newFast;

        _statusMessage = $"Fly move speed: {CameraMoveSpeed:F0} (Shift: {CameraMoveSpeedFast:F0})";
    }

    /// <summary>
    /// Mouse-bound fly toggle when the click was not on the 3D viewport (viewport uses the same bind there).
    /// Uses ImGui click state — raw <see cref="InputSnapshot.MouseEvents"/> can be empty after ImGui.Update.
    /// </summary>
    private void ProcessGlobalMouseFlyToggleAfterImGuiUi()
    {
        if (ImGui.GetIO().WantTextInput) return;
        if (!_binds.ToggleFlyCamera.IsMouse) return;
        // Thumb toggles run from ProcessGlobalMouseFlyToggleThumbFromSnapshot (snapshot edges).
        if (_binds.ToggleFlyCamera.Mouse is MouseButton.Button1 or MouseButton.Button2) return;
        if (_viewportSceneImageHovered) return;
        int ti = ImGuiMouseButtonIndex(_binds.ToggleFlyCamera.Mouse);
        if (ti < 0) return;
        if (!ImGui.IsMouseClicked((ImGuiMouseButton)ti)) return;
        ToggleFlyCameraCore();
    }

    /// <summary>Matches Veldrid.ImGui feed order: Left, Right, Middle, Button1, Button2.
    /// Returns an int (not <see cref="ImGuiMouseButton"/>) because ImGui's mouse-button
    /// API only defines Left/Right/Middle; Button1/Button2 (mouse 4/5) are valid
    /// indices the runtime accepts but aren't enum-valued. Callers cast to
    /// <see cref="ImGuiMouseButton"/> when ti &lt; 3.</summary>
    private static int ImGuiMouseButtonIndex(MouseButton b) => b switch
    {
        MouseButton.Left => 0,
        MouseButton.Right => 1,
        MouseButton.Middle => 2,
        MouseButton.Button1 => 3,
        MouseButton.Button2 => 4,
        _ => -1
    };

    private void DrawMenuBar()
    {
        if (!ImGui.BeginMainMenuBar()) return;
        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("New Scene")) NewScene();
            if (ImGui.MenuItem("Open Scene...", "Ctrl+O")) OpenSceneViaPicker();
            if (ImGui.MenuItem("Save Scene", "Ctrl+S")) SaveScene(saveAs: false);
            if (ImGui.MenuItem("Save Scene As...", "Ctrl+Shift+S")) SaveScene(saveAs: true);
            ImGui.Separator();
            if (ImGui.MenuItem("Load DIST...")) LoadDistViaPicker();
            if (ImGui.MenuItem("Remove Active DIST", null, false, _scene.HasActiveDist)) RemoveActiveDist();
            if (ImGui.MenuItem("Clear Active Map's Meshes", null, false, _scene.Meshes.Count > 0)) ClearImportedMeshes();
            ImGui.Separator();
            if (ImGui.MenuItem("Import Sk8 Map...")) ImportSk8ViaPicker();
            if (ImGui.MenuItem("Remove Active Sk8 Map", null, false, _scene.ActiveGlbMap != null)) RemoveActiveGlbMap();
            bool canExport = _scene.Dists.Any(d => !string.IsNullOrEmpty(d.FolderPath));
            if (ImGui.MenuItem("Export DLC...", null, false, canExport)) BeginExportDlcFlow();
            if (ImGui.MenuItem("Show Last Build Log", null, false, !string.IsNullOrEmpty(_lastBuildLog))) _showBuildLog = true;
            ImGui.Separator();
            if (ImGui.MenuItem("Exit")) Environment.Exit(0);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Edit"))
        {
            if (ImGui.MenuItem("Undo", "Ctrl+Z", false, _undoStates.Count > 0)) PerformUndo();
            if (ImGui.MenuItem("Delete", "Del", false, _selected != null)) DeleteSelected();
            if (ImGui.MenuItem("Deselect", "Esc", false, _selected != null)) _selected = null;
            ImGui.Separator();
            if (ImGui.MenuItem("Keybinds...")) _showKeybindsWindow = true;
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Add"))
        {
            if (ImGui.MenuItem("Trigger Volume")) AddVolume();
            if (ImGui.MenuItem("Locator")) AddLocator();
            if (ImGui.MenuItem("Challenge...")) RequestAddChallenge();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("View"))
        {
            if (ImGui.MenuItem("Frame Selected / All", "F")) FrameSelectedOrAll();
            if (ImGui.MenuItem("Frame Origin")) _camera.FrameSphere(Vector3.Zero, 15f);
            bool fly = _camera.FlyMode;
            if (ImGui.MenuItem("Fly camera", null, fly, true))
            {
                _camera.SetFlyMode(!fly);
                _orbiting = false;
                _panning = false;
                _statusMessage = _camera.FlyMode ? "Camera: Fly mode" : "Camera: Orbit mode";
            }
            ImGui.Separator();
            // Render-rate mode. Performance caps at 30 fps active / 10 idle
            // for very low GPU; High FPS caps at 120 / 30 for a smoother feel
            // during heavy interaction. Both still benefit from dirty-flag
            // skipping when truly idle.
            bool perf = _renderRateMode == RenderRateMode.Performance;
            bool high = _renderRateMode == RenderRateMode.HighFps;
            if (ImGui.MenuItem("Performance Mode (30 / 10 fps)", null, perf, true))
            {
                _renderRateMode = RenderRateMode.Performance;
                _statusMessage = "Render rate: Performance (30 fps interactive, 10 fps idle).";
                _needs3DRedraw = true;
            }
            if (ImGui.MenuItem("High FPS Mode (120 / 30 fps)", null, high, true))
            {
                _renderRateMode = RenderRateMode.HighFps;
                _statusMessage = "Render rate: High FPS (120 fps interactive, 30 fps idle).";
                _needs3DRedraw = true;
            }
            ImGui.EndMenu();
        }
        ImGui.EndMainMenuBar();
    }

    private static ImGuiWindowFlags PinnedPanelFlags() =>
        ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse |
        ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoSavedSettings;

    private void DrawSceneTree(float x, float y, float w, float h)
    {
        ImGui.SetNextWindowPos(new Vector2(x, y));
        ImGui.SetNextWindowSize(new Vector2(w, h));
        ImGui.Begin("Scene", PinnedPanelFlags());

        // Top-level controls: add a DIST (folder of .psf files) or import a GLB
        // map. The folder/file pickers handle the "+" actions — there are no
        // empty maps in this app. Removal acts on whichever kind is active.
        if (ImGui.SmallButton("+ DIST")) LoadDistViaPicker();
        ImGui.SameLine();
        if (ImGui.SmallButton("+ Sk8")) ImportSk8ViaPicker();
        ImGui.SameLine();
        if (ImGui.SmallButton("- Map")) RemoveActiveMap();
        ImGui.Separator();

        if (_scene.Dists.Count == 0 && _scene.GlbMaps.Count == 0)
        {
            ImGui.TextDisabled("No map loaded.");
            ImGui.TextDisabled("Click '+ DIST' or '+ Sk8' to load one.");
        }

        // One sub-tree per map (Dist first, then GlbMap — both treated the same
        // visually). The active map is highlighted; clicking the header
        // switches active. Children shown only for the active map so it's
        // clear what you're editing.
        Guid activeId = _scene.ActiveMapId;
        for (int i = 0; i < _scene.Dists.Count; i++)
            DrawMapHeader(_scene.Dists[i], activeId, isGlb: false);
        for (int i = 0; i < _scene.GlbMaps.Count; i++)
            DrawMapHeader(_scene.GlbMaps[i], activeId, isGlb: true);
        ImGui.End();
    }

    /// Render a single map header row plus its children when active. Shared by
    /// both Dist and GlbMap iteration so the UI is byte-for-byte identical
    /// except for the GLB Materials subtree.
    private void DrawMapHeader(IMap m, Guid activeId, bool isGlb)
    {
        bool isActive = m.Id == activeId;
        string kindTag = isGlb ? "Sk8" : "DIST";
        string header = isActive ? $"[*] {m.Name}  [{kindTag}]" : $"     {m.Name}  [{kindTag}]";

        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow;
        if (isActive) flags |= ImGuiTreeNodeFlags.Selected | ImGuiTreeNodeFlags.DefaultOpen;

        string idTag = isGlb ? "glb" : "dist";
        bool open = ImGui.TreeNodeEx(header + "##" + idTag + m.Id, flags);
        TrackLeftPanelExtent();
        if (ImGui.IsItemClicked() && _scene.ActiveMapId != m.Id)
        {
            _scene.ActiveMapId = m.Id;
            _selected = null;
            _statusMessage = $"Active map: {m.Name} ({kindTag})";
        }
        if (!open) return;
        if (isActive)
        {
            DrawMapChildren(m);
            if (m is GlbMap g) DrawMaterialsBranch(g);
        }
        else
        {
            ImGui.TextDisabled("(switch active to edit)");
        }
        ImGui.TreePop();
    }

    private void DrawMapChildren(IMap m)
    {
        // Tree mirrors the DlcSpec structure:
        //   Map (Dist OR GlbMap)
        //   ├─ Freeskate    (each locator = one DLC menu entry; its Category
        //   │                drives both offline AND online menu grouping —
        //   │                online entries auto-derive from each Freeskate
        //   │                locator at export time)
        //   ├─ Challenges   (each expandable to its own locators + trigger volumes)
        //   └─ Loose        (anything not yet placed under a section)
        DrawSectionBranch(m, "Freeskate", OwnerKind.Freeskate);
        DrawChallengesBranch(m);
        DrawLooseBranch(m);
    }

    /// Freeskate / Online Freeskate sections only carry locators — those rows
    /// reference a spawn locator (and optional sub-locators) but no trigger
    /// volumes. Trigger volumes are a per-challenge concern, handled by
    /// DrawChallengesBranch.
    private void DrawSectionBranch(IMap m, string label, OwnerKind owner)
    {
        int locCount = m.Locators.Count(l => l.Owner == owner);
        bool addClicked;
        bool open = DrawTreeNodeWithRightPlus(
            treeLabel: $"{label} ({locCount})##sec{owner}{m.Id}",
            treeFlags: ImGuiTreeNodeFlags.DefaultOpen,
            buttonId: $"addl{owner}{m.Id}",
            out addClicked);
        if (addClicked) AddLocatorIntoSection(m, owner, null);
        if (!open) return;

        foreach (Locator l in m.Locators.Where(l => l.Owner == owner))
        {
            bool sel = ReferenceEquals(_selected, l);
            TrackLeftPanelLabelExtent(l.Name);
            if (ImGui.Selectable(l.Name + "##l" + l.Id, sel)) _selected = l;
        }
        ImGui.TreePop();
    }

    private void DrawChallengesBranch(IMap m)
    {
        bool addClicked;
        bool open = DrawTreeNodeWithRightPlus(
            treeLabel: $"Challenges ({m.Challenges.Count})##chs{m.Id}",
            treeFlags: ImGuiTreeNodeFlags.DefaultOpen,
            buttonId: $"addch{m.Id}",
            out addClicked);
        if (addClicked) RequestAddChallenge();
        if (!open) return;

        foreach (Challenge c in m.Challenges)
        {
            bool sel = ReferenceEquals(_selected, c);
            ImGuiTreeNodeFlags f = ImGuiTreeNodeFlags.OpenOnArrow;
            if (sel) f |= ImGuiTreeNodeFlags.Selected;
            bool forceOpenChallenge = _forceOpenChallengeId == c.Id;
            if (forceOpenChallenge) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            bool ch_open = ImGui.TreeNodeEx($"{c.Name} [{c.Type}]##c" + c.Id, f);
            TrackLeftPanelExtent();
            if (forceOpenChallenge) _forceOpenChallengeId = null;
            // Click anywhere on header selects (clicking the arrow also toggles open;
            // ImGui.NET 5.72 lacks IsItemToggledOpen so we accept the dual effect).
            if (ImGui.IsItemClicked()) _selected = c;
            if (!ch_open) continue;

            bool IsVisualIndicatorLocator(Guid locatorId) => c.ReferencesVisualLocator(locatorId);

            int tvCount = m.TriggerVolumes.Count(v => v.Owner == OwnerKind.Challenge && v.OwnerChallengeId == c.Id);

            List<Locator> visualForChallenge = m.Locators
                .Where(l =>
                    l.Owner == OwnerKind.Challenge &&
                    l.OwnerChallengeId == c.Id &&
                    IsVisualIndicatorLocator(l.Id))
                .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            int visualCount = visualForChallenge.Count;

            bool forceOpenVisuals = _forceOpenChallengeVisualsId == c.Id;
            if (forceOpenVisuals) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            bool addVisualClicked;
            bool visOpen = DrawTreeNodeWithRightPlus(
                treeLabel: $"Visual Locators ({visualCount})##chvis{c.Id}",
                treeFlags: ImGuiTreeNodeFlags.DefaultOpen,
                buttonId: $"addchvis{c.Id}",
                out addVisualClicked);
            if (addVisualClicked)
                ImGui.OpenPopup($"vl_add_menu_{c.Id}");
            if (ImGui.BeginPopup($"vl_add_menu_{c.Id}"))
            {
                if (ImGui.Selectable("Ribbon arrow (world)"))
                {
                    CreateWorldRibbonArrowLocator(c);
                    ImGui.CloseCurrentPopup();
                }
                if (ImGui.Selectable("Ribbon arrow (in-challenge)"))
                {
                    CreateInChallengeRibbonArrowLocator(c);
                    ImGui.CloseCurrentPopup();
                }
                if (ImGui.Selectable("Chevron"))
                {
                    CreateChevronLocator(c);
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
            if (visOpen)
            {
                if (visualCount == 0)
                    ImGui.TextDisabled("(none — use +)");
                foreach (Locator vl in visualForChallenge)
                {
                    string role = c.ChevronLocatorIds.Contains(vl.Id) ? "Chevron" : "Ribbon Arrow";
                    bool s = ReferenceEquals(_selected, vl);
                    string visualLabel = $"{vl.Name} ({role})";
                    TrackLeftPanelLabelExtent(visualLabel);
                    if (ImGui.Selectable($"{visualLabel}##vl{vl.Id}", s)) _selected = vl;
                }
                ImGui.TreePop();
            }
            if (forceOpenVisuals) _forceOpenChallengeVisualsId = null;

            int locCount = m.Locators.Count(l =>
                l.Owner == OwnerKind.Challenge &&
                l.OwnerChallengeId == c.Id &&
                !IsVisualIndicatorLocator(l.Id));
            bool forceOpenLocators = _forceOpenChallengeLocatorsId == c.Id;
            if (forceOpenLocators) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            bool addLocClicked;
            bool locOpen = DrawTreeNodeWithRightPlus(
                treeLabel: $"Locators ({locCount})##chloc{c.Id}",
                treeFlags: ImGuiTreeNodeFlags.DefaultOpen,
                buttonId: $"addchl{c.Id}",
                out addLocClicked);
            if (addLocClicked) AddLocatorIntoSection(m, OwnerKind.Challenge, c.Id);
            if (locOpen)
            {
                foreach (Locator l in m.Locators.Where(l =>
                    l.Owner == OwnerKind.Challenge &&
                    l.OwnerChallengeId == c.Id &&
                    !IsVisualIndicatorLocator(l.Id)))
                {
                    bool s = ReferenceEquals(_selected, l);
                    TrackLeftPanelLabelExtent(l.Name);
                    if (ImGui.Selectable(l.Name + "##l" + l.Id, s)) _selected = l;
                }
                ImGui.TreePop();
            }
            if (forceOpenLocators) _forceOpenChallengeLocatorsId = null;

            bool forceOpenVolumes = _forceOpenChallengeVolumesId == c.Id;
            if (forceOpenVolumes) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            bool addVolClicked;
            bool tvOpen = DrawTreeNodeWithRightPlus(
                treeLabel: $"Trigger Volumes ({tvCount})##chtv{c.Id}",
                treeFlags: ImGuiTreeNodeFlags.DefaultOpen,
                buttonId: $"addchv{c.Id}",
                out addVolClicked);
            if (addVolClicked) AddVolumeIntoSection(m, OwnerKind.Challenge, c.Id);
            if (tvOpen)
            {
                foreach (TriggerVolume v in m.TriggerVolumes.Where(v => v.Owner == OwnerKind.Challenge && v.OwnerChallengeId == c.Id))
                {
                    bool s = ReferenceEquals(_selected, v);
                    TrackLeftPanelLabelExtent(v.Name);
                    if (ImGui.Selectable(v.Name + "##v" + v.Id, s)) _selected = v;
                }
                ImGui.TreePop();
            }
            if (forceOpenVolumes) _forceOpenChallengeVolumesId = null;
            ImGui.TreePop();
        }
        ImGui.TreePop();
    }

    private bool DrawTreeNodeWithRightPlus(
        string treeLabel,
        ImGuiTreeNodeFlags treeFlags,
        string buttonId,
        out bool addClicked)
    {
        // Draw the tree row first so label text gets full width.
        // Then overlay a '+' button at the far right without consuming
        // layout width (keeps the row on ONE line and avoids clipping the label).
        addClicked = false;
        ImGuiTreeNodeFlags effectiveFlags = treeFlags | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick;
        // We draw '+' on top of the same row as the tree item. Without this,
        // the tree row can consume clicks and the overlapping SmallButton may
        // not fire reliably. `AllowOverlap` is the modern replacement for the
        // deprecated `SetItemAllowOverlap()` call — it's a flag on the item
        // itself, applied at construction.
        ImGui.SetNextItemAllowOverlap();
        bool open = ImGui.TreeNodeEx(treeLabel, effectiveFlags);

        // "+" SmallButton placed on the same line as the tree row. Adjacent
        // to the label with a fixed gap (not flush-right against the
        // scrollbar) — flush-right was fragile under the auto-shrinking
        // column because the row's right edge moves frame-to-frame as the
        // panel measures itself.
        var style = ImGui.GetStyle();
        ImGui.SameLine(0, style.ItemInnerSpacing.X + 6f);
        addClicked = ImGui.SmallButton($"+##{buttonId}");
        // Track the button's right edge so the panel widens to include the
        // "+" — measuring only the TreeNodeEx above leaves the column too
        // narrow for the button to fit, hiding it behind the scrollbar.
        TrackLeftPanelExtent();

        return open;
    }

    private void DrawLooseBranch(IMap m)
    {
        int looseLoc = m.Locators.Count(l => l.Owner == OwnerKind.Loose);
        int looseTv  = m.TriggerVolumes.Count(v => v.Owner == OwnerKind.Loose);
        if (looseLoc + looseTv == 0) return;     // hide when empty so it doesn't add visual noise
        if (!ImGui.TreeNodeEx($"Loose ({looseLoc + looseTv})##loose" + m.Id, ImGuiTreeNodeFlags.DefaultOpen)) return;
        TrackLeftPanelExtent();

        if (looseLoc > 0 && ImGui.TreeNodeEx($"Locators ({looseLoc})##looseloc" + m.Id, ImGuiTreeNodeFlags.DefaultOpen))
        {
            TrackLeftPanelExtent();
            foreach (Locator l in m.Locators.Where(l => l.Owner == OwnerKind.Loose))
            {
                bool sel = ReferenceEquals(_selected, l);
                TrackLeftPanelLabelExtent(l.Name);
                if (ImGui.Selectable(l.Name + "##l" + l.Id, sel)) _selected = l;
            }
            ImGui.TreePop();
        }
        if (looseTv > 0 && ImGui.TreeNodeEx($"Trigger Volumes ({looseTv})##loosetv" + m.Id, ImGuiTreeNodeFlags.DefaultOpen))
        {
            TrackLeftPanelExtent();
            foreach (TriggerVolume v in m.TriggerVolumes.Where(v => v.Owner == OwnerKind.Loose))
            {
                bool sel = ReferenceEquals(_selected, v);
                TrackLeftPanelLabelExtent(v.Name);
                if (ImGui.Selectable(v.Name + "##v" + v.Id, sel)) _selected = v;
            }
            ImGui.TreePop();
        }
        ImGui.TreePop();
    }

    /// GLB-specific section: search box + Blender-style scrollable material
    /// list. Click a row to set <c>_selected</c> to the matching
    /// <see cref="GlbMaterialAssignment"/> so the Inspector switches to its
    /// physics/audio/pattern/class fields. Filter is case-insensitive substring
    /// on the material name; cleared text shows the whole list.
    private void DrawMaterialsBranch(GlbMap g)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled($"Materials ({g.Materials.Count})");

        // Search bar. SetNextItemWidth(-1) makes the input fill the panel
        // width without forcing the auto-fit logic to widen for it.
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##matsearch", "Search...", ref _glbMaterialSearchText, 64);

        // Scrollable list — fills remaining vertical space inside the parent
        // map's tree node, MINUS one line of room reserved for the Splines
        // count below. Border on so the list reads as a distinct widget.
        float reservedBelow = ImGui.GetFrameHeightWithSpacing();
        Vector2 listSize = new(0f, Math.Max(120f, ImGui.GetContentRegionAvail().Y - reservedBelow - 4f));
        if (ImGui.BeginChild("matlist##" + g.Id, listSize, ImGuiChildFlags.Borders))
        {
            string filter = _glbMaterialSearchText;
            bool hasFilter = !string.IsNullOrEmpty(filter);
            int shown = 0;
            foreach (GlbMaterialAssignment ma in g.Materials)
            {
                if (hasFilter && ma.MaterialName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                bool sel = ReferenceEquals(_selected, ma);
                if (ImGui.Selectable(ma.MaterialName + "##ma" + ma.Id, sel))
                    _selected = ma;
                shown++;
            }
            if (shown == 0)
                ImGui.TextDisabled(hasFilter ? "(no matches)" : "(no materials)");
        }
        ImGui.EndChild();

        // Splines summary directly under the materials list. Just the count
        // for now — full per-spline authoring is a follow-up. GLB is the
        // source of truth: re-importing the .glb refreshes this list.
        ImGui.TextDisabled($"Splines ({g.Splines.Count})");
    }

    private void AddLocatorIntoSection(IMap m, OwnerKind owner, Guid? ownerChallengeId)
    {
        PushUndoSnapshot("Add locator");
        Locator l = new()
        {
            Name = NextLocatorName(m, owner, ownerChallengeId),
            Position = GetSpawnPositionFromViewRayOrOrigin(),
            Kind = owner switch
            {
                OwnerKind.Freeskate => LocatorKind.FreeskateAnchor,
                OwnerKind.Challenge => LocatorKind.ChallengeStart,
                _ => LocatorKind.Spawn,
            },
            Owner = owner,
            OwnerChallengeId = ownerChallengeId,
        };
        m.Locators.Add(l);
        if (owner == OwnerKind.Challenge && ownerChallengeId is Guid challengeId)
        {
            _forceOpenChallengeId = challengeId;
            _forceOpenChallengeLocatorsId = challengeId;
        }
        _selected = l;
        _statusMessage = $"Added locator '{l.Name}'.";
    }

    private void AddVolumeIntoSection(IMap m, OwnerKind owner, Guid? ownerChallengeId)
    {
        PushUndoSnapshot("Add volume");
        TriggerVolume v = new()
        {
            Name = NextVolumeName(m, owner, ownerChallengeId),
            Center = GetSpawnPositionFromViewRayOrOrigin(),
            HalfExtents = new Vector3(2, 2, 1),
            Owner = owner,
            OwnerChallengeId = ownerChallengeId,
        };
        m.TriggerVolumes.Add(v);
        if (owner == OwnerKind.Challenge && ownerChallengeId is Guid challengeId)
        {
            _forceOpenChallengeId = challengeId;
            _forceOpenChallengeLocatorsId = challengeId;
            _forceOpenChallengeVolumesId = challengeId;
        }
        _selected = v;
        _statusMessage = $"Added trigger volume '{v.Name}'.";
    }

    private static string NextLocatorName(IMap m, OwnerKind owner, Guid? challengeId)
    {
        string prefix = owner switch
        {
            OwnerKind.Freeskate => "freeskate_loc",
            OwnerKind.Challenge => "ch_loc",
            _                   => "locator",
        };
        return NextIndexedName(
            m.Locators.Where(l => l.Owner == owner && l.OwnerChallengeId == challengeId).Select(l => l.Name),
            prefix);
    }

    private static string NextVolumeName(IMap m, OwnerKind owner, Guid? challengeId)
    {
        string prefix = owner switch
        {
            OwnerKind.Freeskate => "freeskate_vol",
            OwnerKind.Challenge => "ch_vol",
            _                   => "volume",
        };
        return NextIndexedName(
            m.TriggerVolumes.Where(v => v.Owner == owner && v.OwnerChallengeId == challengeId).Select(v => v.Name),
            prefix);
    }

    private static string NextIndexedName(IEnumerable<string> existingNames, string prefix)
    {
        int maxIndex = 0;
        string stem = prefix + "_";
        foreach (string name in existingNames)
        {
            if (!name.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                continue;
            ReadOnlySpan<char> suffix = name.AsSpan(stem.Length);
            if (!int.TryParse(suffix, out int n))
                continue;
            if (n > maxIndex) maxIndex = n;
        }
        return $"{prefix}_{(maxIndex + 1):D2}";
    }

    private void DrawViewport(float x, float y, float w, float h, InputSnapshot input, float dt)
    {
        ImGui.SetNextWindowPos(new Vector2(x, y));
        ImGui.SetNextWindowSize(new Vector2(w, h));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.Begin("Viewport", PinnedPanelFlags());
        ImGui.PopStyleVar();
        if (ImGui.BeginTabBar("##viewport_tabs"))
        {
            if (ImGui.BeginTabItem("Scene"))
            {
                Vector2 panelTopLeft = ImGui.GetCursorScreenPos();
                Vector2 avail = ImGui.GetContentRegionAvail();
                uint vpW = (uint)Math.Max(16, (int)avail.X);
                uint vpH = (uint)Math.Max(16, (int)avail.Y);
                _lastSceneViewportPanelSize = new Vector2(vpW, vpH);
                _renderer.EnsureSize(vpW, vpH, _imguiRenderer);

                IntPtr binding = _renderer.GetImGuiBinding(_imguiRenderer);
                ImGui.Image(binding, new Vector2(vpW, vpH));

                bool hovered = ImGui.IsItemHovered();
                _viewportSceneImageHovered = hovered;
                Vector2 mouse = ImGui.GetMousePos();
                Vector2 mouseLocal = mouse - panelTopLeft;
                Vector2 panelSize = new(vpW, vpH);

                const ImGuiMouseButton MouseLeft = ImGuiMouseButton.Left;
                const ImGuiMouseButton MouseRight = ImGuiMouseButton.Right;
                const ImGuiMouseButton MouseMiddle = ImGuiMouseButton.Middle;
                bool shiftHeld = _heldKeys.Contains(Key.ShiftLeft) || _heldKeys.Contains(Key.ShiftRight);
                bool cameraFly = _camera.FlyMode;

                bool stealsRight = _binds.ToggleFlyCamera.IsMouse && _binds.ToggleFlyCamera.Mouse == MouseButton.Right;
                bool stealsMiddle = _binds.ToggleFlyCamera.IsMouse && _binds.ToggleFlyCamera.Mouse == MouseButton.Middle;
                ImGuiMouseButton lookImGui = MouseRight;
                ImGuiMouseButton panImGui = MouseMiddle;
                bool panNeedsShift = false;
                if (stealsRight)
                {
                    lookImGui = MouseMiddle;
                    panImGui = MouseMiddle;
                    panNeedsShift = true;
                }
                else if (stealsMiddle)
                {
                    lookImGui = MouseRight;
                    panImGui = MouseRight;
                    panNeedsShift = true;
                }

                bool consumedFlyToggle = false;
                // Thumb / side-button fly toggle is handled in ProcessGlobalMouseFlyToggleThumbFromSnapshot — ImGui click detection is unreliable for mouse 4/5.
                if (hovered && _binds.ToggleFlyCamera.IsMouse
                    && _binds.ToggleFlyCamera.Mouse is not (MouseButton.Button1 or MouseButton.Button2))
                {
                    int ti = ImGuiMouseButtonIndex(_binds.ToggleFlyCamera.Mouse);
                    if (ti >= 0 && ImGui.IsMouseClicked((ImGuiMouseButton)ti))
                    {
                        ToggleFlyCameraCore();
                        consumedFlyToggle = true;
                    }
                }

                bool lookHeld = ImGui.IsMouseDown(lookImGui);
                bool panHeld = !cameraFly && ImGui.IsMouseDown(panImGui) && (!panNeedsShift || shiftHeld);

                if (!consumedFlyToggle && hovered && (ImGui.IsMouseClicked(lookImGui) || (!cameraFly && ImGui.IsMouseClicked(panImGui) && (!panNeedsShift || shiftHeld))))
                {
                    _lastMousePos = mouse;
                    if (cameraFly)
                    {
                        _orbiting = ImGui.IsMouseClicked(lookImGui);
                        _panning = false;
                    }
                    else
                    {
                        _orbiting = ImGui.IsMouseClicked(lookImGui);
                        _panning = ImGui.IsMouseClicked(panImGui) && (!panNeedsShift || shiftHeld);
                        if (_orbiting)
                            TryRetargetOrbitToViewRay(panelSize);
                    }
                }
                if (!lookHeld) _orbiting = false;
                if (!panHeld) _panning = false;

                bool leftHeld = ImGui.IsMouseDown(MouseLeft);

                Vector2 delta = mouse - _lastMousePos;
                _lastMousePos = mouse;

                float wheel = hovered ? input.WheelDelta : 0f;
                if (cameraFly)
                {
                    if (wheel != 0)
                        AdjustFlyMoveSpeedFromWheel(wheel);
                    if (_orbiting)
                        _camera.UpdateFly(delta, _orbiting);
                }
                else
                {
                    if (_orbiting || _panning || wheel != 0)
                        _camera.Update(delta, wheel, _orbiting, _panning);
                }

                if (_activeGizmoAxis == GizmoAxis.None)
                    _hoveredGizmoAxis = HitTestGizmo(mouseLocal, panelSize);

                if (_activeGizmoAxis == GizmoAxis.None
                    && _hoveredGizmoAxis != GizmoAxis.None
                    && hovered
                    && ImGui.IsMouseClicked(MouseLeft))
                {
                    BeginGizmoDrag(_hoveredGizmoAxis, mouseLocal, panelSize);
                }

                if (_activeGizmoAxis != GizmoAxis.None)
                {
                    // The gizmo drag mutates the selected object's transform,
                    // which DetectRenderStateChanges can't see (it compares
                    // _selected by reference and the active axis is pinned for
                    // the whole drag). Without an explicit invalidate the
                    // offscreen FB is never re-rendered until the button is
                    // released — the viewport looks frozen mid-drag. Force a
                    // redraw every drag frame (and on release) so the object
                    // tracks the cursor live.
                    if (leftHeld) { UpdateGizmoDrag(mouseLocal, panelSize); Invalidate3D(); }
                    else { _activeGizmoAxis = GizmoAxis.None; Invalidate3D(); }
                }

                if (hovered && ImGui.IsMouseClicked(MouseLeft)
                    && _activeGizmoAxis == GizmoAxis.None
                    && _hoveredGizmoAxis == GizmoAxis.None)
                {
                    PickObject(mouseLocal, panelSize);
                }

                ImGuiIOPtr io = ImGui.GetIO();
                bool textActive = io.WantTextInput;
                Vector2 horiz = Vector2.Zero;
                float vert = 0;
                if (!textActive)
                {
                    if (_binds.CamForward.IsKey && _heldKeys.Contains(_binds.CamForward.Key)) horiz.Y += 1;
                    if (_binds.CamBack.IsKey && _heldKeys.Contains(_binds.CamBack.Key)) horiz.Y -= 1;
                    if (_binds.CamStrafeLeft.IsKey && _heldKeys.Contains(_binds.CamStrafeLeft.Key)) horiz.X -= 1;
                    if (_binds.CamStrafeRight.IsKey && _heldKeys.Contains(_binds.CamStrafeRight.Key)) horiz.X += 1;
                    if (_binds.CamUp.IsKey && _heldKeys.Contains(_binds.CamUp.Key)) vert += 1;
                    if (_binds.CamDown.IsKey && _heldKeys.Contains(_binds.CamDown.Key)) vert -= 1;
                    if (horiz.LengthSquared() > 1) horiz = Vector2.Normalize(horiz);
                }
                float speed = (_heldKeys.Contains(Key.ShiftLeft) || _heldKeys.Contains(Key.ShiftRight))
                    ? CameraMoveSpeedFast : CameraMoveSpeed;
                if (horiz != Vector2.Zero || vert != 0)
                    _camera.MoveLocal(horiz, vert, speed, dt);

                // Minimal HUD overlay — camera mode, tool, fps. Drawn
                // directly through the window's draw list so it doesn't
                // advance the cursor (the viewport image claims the whole
                // panel; we just paint on top).
                ImDrawListPtr dl = ImGui.GetWindowDrawList();
                uint hudColor = ImGui.GetColorU32(new Vector4(0.85f, 0.85f, 0.85f, 0.85f));
                string mode = cameraFly ? "FLY" : "ORBIT";
                dl.AddText(panelTopLeft + new Vector2(8, 6), hudColor, mode);
                dl.AddText(panelTopLeft + new Vector2(8, 22), hudColor,
                    $"tool: {_tool}    fps: {_lastFramesTotal}");

                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    private string DescribeSelected() => _selected switch
    {
        TriggerVolume v          => $"volume '{v.Name}'",
        Locator l                => $"locator '{l.Name}'",
        Challenge c              => $"challenge '{c.Name}'",
        GlbMaterialAssignment ma => $"material '{ma.MaterialName}'",
        _                        => "(none)"
    };

    /// <summary>
    /// Closest mesh hit along the ray through the viewport center — same ray and triangle tests as orbit retarget
    /// (<see cref="TryRetargetOrbitToViewRay"/>). Returns false if the panel is invalid, there are no meshes, or the ray misses.
    /// </summary>
    private bool TryGetClosestMeshHitFromViewCenter(Vector2 panelSize, out Vector3 hitWorld)
    {
        hitWorld = default;
        if (panelSize.X <= 0 || panelSize.Y <= 0 || _scene.Meshes.Count == 0)
            return false;

        Vector2 centerPx = panelSize * 0.5f;
        var (ro, rd) = Picking.MouseRay(centerPx, panelSize, _camera);

        const float minT = 1e-3f;
        float bestT = float.PositiveInfinity;
        Vector3 bestHit = default;

        foreach (ImportedMesh mesh in _scene.Meshes)
        {
            if (mesh.CpuPositions is null || mesh.CpuIndices is null || mesh.CpuIndices.Length < 3)
                continue;
            if (!Picking.RayAabb(ro, rd, mesh.BoundsMin, mesh.BoundsMax, out _))
                continue;

            Picking.RayMeshTrianglesClosest(
                ro, rd, mesh.CpuPositions, mesh.CpuIndices, minT, ref bestT, ref bestHit);
        }

        if (bestT >= float.PositiveInfinity)
            return false;

        hitWorld = bestHit;
        return true;
    }

    /// <summary>Spawn placement for new authored objects: view-center ray vs imported meshes, or <see cref="Vector3.Zero"/> on miss.</summary>
    private Vector3 GetSpawnPositionFromViewRayOrOrigin()
    {
        if (TryGetClosestMeshHitFromViewCenter(_lastSceneViewportPanelSize, out Vector3 hit))
            return hit;
        return Vector3.Zero;
    }

    /// Ray through viewport center vs imported mesh triangles (CPU copy from upload).
    /// Broad-phase AABB per mesh, then Möller–Trumbore two-sided tests. On a hit,
    /// orbit pivots at the nearest surface point while preserving eye position.
    /// On a miss, leaves target/yaw/pitch/distance unchanged (legacy orbit pivot).
    private void TryRetargetOrbitToViewRay(Vector2 panelSize)
    {
        if (_camera.FlyMode)
            return;
        if (!TryGetClosestMeshHitFromViewCenter(panelSize, out Vector3 bestHit))
            return;

        SnapOrbitPreservingEye(bestHit);
    }

    /// Keeps camera eye position fixed while moving the orbit pivot to newTarget
    /// and updating yaw, pitch, and distance to match OrbitCamera conventions.
    private void SnapOrbitPreservingEye(Vector3 newTarget)
    {
        Vector3 p = _camera.Position;
        Vector3 to = newTarget - p;
        float len = to.Length();
        if (len < 1e-4f)
            return;
        Vector3 f = to / len;
        float pitch = MathF.Asin(Math.Clamp(f.Y, -1f, 1f));
        float yaw = MathF.Atan2(f.X, f.Z);
        _camera.Target = newTarget;
        _camera.Distance = len;
        _camera.YawRadians = yaw;
        _camera.PitchRadians = pitch;
    }

    private void PickObject(Vector2 mouseLocal, Vector2 panelSize)
    {
        var (ro, rd) = Picking.MouseRay(mouseLocal, panelSize, _camera);
        object? best = null;

        // Prefer locators first so authored points inside trigger volumes are still
        // easy to select (outer wire volumes shouldn't "eat" the click).
        float bestLocatorT = float.PositiveInfinity;
        foreach (Locator l in _scene.Locators)
        {
            if (Picking.RaySphere(ro, rd, l.Position, 0.75f, out float t)
                && t < bestLocatorT)
            {
                bestLocatorT = t;
                best = l;
            }
        }

        // If no locator hit, pick trigger volumes. For nested/intersecting volumes,
        // prefer the smallest hit volume (inner over outer), then nearest as tie-breaker.
        if (best is null)
        {
            TriggerVolume? bestVolume = null;
            float bestVolumeMetric = float.PositiveInfinity;
            float bestVolumeT = float.PositiveInfinity;

            foreach (TriggerVolume v in _scene.TriggerVolumes)
            {
                if (!Picking.RayObb(ro, rd, v.Center, v.HalfExtents, v.RotationDegrees, out float t))
                    continue;

                float volumeMetric = v.HalfExtents.X * v.HalfExtents.Y * v.HalfExtents.Z;
                bool better =
                    volumeMetric < bestVolumeMetric - 1e-4f ||
                    (MathF.Abs(volumeMetric - bestVolumeMetric) <= 1e-4f && t < bestVolumeT);
                if (!better) continue;

                bestVolume = v;
                bestVolumeMetric = volumeMetric;
                bestVolumeT = t;
            }

            best = bestVolume;
        }

        // Third tier: triangle-pick against imported meshes. Only meaningful
        // when there's something to select in the Inspector — today that
        // means GLB meshes tagged with a material name (resolve to the
        // GlbMaterialAssignment for the active GlbMap). PSF/DIST meshes have
        // no per-mesh state to edit, so a click on a DIST mesh leaves
        // selection unchanged rather than clearing it (Blender-style: clicking
        // a non-selectable surface is a no-op, not a deselect).
        if (best is null)
        {
            ImportedMesh? hitMesh = TryPickMesh(ro, rd);
            if (hitMesh?.GlbMaterialName is string matName
                && _scene.ActiveGlbMap is GlbMap g
                && g.Materials.FirstOrDefault(a =>
                    string.Equals(a.MaterialName, matName, StringComparison.Ordinal)) is GlbMaterialAssignment ma)
            {
                _selected = ma;
                _statusMessage = $"Selected material '{ma.MaterialName}'.";
                return;
            }
            // Mesh hit but no actionable target (DIST mesh or material not in
            // active map's list) — keep current selection.
            if (hitMesh != null) return;
        }

        _selected = best;
        _statusMessage = best == null ? "Deselected." : $"Selected {DescribeSelected()}.";
    }

    /// Closest triangle hit across the active map's meshes. Same broad-phase
    /// AABB → Möller–Trumbore pattern as <see cref="TryGetClosestMeshHitFromViewCenter"/>.
    private ImportedMesh? TryPickMesh(Vector3 ro, Vector3 rd)
    {
        if (_scene.Meshes.Count == 0) return null;
        const float minT = 1e-3f;
        float bestT = float.PositiveInfinity;
        Vector3 bestHit = default;
        ImportedMesh? bestMesh = null;
        foreach (ImportedMesh mesh in _scene.Meshes)
        {
            if (mesh.CpuPositions is null || mesh.CpuIndices is null || mesh.CpuIndices.Length < 3)
                continue;
            if (!Picking.RayAabb(ro, rd, mesh.BoundsMin, mesh.BoundsMax, out _))
                continue;
            float prevBestT = bestT;
            Picking.RayMeshTrianglesClosest(
                ro, rd, mesh.CpuPositions, mesh.CpuIndices, minT, ref bestT, ref bestHit);
            if (bestT < prevBestT) bestMesh = mesh;
        }
        return bestMesh;
    }

    private Vector3 GetSelectedOrigin()
    {
        return _selected switch
        {
            TriggerVolume v => v.Center,
            Locator l       => l.Position,
            _               => Vector3.Zero,
        };
    }

    private void SetSelectedOrigin(Vector3 p)
    {
        switch (_selected)
        {
            case TriggerVolume v: v.Center = p; break;
            case Locator l:       l.Position = p; break;
        }
    }

    private GizmoAxis HitTestGizmo(Vector2 mouseLocal, Vector2 panelSize)
    {
        if (_selected is not (TriggerVolume or Locator)) return GizmoAxis.None;
        if (_tool == EditorTool.Select) return GizmoAxis.None;

        Vector3 origin = GetSelectedOrigin();
        float size = Renderer3D.GetGizmoSize();

        return _tool switch
        {
            EditorTool.Move   => HitTestAxisLines(mouseLocal, panelSize, origin, size),
            EditorTool.Scale  => HitTestAxisLines(mouseLocal, panelSize, origin, size),
            EditorTool.Rotate => HitTestRotateRings(mouseLocal, panelSize, origin, size),
            _                 => GizmoAxis.None,
        };
    }

    private GizmoAxis HitTestAxisLines(Vector2 mouseLocal, Vector2 panelSize, Vector3 origin, float size)
    {
        const float pickPx = 10f;
        float bestDist = pickPx;
        GizmoAxis best = GizmoAxis.None;
        foreach (GizmoAxis axis in new[] { GizmoAxis.X, GizmoAxis.Y, GizmoAxis.Z })
        {
            Vector3 dir = AxisDir(axis);
            if (!Picking.WorldToPanel(origin, panelSize, _camera, out Vector2 a)) continue;
            if (!Picking.WorldToPanel(origin + dir * size, panelSize, _camera, out Vector2 b)) continue;
            float d = Picking.PointToSegmentDistance(mouseLocal, a, b);
            if (d < bestDist) { bestDist = d; best = axis; }
        }
        return best;
    }

    /// Sample each ring in screen-space (32 segments) and pick the closest.
    private GizmoAxis HitTestRotateRings(Vector2 mouseLocal, Vector2 panelSize, Vector3 origin, float size)
    {
        const float pickPx = 10f;
        const int samples = 32;
        float bestDist = pickPx;
        GizmoAxis best = GizmoAxis.None;

        TestRing(GizmoAxis.X);
        TestRing(GizmoAxis.Y);
        TestRing(GizmoAxis.Z);
        return best;

        void TestRing(GizmoAxis axis)
        {
            Vector2 prev = default;
            bool havePrev = false;
            for (int i = 0; i <= samples; i++)
            {
                float a = i * MathF.PI * 2f / samples;
                Vector3 worldP = origin + RingPoint(axis, MathF.Cos(a), MathF.Sin(a)) * size;
                if (!Picking.WorldToPanel(worldP, panelSize, _camera, out Vector2 p))
                { havePrev = false; continue; }
                if (havePrev)
                {
                    float d = Picking.PointToSegmentDistance(mouseLocal, prev, p);
                    if (d < bestDist) { bestDist = d; best = axis; }
                }
                prev = p;
                havePrev = true;
            }
        }
    }

    /// Ring basis for the requested axis in the current gizmo space (world/local).
    /// u/v span the ring plane; n is the plane normal.
    private (Vector3 u, Vector3 v, Vector3 n) RingBasis(GizmoAxis axis)
    {
        static Vector3 SafeNorm(Vector3 d, Vector3 fallback)
        {
            return d.LengthSquared() < 1e-6f ? fallback : Vector3.Normalize(d);
        }

        Vector3 nx = AxisDir(GizmoAxis.X);
        Vector3 ny = AxisDir(GizmoAxis.Y);
        Vector3 nz = AxisDir(GizmoAxis.Z);
        return axis switch
        {
            GizmoAxis.X => (SafeNorm(ny, Vector3.UnitY), SafeNorm(nz, Vector3.UnitZ), SafeNorm(nx, Vector3.UnitX)),
            // Keep Y ring sign convention aligned with existing drag direction.
            GizmoAxis.Y => (SafeNorm(nx, Vector3.UnitX), SafeNorm(-nz, -Vector3.UnitZ), SafeNorm(ny, Vector3.UnitY)),
            GizmoAxis.Z => (SafeNorm(nx, Vector3.UnitX), SafeNorm(ny, Vector3.UnitY), SafeNorm(nz, Vector3.UnitZ)),
            _           => (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ),
        };
    }

    /// Maps (cosθ, sinθ) into the ring plane for the requested axis.
    private Vector3 RingPoint(GizmoAxis axis, float c, float s)
    {
        var (u, v, _) = RingBasis(axis);
        return u * c + v * s;
    }

    private Vector3 PlaneNormal(GizmoAxis axis)
    {
        var (_, _, n) = RingBasis(axis);
        return n;
    }

    private void BeginGizmoDrag(GizmoAxis axis, Vector2 mouseLocal, Vector2 panelSize)
    {
        _activeGizmoAxis = axis;
        _dragStartCenter = GetSelectedOrigin();
        _gizmoUndoCaptured = false;

        switch (_tool)
        {
            case EditorTool.Move:
            {
                var (ro, rd) = Picking.MouseRay(mouseLocal, panelSize, _camera);
                Picking.ClosestPointsOnLines(ro, rd, _dragStartCenter, AxisDir(axis), out _, out _dragStartAxisT);
                break;
            }
            case EditorTool.Rotate:
            {
                _dragStartRotationDeg = _selected switch
                {
                    TriggerVolume v => v.RotationDegrees,
                    Locator l       => l.RotationDegrees,
                    _               => Vector3.Zero,
                };
                _dragStartRotationQ = Quaternion.CreateFromRotationMatrix(SelectedRotationMatrix());
                (_dragRingU, _dragRingV, _dragRingN) = RingBasis(axis);
                _dragStartRingAngle = ComputeRingAngle(mouseLocal, panelSize, _dragStartCenter, _dragRingU, _dragRingV, _dragRingN);
                break;
            }
            case EditorTool.Scale:
            {
                if (_selected is TriggerVolume v) _dragStartHalfExtents = v.HalfExtents;
                _dragStartAxisDistance = MathF.Max(ProjectMouseOntoAxis(axis, mouseLocal, panelSize, _dragStartCenter), 0.001f);
                break;
            }
        }
    }

    private void UpdateGizmoDrag(Vector2 mouseLocal, Vector2 panelSize)
    {
        switch (_tool)
        {
            case EditorTool.Move:
            {
                var (ro, rd) = Picking.MouseRay(mouseLocal, panelSize, _camera);
                if (!Picking.ClosestPointsOnLines(ro, rd, _dragStartCenter, AxisDir(_activeGizmoAxis), out _, out float t))
                    return;
                Vector3 newOrigin = _dragStartCenter + AxisDir(_activeGizmoAxis) * (t - _dragStartAxisT);
                if (!_gizmoUndoCaptured && newOrigin != GetSelectedOrigin())
                {
                    PushUndoSnapshot("Move");
                    _gizmoUndoCaptured = true;
                }
                SetSelectedOrigin(newOrigin);
                break;
            }
            case EditorTool.Rotate:
            {
                float current = ComputeRingAngle(mouseLocal, panelSize, _dragStartCenter, _dragRingU, _dragRingV, _dragRingN);
                float delta = MathF.Atan2(MathF.Sin(current - _dragStartRingAngle), MathF.Cos(current - _dragStartRingAngle));
                float deltaDeg = delta * 180f / MathF.PI;
                float deltaRad = deltaDeg * MathF.PI / 180f;
                Vector3 localAxis = _activeGizmoAxis switch
                {
                    GizmoAxis.X => Vector3.UnitX,
                    GizmoAxis.Y => Vector3.UnitY,
                    GizmoAxis.Z => Vector3.UnitZ,
                    _ => Vector3.UnitY,
                };
                Quaternion qDeltaLocal = Quaternion.CreateFromAxisAngle(localAxis, deltaRad);
                Quaternion qNew = Quaternion.Normalize(_dragStartRotationQ * qDeltaLocal);
                Vector3 newRot = QuaternionToEditorEulerDegrees(qNew);
                Vector3 currentRot = _selected switch
                {
                    TriggerVolume tv => tv.RotationDegrees,
                    Locator ll => ll.RotationDegrees,
                    _ => Vector3.Zero,
                };
                if (!_gizmoUndoCaptured && newRot != currentRot)
                {
                    PushUndoSnapshot("Rotate");
                    _gizmoUndoCaptured = true;
                }
                if (_selected is TriggerVolume v) v.RotationDegrees = newRot;
                else if (_selected is Locator l)  l.RotationDegrees = newRot;
                break;
            }
            case EditorTool.Scale:
            {
                if (_selected is not TriggerVolume tv) break;
                float currentDist = MathF.Max(ProjectMouseOntoAxis(_activeGizmoAxis, mouseLocal, panelSize, _dragStartCenter), 0.001f);
                float factor = currentDist / _dragStartAxisDistance;
                Vector3 newExt = ApplyAxisScale(_dragStartHalfExtents, _activeGizmoAxis, factor);
                if (!_gizmoUndoCaptured && newExt != tv.HalfExtents)
                {
                    PushUndoSnapshot("Scale");
                    _gizmoUndoCaptured = true;
                }
                tv.HalfExtents = newExt;
                break;
            }
        }
    }

    private void CancelGizmoDrag()
    {
        if (_activeGizmoAxis == GizmoAxis.None) return;
        switch (_tool)
        {
            case EditorTool.Move:   SetSelectedOrigin(_dragStartCenter); break;
            case EditorTool.Rotate:
                if (_selected is TriggerVolume v) v.RotationDegrees = _dragStartRotationDeg;
                else if (_selected is Locator l)  l.RotationDegrees = _dragStartRotationDeg;
                break;
            case EditorTool.Scale:
                if (_selected is TriggerVolume tv) tv.HalfExtents = _dragStartHalfExtents;
                break;
        }
        _activeGizmoAxis = GizmoAxis.None;
        _gizmoUndoCaptured = false;
        Invalidate3D(); // repaint the reverted transform
    }

    /// Project mouse ray onto the world plane perpendicular to `axis` through `centre`,
    /// then return the angle (radians) from centre to the hit point measured in the ring's plane.
    private float ComputeRingAngle(GizmoAxis axis, Vector2 mouseLocal, Vector2 panelSize, Vector3 centre)
    {
        var (u, v, n) = RingBasis(axis);
        return ComputeRingAngle(mouseLocal, panelSize, centre, u, v, n);
    }

    private float ComputeRingAngle(
        Vector2 mouseLocal, Vector2 panelSize, Vector3 centre,
        Vector3 u, Vector3 v, Vector3 n)
    {
        var (ro, rd) = Picking.MouseRay(mouseLocal, panelSize, _camera);
        float denom = Vector3.Dot(rd, n);
        if (MathF.Abs(denom) < 1e-6f) return 0f;
        float t = Vector3.Dot(centre - ro, n) / denom;
        Vector3 hit = ro + rd * t - centre;
        return MathF.Atan2(Vector3.Dot(hit, v), Vector3.Dot(hit, u));
    }

    /// Project mouse ray onto the axis line, return distance from `centre` along the axis.
    private float ProjectMouseOntoAxis(GizmoAxis axis, Vector2 mouseLocal, Vector2 panelSize, Vector3 centre)
    {
        var (ro, rd) = Picking.MouseRay(mouseLocal, panelSize, _camera);
        Picking.ClosestPointsOnLines(ro, rd, centre, AxisDir(axis), out _, out float t);
        return t;
    }

    private static Vector3 ApplyAxisScale(Vector3 baseExt, GizmoAxis axis, float factor) => axis switch
    {
        GizmoAxis.X => new Vector3(baseExt.X * factor, baseExt.Y, baseExt.Z),
        GizmoAxis.Y => new Vector3(baseExt.X, baseExt.Y * factor, baseExt.Z),
        GizmoAxis.Z => new Vector3(baseExt.X, baseExt.Y, baseExt.Z * factor),
        _           => baseExt * factor,
    };

    private static float NormalizeAngle(float deg)
    {
        while (deg > 180f) deg -= 360f;
        while (deg < -180f) deg += 360f;
        return deg;
    }

    /// World axis direction, OR local axis direction (rotated by the selected object) when
    /// the Move tool is in Local space. Used by both picking and drag math so they stay in
    /// sync with what the renderer draws.
    private Vector3 AxisDir(GizmoAxis a)
    {
        Vector3 baseDir = a switch
        {
            GizmoAxis.X => Vector3.UnitX,
            GizmoAxis.Y => Vector3.UnitY,
            GizmoAxis.Z => Vector3.UnitZ,
            _           => Vector3.Zero,
        };
        // Local-space axis directions for: Move when toggled to Local, Scale always,
        // and Rotate (local-axis rotation gizmo).
        bool localized =
            (_tool == EditorTool.Move && _moveSpace == GizmoSpace.Local) ||
            _tool == EditorTool.Scale ||
            _tool == EditorTool.Rotate;
        if (!localized) return baseDir;
        Vector3 d = Vector3.TransformNormal(baseDir, SelectedRotationMatrix());
        return d.LengthSquared() < 1e-6f ? baseDir : Vector3.Normalize(d);
    }

    private Matrix4x4 SelectedRotationMatrix()
    {
        Vector3 deg = _selected switch
        {
            TriggerVolume v => v.RotationDegrees,
            Locator l       => l.RotationDegrees,
            _               => Vector3.Zero,
        };
        const float toRad = MathF.PI / 180f;
        return Matrix4x4.CreateFromYawPitchRoll(deg.Y * toRad, deg.X * toRad, deg.Z * toRad);
    }

    /// Convert quaternion to editor Euler degrees in the same axis semantics as
    /// CreateFromYawPitchRoll(yaw=Y, pitch=X, roll=Z).
    private static Vector3 QuaternionToEditorEulerDegrees(Quaternion q)
    {
        Matrix4x4 m = Matrix4x4.CreateFromQuaternion(q);

        // Extraction for Yaw(Y)-Pitch(X)-Roll(Z) convention used by CreateFromYawPitchRoll.
        float pitchX = MathF.Asin(Math.Clamp(-m.M32, -1f, 1f));
        float yawY;
        float rollZ;
        if (MathF.Abs(m.M32) < 0.999999f)
        {
            yawY = MathF.Atan2(m.M31, m.M33);
            rollZ = MathF.Atan2(m.M12, m.M22);
        }
        else
        {
            // Near gimbal lock: preserve a stable yaw and collapse roll.
            yawY = MathF.Atan2(-m.M13, m.M11);
            rollZ = 0f;
        }

        const float toDeg = 180f / MathF.PI;
        return new Vector3(
            NormalizeAngle(pitchX * toDeg),
            NormalizeAngle(yawY * toDeg),
            NormalizeAngle(rollZ * toDeg));
    }

    private void DeleteSelected()
    {
        if (_selected is null) return;
        PushUndoSnapshot("Delete");
        switch (_selected)
        {
            case TriggerVolume v:
                _scene.TriggerVolumes.Remove(v);
                foreach (Challenge ch in _scene.Challenges)
                {
                    if (ch.ScoringVolumeId == v.Id) ch.ScoringVolumeId = null;
                    if (ch.DiscoveryBoundaryId == v.Id) ch.DiscoveryBoundaryId = null;
                    if (ch.ChallengeBoundaryId == v.Id) ch.ChallengeBoundaryId = null;
                }
                _statusMessage = $"Removed volume '{v.Name}'.";
                _selected = null;
                break;
            case Locator l:
                _scene.Locators.Remove(l);
                foreach (Challenge ch in _scene.Challenges)
                {
                    if (ch.StartLocatorId == l.Id) ch.StartLocatorId = null;
                    if (ch.VisualSignupLocatorId == l.Id) ch.VisualSignupLocatorId = null;
                    ch.InChallengeRibbonArrowLocatorIds.Remove(l.Id);
                    ch.ChevronLocatorIds.Remove(l.Id);
                }
                _statusMessage = $"Removed locator '{l.Name}'.";
                _selected = null;
                break;
            case Challenge c:
                _scene.Challenges.Remove(c);
                _statusMessage = $"Removed challenge '{c.Name}'.";
                _selected = null;
                break;
        }
    }

    private void DrawInspector(float x, float y, float w, float h)
    {
        ImGui.SetNextWindowPos(new Vector2(x, y));
        ImGui.SetNextWindowSize(new Vector2(w, h));
        ImGui.Begin("Inspector", PinnedPanelFlags());
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(5, 1));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 2));

        // Package + DIST headers used to live here, but they conflated
        // DLC-wide actions with per-object authoring. Export DLC is now
        // exclusively under File ▸ Export DLC..., and per-object metadata
        // (including each Freeskate locator's Category) lives on the
        // selected object's inspector below.
        DrawExportNamePrompt();

        switch (_selected)
        {
            case TriggerVolume v: DrawVolumeInspector(v); break;
            case Locator l: DrawLocatorInspector(l); break;
            case Challenge c: DrawChallengeInspector(c); break;
            case GlbMaterialAssignment ma: DrawGlbMaterialInspector(ma); break;
            default: ImGui.TextDisabled("Select an object."); break;
        }

        // Materials aren't user-deletable (they're tied to GLB material names);
        // hide the Delete button + visual-locator hint when one is selected.
        if (_selected != null && _selected is not GlbMaterialAssignment)
        {
            ImGui.Spacing();
            ImGui.Separator();
            if (ImGui.Button("Delete item", new Vector2(-1, 0))) DeleteSelected();

            if (_selected is Locator loc
                && loc.Owner == OwnerKind.Challenge
                && loc.OwnerChallengeId is Guid och
                && _scene.ActiveMap?.Challenges.FirstOrDefault(ch => ch.Id == och) is { } chal)
            {
                string? visualType = null;
                if (chal.ChevronLocatorIds.Contains(loc.Id))
                    visualType = "Chevron";
                else if (chal.VisualSignupLocatorId == loc.Id || chal.InChallengeRibbonArrowLocatorIds.Contains(loc.Id))
                    visualType = "Ribbon Arrow";
                if (visualType is not null)
                {
                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.TextDisabled("Type");
                    ImGui.Text(visualType);
                }
            }
        }

        ImGui.PopStyleVar(2);
        ImGui.End();
    }

    /// <summary>
    /// Pixel width for the value side of label+widget rows. Sized from the
    /// available region inside the inspector (which itself auto-fits the
    /// column to content); falls back to the min panel width minus padding
    /// when the inspector hasn't laid out yet.
    /// </summary>
    private float InspectorValueWidth()
    {
        float avail = ImGui.GetContentRegionAvail().X;
        if (avail < 4f)
            avail = Math.Max(120f, MinLeftPanelWidth - ImGui.GetStyle().WindowPadding.X * 2f - 16f);
        float reserve = Math.Max(
            158f,
            ImGui.CalcTextSize("Challenge boundary").X + ImGui.GetStyle().ItemInnerSpacing.X + 6f);
        return Math.Max(44f, avail - reserve);
    }

    /// <summary>
    /// Tracks the rightmost X of the most-recently-submitted ImGui item so
    /// the left column can auto-fit content next frame. Call right after a
    /// <c>TreeNodeEx</c>, <c>Text</c>, <c>Button</c>, etc — items whose
    /// natural rect is fitted to their content. Do NOT call after
    /// <c>Selectable</c> (which defaults to full-width — would prevent the
    /// column from ever shrinking); use <see cref="TrackLeftPanelLabelExtent"/>
    /// instead.
    /// </summary>
    private void TrackLeftPanelExtent()
    {
        float x = ImGui.GetItemRectMax().X;
        if (x > _frameLeftPanelMaxX) _frameLeftPanelMaxX = x;
    }

    /// <summary>
    /// Variant of <see cref="TrackLeftPanelExtent"/> that measures by the
    /// label's natural text width (via <c>CalcTextSize</c>) instead of the
    /// item's submitted rect. Use right BEFORE a full-width <c>Selectable</c>
    /// so the column shrinks when leaf names are short. Pass the visible
    /// label only (no <c>##id</c> suffix) so the measurement matches what
    /// the user sees on screen.
    /// </summary>
    private void TrackLeftPanelLabelExtent(string visibleLabel)
    {
        float natural = ImGui.GetCursorScreenPos().X
            + ImGui.CalcTextSize(visibleLabel).X
            + ImGui.GetStyle().FramePadding.X * 2f;
        if (natural > _frameLeftPanelMaxX) _frameLeftPanelMaxX = natural;
    }

    private void DrawVolumeInspector(TriggerVolume v)
    {
        ImGui.TextColored(new Vector4(0.6f, 0.85f, 1f, 1f), "Trigger Volume");
        ImGui.Separator();
        ImGui.PushItemWidth(InspectorValueWidth());
        string name = v.Name;
        if (ImGui.InputText("Name", ref name, 64)) v.Name = name;
        Vector3 center = v.Center;
        if (ImGui.DragFloat3("Center", ref center, 0.1f)) { v.Center = center; _needs3DRedraw = true; }
        Vector3 he = v.HalfExtents;
        if (ImGui.DragFloat3("Half-extents", ref he, 0.1f, 0.05f, 100f)) { v.HalfExtents = he; _needs3DRedraw = true; }
        Vector3 rot = v.RotationDegrees;
        if (ImGui.DragFloat3("Rotation XYZ°", ref rot, 1f, -180f, 180f)) { v.RotationDegrees = rot; _needs3DRedraw = true; }
        ImGui.PopItemWidth();
        ImGui.Spacing();
        ImGui.TextDisabled($"ID: {v.Id.ToString("N").Substring(0, 8)}");
    }

    private void DrawLocatorInspector(Locator l)
    {
        // Each Freeskate-owned locator becomes its own DLC location entry on
        // export — its Name shows up in the menu and Category groups it with
        // siblings under one heading. Show those as the headline so users
        // editing a Freeskate locator see "this is a location" up front.
        bool isFreeskateLocation = l.Owner == OwnerKind.Freeskate;
        ImGui.TextColored(
            new Vector4(1f, 0.85f, 0.4f, 1f),
            isFreeskateLocation ? "Location (Freeskate)" : "Locator");
        ImGui.Separator();

        ImGui.PushItemWidth(InspectorValueWidth());
        string name = l.Name;
        if (ImGui.InputText("Name", ref name, 64)) l.Name = name;

        if (isFreeskateLocation)
        {
            string category = l.Category;
            if (ImGui.InputText("Category", ref category, 32)) l.Category = category;
            if (string.IsNullOrWhiteSpace(category))
                ImGui.TextDisabled("(blank → grouped under \"Maps\")");
            else
                ImGui.TextDisabled("Locations sharing this Category share a menu heading.");
        }

        Vector3 pos = l.Position;
        if (ImGui.DragFloat3("Position", ref pos, 0.1f)) { l.Position = pos; _needs3DRedraw = true; }
        Vector3 rot = l.RotationDegrees;
        if (ImGui.DragFloat3("Rotation XYZ°", ref rot, 1f, -180f, 180f)) { l.RotationDegrees = rot; _needs3DRedraw = true; }
        int kind = (int)l.Kind;
        if (ImGui.Combo("Kind", ref kind, "Spawn\0ChallengeStart\0FreeskateAnchor\0Sub\0")) { l.Kind = (LocatorKind)kind; _needs3DRedraw = true; }
        ImGui.PopItemWidth();
    }

    /// Inspector view for a single material assignment on a <see cref="GlbMap"/>.
    /// Fields and their dropdown entries are both alphabetized so the user can
    /// scan quickly. Combo index ↔ enum value goes through
    /// <see cref="PhysicsMaterialLabels"/> (sorted labels + mapping table)
    /// because the underlying numeric IDs aren't in alphabetical order.
    private void DrawGlbMaterialInspector(GlbMaterialAssignment ma)
    {
        ImGui.TextColored(new Vector4(1f, 0.7f, 0.95f, 1f), "Material");
        ImGui.Separator();
        ImGui.PushItemWidth(InspectorValueWidth());

        // Material name is the join key on re-import — never user-editable.
        ImGui.TextDisabled("Name");
        ImGui.SameLine();
        ImGui.TextUnformatted(ma.MaterialName);

        // Fields in alphabetical order: Attributor Class, Audio Surface,
        // Physics Surface, Surface Pattern.
        int classIdx = PhysicsMaterialLabels.Class.IndexOf(ma.MaterialClass);
        if (ImGui.Combo("Attributor Class", ref classIdx,
                PhysicsMaterialLabels.Class.Labels, PhysicsMaterialLabels.Class.Count))
            ma.MaterialClass = PhysicsMaterialLabels.Class.ValueAt(classIdx);

        int audioIdx = PhysicsMaterialLabels.Audio.IndexOf(ma.Audio);
        if (ImGui.Combo("Audio Surface", ref audioIdx,
                PhysicsMaterialLabels.Audio.Labels, PhysicsMaterialLabels.Audio.Count))
            ma.Audio = PhysicsMaterialLabels.Audio.ValueAt(audioIdx);

        int physicsIdx = PhysicsMaterialLabels.Physics.IndexOf(ma.Physics);
        if (ImGui.Combo("Physics Surface", ref physicsIdx,
                PhysicsMaterialLabels.Physics.Labels, PhysicsMaterialLabels.Physics.Count))
            ma.Physics = PhysicsMaterialLabels.Physics.ValueAt(physicsIdx);

        int patternIdx = PhysicsMaterialLabels.Pattern.IndexOf(ma.Pattern);
        if (ImGui.Combo("Surface Pattern", ref patternIdx,
                PhysicsMaterialLabels.Pattern.Labels, PhysicsMaterialLabels.Pattern.Count))
            ma.Pattern = PhysicsMaterialLabels.Pattern.ValueAt(patternIdx);

        ImGui.PopItemWidth();

        bool excludeCol = ma.ExcludeCollision;
        if (ImGui.Checkbox("Exclude from collision", ref excludeCol)) ma.ExcludeCollision = excludeCol;
        bool excludePres = ma.ExcludePres;
        if (ImGui.Checkbox("Exclude from pres", ref excludePres)) ma.ExcludePres = excludePres;

        ImGui.Spacing();
        ImGui.TextDisabled($"ID: {ma.Id.ToString("N").Substring(0, 8)}");
    }

    private void DrawChallengeInspector(Challenge c)
    {
        ImGui.TextColored(new Vector4(0.7f, 1f, 0.6f, 1f), $"Challenge — {c.Type}");
        ImGui.Separator();
        float cw = InspectorValueWidth();
        string name = c.Name;
        ImGui.PushItemWidth(cw);
        if (ImGui.InputText("Name", ref name, 64)) c.Name = name;
        // Type is fixed at creation (picked via Add Challenge popup) — see
        // DrawChallengeTypePicker. Different types reference different
        // authoring data (OTS volumes vs race gates), so post-creation
        // mutation would orphan references. Type shown read-only above.
        // Score/XP fields are OTS-only. Race uses heat timing
        // (TimeLimit + KilledItSeconds), Skate uses turn timer +
        // owned-it credit reward. Hide on Race + Skate; underlying scene
        // fields still serialize so flipping type back to OTS doesn't
        // lose previously-authored point values.
        if (c.Type != ChallengeType.Race && c.Type != ChallengeType.Skate)
        {
            int owned = c.OwnedPoints;
            if (ImGui.DragInt("Owned points", ref owned, 5f, 0, 100_000)) c.OwnedPoints = owned;
            int killedIt = c.KilledItPoints;
            if (ImGui.DragInt("Killed-It points", ref killedIt, 5f, 0, 100_000)) c.KilledItPoints = killedIt;
            int onlineXp = c.OnlineBonusXp;
            if (ImGui.DragInt("Online bonus XP", ref onlineXp, 5f, 0, 100_000)) c.OnlineBonusXp = onlineXp;
        }
        ImGui.PopItemWidth();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("References");
        cw = InspectorValueWidth();
        // OTS-only top-level references. Race uses a separate gates panel
        // below; Skate uses its own ChallengeBoundary + SpotVolumes panel.
        if (c.Type != ChallengeType.Race && c.Type != ChallengeType.Skate)
        {
            DrawObjectRefCombo("Scoring volume", _scene.TriggerVolumes, c.ScoringVolumeId, id => c.ScoringVolumeId = id, cw);
            DrawObjectRefCombo("Challenge boundary", _scene.TriggerVolumes, c.ChallengeBoundaryId, id => c.ChallengeBoundaryId = id, cw);
        }
        var startOptions = _scene.Locators
            .Where(l => l.Kind == LocatorKind.ChallengeStart)
            .OrderByDescending(l => l.Owner == OwnerKind.Challenge && l.OwnerChallengeId == c.Id)
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        DrawLocatorRefCombo("Start locator", startOptions, c.StartLocatorId, id => c.StartLocatorId = id, cw);

        // Race-only authoring section. Gates list + timing + variant flags.
        if (c.Type == ChallengeType.Race)
            DrawRaceAuthoringSection(c, cw);

        // Skate-only authoring section (Game of S.K.A.T.E.).
        if (c.Type == ChallengeType.Skate)
            DrawSkateAuthoringSection(c, cw);

        // OTS-only "Visual Locators" section (world/signup ribbon arrow,
        // in-challenge ribbon arrows, chevron locators). Skate uses its own
        // VisualIndicators list — hide to avoid confusion.
        ImGui.Spacing();
        if (c.Type != ChallengeType.Skate && ImGui.TreeNode("Visual Locators"))
        {
            cw = InspectorValueWidth();
            var visualLocatorOptions = _scene.Locators
                .OrderByDescending(l => l.Owner == OwnerKind.Challenge && l.OwnerChallengeId == c.Id)
                .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ImGui.TextDisabled("World / signup");
            DrawLocatorRefCombo("Ribbon arrow", visualLocatorOptions, c.VisualSignupLocatorId, id =>
            {
                PushUndoSnapshot("Ribbon arrow ref");
                c.VisualSignupLocatorId = id;
            }, cw);
            if (ImGui.Button("Create ribbon arrow locator"))
                CreateWorldRibbonArrowLocator(c);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextDisabled("In-challenge");
            for (int i = 0; i < c.InChallengeRibbonArrowLocatorIds.Count; i++)
            {
                int idx = i;
                Guid cur = c.InChallengeRibbonArrowLocatorIds[idx];
                DrawLocatorRefCombo($"Ribbon arrow {idx + 1}", visualLocatorOptions, cur, id =>
                {
                    PushUndoSnapshot("In-challenge ribbon ref");
                    if (id is Guid ng)
                        c.InChallengeRibbonArrowLocatorIds[idx] = ng;
                    else
                        c.InChallengeRibbonArrowLocatorIds.RemoveAt(idx);
                }, cw);
                ImGui.SameLine();
                if (ImGui.Button($"Remove##inchrb{idx}"))
                {
                    PushUndoSnapshot("Remove in-challenge ribbon");
                    c.InChallengeRibbonArrowLocatorIds.RemoveAt(idx);
                }
            }

            if (ImGui.Button("Add in-challenge ribbon arrow"))
                CreateInChallengeRibbonArrowLocator(c);

            ImGui.Spacing();
            for (int i = 0; i < c.ChevronLocatorIds.Count; i++)
            {
                int idx = i;
                Guid cur = c.ChevronLocatorIds[idx];
                DrawLocatorRefCombo($"Chevron {idx + 1}", visualLocatorOptions, cur, id =>
                {
                    PushUndoSnapshot("Chevron ref");
                    if (id is Guid ng)
                        c.ChevronLocatorIds[idx] = ng;
                    else
                        c.ChevronLocatorIds.RemoveAt(idx);
                }, cw);
                ImGui.SameLine();
                if (ImGui.Button($"Remove##chev{idx}"))
                {
                    PushUndoSnapshot("Remove chevron");
                    c.ChevronLocatorIds.RemoveAt(idx);
                }
            }

            if (ImGui.Button("Add chevron locator"))
                CreateChevronLocator(c);

            ImGui.TreePop();
        }
    }

    /// Race-only inspector section. Renders below the shared challenge
    /// fields when <see cref="ChallengeType.Race"/> is selected. UI shape
    /// follows the minimum-viable race authoring spec: a flat ordered list
    /// of gate trigger volumes (single heat, single leg under the hood) +
    /// heat timing + variant flags. Maps 1-1 to
    /// <see cref="Challenge.RaceGateVolumeIds"/> / Race* properties.
    private void DrawRaceAuthoringSection(Challenge c, float cw)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.9f, 0.7f, 1f, 1f), "Race");
        ImGui.Spacing();

        ImGui.PushItemWidth(cw);
        int timeLimit = c.RaceTimeLimitSeconds;
        if (ImGui.DragInt("Time limit (s)", ref timeLimit, 1f, 1, 3600)) c.RaceTimeLimitSeconds = timeLimit;
        float killedIt = c.RaceKilledItSeconds;
        if (ImGui.DragFloat("Killed-It time (s)", ref killedIt, 0.5f, 0.1f, 3600f, "%.2f")) c.RaceKilledItSeconds = killedIt;
        ImGui.PopItemWidth();

        bool skipable = c.RaceGateSkipable;
        if (ImGui.Checkbox("Gate skipable", ref skipable)) c.RaceGateSkipable = skipable;
        ImGui.SameLine();
        ImGui.TextDisabled("(missed gates don't fail the heat)");

        bool deathRace = c.IsDeathRace;
        if (ImGui.Checkbox("Death Race (online variant)", ref deathRace)) c.IsDeathRace = deathRace;

        ImGui.Spacing();
        ImGui.Text($"Gates ({c.RaceGateVolumeIds.Count})");
        ImGui.SameLine();
        ImGui.TextDisabled("— last gate becomes FINISH");

        // Per-gate row: up/down arrows, volume combo, remove (X).
        // We collect mutations into local variables so we don't mutate the
        // list during the foreach. Apply them after the loop.
        int? moveUpIndex = null;
        int? moveDownIndex = null;
        int? removeIndex = null;

        for (int i = 0; i < c.RaceGateVolumeIds.Count; i++)
        {
            Guid currentGate = c.RaceGateVolumeIds[i];
            ImGui.PushID($"gate_{i}");

            // Up / down reorder buttons. Always rendered; clicks at the
            // ends are no-ops (the index guards in the mutation block
            // below skip them). The older ImGui.NET binding shipped here
            // lacks `BeginDisabled`/`EndDisabled` for proper greying.
            if (ImGui.ArrowButton("up", ImGuiDir.Up)) moveUpIndex = i;
            ImGui.SameLine();
            if (ImGui.ArrowButton("down", ImGuiDir.Down)) moveDownIndex = i;

            // Gate combo — pick a TriggerVolume from the active DIST. Width
            // sized to leave room for the remove button at the right edge.
            ImGui.SameLine();
            float available = ImGui.GetContentRegionAvail().X;
            float removeButtonWidth = 30f;
            ImGui.SetNextItemWidth(available - removeButtonWidth - ImGui.GetStyle().ItemSpacing.X);
            string preview = _scene.TriggerVolumes.FirstOrDefault(v => v.Id == currentGate) is { } gv
                ? $"#{i + 1:D2} — {gv.Name}"
                : $"#{i + 1:D2} — (none)";
            if (ImGui.BeginCombo("##gate_picker", preview))
            {
                if (ImGui.Selectable("(none)", currentGate == Guid.Empty))
                    c.RaceGateVolumeIds[i] = Guid.Empty;
                foreach (TriggerVolume opt in _scene.TriggerVolumes)
                {
                    bool sel = opt.Id == currentGate;
                    if (ImGui.Selectable(opt.Name + "##gate_opt_" + opt.Id, sel))
                        c.RaceGateVolumeIds[i] = opt.Id;
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if (ImGui.Button("X")) removeIndex = i;

            ImGui.PopID();
        }

        if (moveUpIndex is int ui && ui > 0)
        {
            (c.RaceGateVolumeIds[ui - 1], c.RaceGateVolumeIds[ui]) =
                (c.RaceGateVolumeIds[ui], c.RaceGateVolumeIds[ui - 1]);
        }
        else if (moveDownIndex is int di && di < c.RaceGateVolumeIds.Count - 1)
        {
            (c.RaceGateVolumeIds[di], c.RaceGateVolumeIds[di + 1]) =
                (c.RaceGateVolumeIds[di + 1], c.RaceGateVolumeIds[di]);
        }
        else if (removeIndex is int ri)
        {
            c.RaceGateVolumeIds.RemoveAt(ri);
        }

        if (ImGui.Button("+ Add gate"))
        {
            // Default new gate to the first available volume so the user
            // immediately sees a populated slot. Empty Guid is fine too —
            // validator will flag at build time.
            Guid initial = _scene.TriggerVolumes.Count > 0
                ? _scene.TriggerVolumes[0].Id
                : Guid.Empty;
            c.RaceGateVolumeIds.Add(initial);
        }
        if (c.RaceGateVolumeIds.Count == 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f),
                "(no gates yet — race won't build until you add at least one)");
        }
    }

    /// Skate-only inspector section (Game of S.K.A.T.E.). Renders below the
    /// shared challenge fields when <see cref="ChallengeType.Skate"/> is
    /// selected. Maps to <see cref="Challenge"/>.Skate* properties; data
    /// shape mirrors the 10 base-game retail spots (skate_dwtn_01..04 etc.).
    /// Per-spot needs: 1-2 SpotVolumes + ChallengeBoundary +
    /// TurnBasedStartVolume + start/wait locators + 1-2 visual indicators.
    private void DrawSkateAuthoringSection(Challenge c, float cw)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "Skate (Game of S.K.A.T.E.)");
        ImGui.TextDisabled("Turn-based copy-trick. Online-only.");
        ImGui.Spacing();

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.20f, 0.50f, 0.30f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.30f, 0.65f, 0.40f, 1f));
        if (ImGui.Button("+ Create all Skate volumes & locators", new Vector2(cw, 28f)))
            CreateFullSkateSpot(c);
        ImGui.PopStyleColor(2);
        ImGui.TextDisabled("Click once -> 3 volumes + 3 locators placed at view-centre. Drag to position.");

        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "TUNING");
        ImGui.Separator();
        ImGui.PushItemWidth(cw);
        float timeLimit = c.SkateTimeLimitSeconds;
        if (ImGui.DragFloat("Turn timer (s)", ref timeLimit, 0.5f, 0.1f, 600f, "%.1f"))
            c.SkateTimeLimitSeconds = timeLimit;

        int reward = c.SkateOwnedItRewardCredits;
        if (ImGui.DragInt("Owned-it reward (credits)", ref reward, 50f, 0, 100_000))
            c.SkateOwnedItRewardCredits = reward;
        ImGui.PopItemWidth();

        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "VOLUMES");
        ImGui.Separator();
        var triggerOptions = _scene.TriggerVolumes;
        DrawObjectRefCombo("Challenge boundary", triggerOptions,
            c.ChallengeBoundaryId, id => c.ChallengeBoundaryId = id, cw);
        DrawObjectRefCombo("Start volume", triggerOptions,
            c.SkateTurnBasedStartVolumeId, id => c.SkateTurnBasedStartVolumeId = id, cw);
        DrawSingleGuidFromListCombo("Spot volume", triggerOptions.Select(v => (v.Id, v.Name)).ToList(),
            c.SkateSpotVolumeIds, cw);

        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "LOCATORS");
        ImGui.Separator();
        ImGui.TextDisabled("Start locator picked in 'References' above.");
        var locatorOptions = _scene.Locators
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        DrawLocatorRefCombo("Wait locator", locatorOptions, c.SkateWaitLocatorId,
            id => c.SkateWaitLocatorId = id, cw);
        DrawSingleGuidFromListCombo("Visual indicator", locatorOptions.Select(l => (l.Id, l.Name)).ToList(),
            c.SkateVisualIndicatorLocatorIds, cw);
    }

    private static void DrawSingleGuidFromListCombo(string label, IReadOnlyList<(Guid Id, string Name)> options, List<Guid> ids, float cw)
    {
        Guid current = ids.Count > 0 ? ids[0] : Guid.Empty;
        string preview = options.FirstOrDefault(o => o.Id == current).Name ?? "(none)";
        if (cw > 0f) ImGui.PushItemWidth(cw);
        if (ImGui.BeginCombo(label, preview))
        {
            if (ImGui.Selectable("(none)", current == Guid.Empty))
                ids.Clear();
            foreach (var opt in options)
            {
                bool sel = opt.Id == current;
                if (ImGui.Selectable(opt.Name + "##sgl_" + opt.Id, sel))
                {
                    if (ids.Count == 0) ids.Add(opt.Id);
                    else ids[0] = opt.Id;
                }
            }
            ImGui.EndCombo();
        }
        if (cw > 0f) ImGui.PopItemWidth();
    }

    private void CreateFullSkateSpot(Challenge c)
    {
        if (_scene.ActiveMap is not IMap m) { _statusMessage = "Load a map first."; return; }

        PushUndoSnapshot("Create Skate spot");

        Vector3 centre = GetSpawnPositionFromViewRayOrOrigin();
        string baseName = string.IsNullOrWhiteSpace(c.Name) ? "skate" : c.Name;

        TriggerVolume boundary = new()
        {
            Name = baseName + "_boundary",
            Center = centre,
            HalfExtents = new Vector3(20f, 10f, 20f),
        };
        TriggerVolume spotVol = new()
        {
            Name = baseName + "_spotvolume",
            Center = centre,
            HalfExtents = new Vector3(3f, 2f, 3f),
        };
        TriggerVolume startVol = new()
        {
            Name = baseName + "_startvolume",
            Center = centre + new Vector3(5f, 0f, 0f),
            HalfExtents = new Vector3(1.5f, 1.5f, 1.5f),
        };
        m.TriggerVolumes.Add(boundary);
        m.TriggerVolumes.Add(spotVol);
        m.TriggerVolumes.Add(startVol);

        Locator start = new()
        {
            Name = baseName + "_start",
            Position = centre + new Vector3(5f, 0f, 0f),
            Kind = LocatorKind.ChallengeStart,
            Owner = OwnerKind.Challenge,
            OwnerChallengeId = c.Id,
        };
        Locator wait = new()
        {
            Name = baseName + "_wait",
            Position = centre + new Vector3(8f, 0f, 0f),
            Kind = LocatorKind.Sub,
            Owner = OwnerKind.Challenge,
            OwnerChallengeId = c.Id,
        };
        Locator vi = new()
        {
            Name = baseName + "_vi",
            Position = centre + new Vector3(0f, 2f, 0f),
            Kind = LocatorKind.Sub,
            Owner = OwnerKind.Challenge,
            OwnerChallengeId = c.Id,
        };
        m.Locators.Add(start);
        m.Locators.Add(wait);
        m.Locators.Add(vi);

        c.ChallengeBoundaryId = boundary.Id;
        c.SkateTurnBasedStartVolumeId = startVol.Id;
        c.SkateSpotVolumeIds.Clear();
        c.SkateSpotVolumeIds.Add(spotVol.Id);
        c.StartLocatorId = start.Id;
        c.SkateWaitLocatorId = wait.Id;
        c.SkateVisualIndicatorLocatorIds.Clear();
        c.SkateVisualIndicatorLocatorIds.Add(vi.Id);

        _statusMessage = $"Created Skate spot scaffold for '{c.Name}': 3 volumes + 3 locators wired.";
    }

    /// "<noun>s (n/max)" header used by Skate list sections.
    private static void DrawSkateCountLabel(string noun, int count, int min, int max)
    {
        Vector4 colour = count < min || count > max
            ? new Vector4(1f, 0.7f, 0.4f, 1f)   // amber: out of valid range
            : new Vector4(0.9f, 0.9f, 0.9f, 1f);
        ImGui.TextColored(colour, $"{noun}s ({count}/{max})");
        ImGui.SameLine();
        ImGui.TextDisabled($"— need {min}-{max}");
    }

    /// Help-tooltip dot appended after a Skate field. Hover for explanation.
    private static void SkateHelp(string tip)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
            ImGui.TextUnformatted(tip);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    /// Renders a reorderable/removable Guid list bound to a list of
    /// authored objects (trigger volumes or locators), with a "+ Add"
    /// button that appends the first available option. Caps growth at
    /// <paramref name="maxCount"/>.
    private void DrawSkateGuidList(
        List<Guid> ids,
        IReadOnlyList<(Guid Id, string Name)> options,
        int maxCount,
        string addLabel,
        string idPrefix)
    {
        int? moveUpIndex = null;
        int? moveDownIndex = null;
        int? removeIndex = null;

        for (int i = 0; i < ids.Count; i++)
        {
            Guid current = ids[i];
            ImGui.PushID($"{idPrefix}_{i}");

            if (ImGui.ArrowButton("up", ImGuiDir.Up)) moveUpIndex = i;
            ImGui.SameLine();
            if (ImGui.ArrowButton("down", ImGuiDir.Down)) moveDownIndex = i;
            ImGui.SameLine();
            float available = ImGui.GetContentRegionAvail().X;
            float removeButtonWidth = 30f;
            ImGui.SetNextItemWidth(available - removeButtonWidth - ImGui.GetStyle().ItemSpacing.X);
            string previewName = options.FirstOrDefault(o => o.Id == current).Name ?? "(none)";
            string preview = $"#{i + 1:D2} — {previewName}";
            if (ImGui.BeginCombo("##picker", preview))
            {
                if (ImGui.Selectable("(none)", current == Guid.Empty))
                    ids[i] = Guid.Empty;
                foreach (var opt in options)
                {
                    bool sel = opt.Id == current;
                    if (ImGui.Selectable(opt.Name + "##opt_" + opt.Id, sel))
                        ids[i] = opt.Id;
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if (ImGui.Button("X")) removeIndex = i;
            ImGui.PopID();
        }

        if (moveUpIndex is int ui && ui > 0)
            (ids[ui - 1], ids[ui]) = (ids[ui], ids[ui - 1]);
        else if (moveDownIndex is int di && di < ids.Count - 1)
            (ids[di], ids[di + 1]) = (ids[di + 1], ids[di]);
        else if (removeIndex is int ri)
            ids.RemoveAt(ri);

        if (ids.Count < maxCount && ImGui.Button(addLabel))
        {
            Guid initial = options.Count > 0 ? options[0].Id : Guid.Empty;
            ids.Add(initial);
        }
    }

    private static bool IsWorldRibbonArrowName(string name) =>
        name.StartsWith("ribbon_arrow_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("signup_vis_", StringComparison.OrdinalIgnoreCase);

    private static bool IsInChallengeRibbonName(string name) =>
        name.StartsWith("inch_ribbon_", StringComparison.OrdinalIgnoreCase);

    private static bool IsChevronLocatorName(string name) =>
        name.StartsWith("chevron_", StringComparison.OrdinalIgnoreCase);

    private void CreateWorldRibbonArrowLocator(Challenge c)
    {
        IMap? d = _scene.ActiveMap;
        if (d == null)
        {
            _statusMessage = "Load a map first.";
            return;
        }

        Locator? seed = d.Locators.FirstOrDefault(l => l.Id == c.StartLocatorId)
            ?? d.Locators.FirstOrDefault(l => l.Id == c.VisualSignupLocatorId);
        Vector3 pos = GetSpawnPositionFromViewRayOrOrigin();
        Vector3 rot = seed?.RotationDegrees ?? Vector3.Zero;

        PushUndoSnapshot("Create ribbon arrow locator");
        Locator l = new()
        {
            Name = NextIndexedName(
                d.Locators.Where(l => l.Owner == OwnerKind.Challenge && l.OwnerChallengeId == c.Id
                    && IsWorldRibbonArrowName(l.Name)).Select(l => l.Name),
                "ribbon_arrow"),
            Position = pos,
            RotationDegrees = rot,
            Kind = LocatorKind.Sub,
            Owner = OwnerKind.Challenge,
            OwnerChallengeId = c.Id,
        };
        d.Locators.Add(l);
        c.VisualSignupLocatorId = l.Id;
        _forceOpenChallengeId = c.Id;
        _forceOpenChallengeVisualsId = c.Id;
        _selected = l;
        _statusMessage = $"Added ribbon arrow locator '{l.Name}' for challenge '{c.Name}'.";
    }

    private void CreateInChallengeRibbonArrowLocator(Challenge c)
    {
        IMap? d = _scene.ActiveMap;
        if (d == null)
        {
            _statusMessage = "Load a map first.";
            return;
        }

        Locator? seed = d.Locators.FirstOrDefault(l =>
                c.InChallengeRibbonArrowLocatorIds.Count > 0
                && l.Id == c.InChallengeRibbonArrowLocatorIds[^1])
            ?? d.Locators.FirstOrDefault(l => l.Id == c.StartLocatorId)
            ?? d.Locators.FirstOrDefault(l => l.Id == c.VisualSignupLocatorId);
        Vector3 pos = GetSpawnPositionFromViewRayOrOrigin();
        Vector3 rot = seed?.RotationDegrees ?? Vector3.Zero;

        PushUndoSnapshot("Add in-challenge ribbon arrow");
        Locator l = new()
        {
            Name = NextIndexedName(
                d.Locators.Where(l => l.Owner == OwnerKind.Challenge && l.OwnerChallengeId == c.Id
                    && IsInChallengeRibbonName(l.Name)).Select(l => l.Name),
                "inch_ribbon"),
            Position = pos,
            RotationDegrees = rot,
            Kind = LocatorKind.Sub,
            Owner = OwnerKind.Challenge,
            OwnerChallengeId = c.Id,
        };
        d.Locators.Add(l);
        c.InChallengeRibbonArrowLocatorIds.Add(l.Id);
        _forceOpenChallengeId = c.Id;
        _forceOpenChallengeVisualsId = c.Id;
        _selected = l;
        _statusMessage = $"Added in-challenge ribbon '{l.Name}'.";
    }

    private void CreateChevronLocator(Challenge c)
    {
        IMap? d = _scene.ActiveMap;
        if (d == null)
        {
            _statusMessage = "Load a map first.";
            return;
        }

        Locator? seed = d.Locators.FirstOrDefault(l =>
                c.ChevronLocatorIds.Count > 0 && l.Id == c.ChevronLocatorIds[^1])
            ?? d.Locators.FirstOrDefault(l => l.Id == c.StartLocatorId)
            ?? d.Locators.FirstOrDefault(l => l.Id == c.VisualSignupLocatorId);
        Vector3 pos = GetSpawnPositionFromViewRayOrOrigin();
        Vector3 rot = seed?.RotationDegrees ?? Vector3.Zero;

        PushUndoSnapshot("Add chevron locator");
        Locator l = new()
        {
            Name = NextIndexedName(
                d.Locators.Where(l => l.Owner == OwnerKind.Challenge && l.OwnerChallengeId == c.Id
                    && IsChevronLocatorName(l.Name)).Select(l => l.Name),
                "chevron"),
            Position = pos,
            RotationDegrees = rot,
            Kind = LocatorKind.Sub,
            Owner = OwnerKind.Challenge,
            OwnerChallengeId = c.Id,
        };
        d.Locators.Add(l);
        c.ChevronLocatorIds.Add(l.Id);
        _forceOpenChallengeId = c.Id;
        _forceOpenChallengeVisualsId = c.Id;
        _selected = l;
        _statusMessage = $"Added chevron locator '{l.Name}'.";
    }

    private static void DrawObjectRefCombo(string label, IList<TriggerVolume> options, Guid? current, Action<Guid?> setter, float width = 0f)
    {
        if (width > 0f) ImGui.PushItemWidth(width);
        string preview = current is { } cid && options.FirstOrDefault(v => v.Id == cid) is { } v
            ? v.Name : "(none)";
        if (!ImGui.BeginCombo(label, preview))
        {
            if (width > 0f) ImGui.PopItemWidth();
            return;
        }
        if (ImGui.Selectable("(none)", current is null)) setter(null);
        foreach (TriggerVolume opt in options)
        {
            bool isSelected = opt.Id == current;
            if (ImGui.Selectable(opt.Name + "##ref_" + opt.Id, isSelected)) setter(opt.Id);
        }
        ImGui.EndCombo();
        if (width > 0f) ImGui.PopItemWidth();
    }

    private static void DrawLocatorRefCombo(string label, IList<Locator> options, Guid? current, Action<Guid?> setter, float width = 0f)
    {
        if (width > 0f) ImGui.PushItemWidth(width);
        string preview = current is { } cid && options.FirstOrDefault(l => l.Id == cid) is { } loc
            ? loc.Name : "(none)";
        if (!ImGui.BeginCombo(label, preview))
        {
            if (width > 0f) ImGui.PopItemWidth();
            return;
        }
        if (ImGui.Selectable("(none)", current is null)) setter(null);
        foreach (Locator opt in options)
        {
            bool isSelected = opt.Id == current;
            if (ImGui.Selectable(opt.Name + "##lref_" + opt.Id, isSelected)) setter(opt.Id);
        }
        ImGui.EndCombo();
        if (width > 0f) ImGui.PopItemWidth();
    }

    private void DrawStatusBar()
    {
        ImGui.SetNextWindowPos(new Vector2(0, _windowHeight - StatusBarHeight));
        ImGui.SetNextWindowSize(new Vector2(_windowWidth, StatusBarHeight));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.0f, 0.48f, 0.80f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 4));
        ImGui.Begin("##status",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoCollapse);
        ImGui.TextUnformatted(_statusMessage);
        ImGui.End();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    private void AddVolume()
    {
        if (_scene.ActiveMap is not IMap m) { _statusMessage = "Load a map first."; return; }
        PushUndoSnapshot("Add volume");
        TriggerVolume v = new()
        {
            Name = $"volume_{m.TriggerVolumes.Count + 1:D2}",
            Center = GetSpawnPositionFromViewRayOrOrigin(),
            HalfExtents = new Vector3(2, 2, 1),
        };
        m.TriggerVolumes.Add(v);
        _selected = v;
        _statusMessage = $"Added trigger volume '{v.Name}' to '{m.Name}'.";
    }

    private void AddLocator()
    {
        if (_scene.ActiveMap is not IMap m) { _statusMessage = "Load a map first."; return; }
        PushUndoSnapshot("Add locator");
        Locator l = new()
        {
            Name = $"locator_{m.Locators.Count + 1:D2}",
            Position = GetSpawnPositionFromViewRayOrOrigin(),
        };
        m.Locators.Add(l);
        _selected = l;
        _statusMessage = $"Added locator '{l.Name}' to '{m.Name}'.";
    }

    /// Set true to open the "Pick challenge type" modal on the next frame.
    /// The actual `ImGui.OpenPopup` call must happen INSIDE the frame's UI
    /// pass; this flag lets event handlers (menu clicks, the tree's "+"
    /// button) request the popup without driving ImGui state from outside.
    private bool _requestChallengeTypePopup;

    /// Request the challenge-type picker. Replaces the old direct
    /// `AddOtsChallenge()` call so the user picks the type up front instead
    /// of editing it via a dropdown after creation.
    private void RequestAddChallenge()
    {
        if (_scene.ActiveMap is null) { _statusMessage = "Load a map first."; return; }
        _requestChallengeTypePopup = true;
    }

    /// Draw the picker. Called once per frame from the main UI pass; opens
    /// the modal when <see cref="_requestChallengeTypePopup"/> is set.
    private void DrawChallengeTypePicker()
    {
        const string popupId = "Pick challenge type##addchallenge";
        if (_requestChallengeTypePopup)
        {
            ImGui.OpenPopup(popupId);
            _requestChallengeTypePopup = false;
        }

        // Centre against DisplaySize — Veldrid.ImGui 5.72 predates
        // GetMainViewport(). The 3-arg SetNextWindowPos with pivot
        // (0.5, 0.5) places the window centred on the supplied point.
        Vector2 center = ImGui.GetIO().DisplaySize * 0.5f;
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        // BeginPopupModal in this version has no (name, flags) overload —
        // call the 3-arg form with a stub `open` ref. Matches the pattern
        // used by the Export-DLC modal further down this file.
        bool keepOpen = true;
        if (!ImGui.BeginPopupModal(popupId, ref keepOpen, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.TextDisabled("Pick the challenge type to add:");
        ImGui.Spacing();

        // One button per ChallengeType. Order matches the enum so adding a
        // new type in EditorScene.cs auto-extends the picker via a single
        // edit to the array below.
        (ChallengeType Type, string Label, string Hint)[] options =
        {
            (ChallengeType.Ots,   "OTS",   "Own-The-Spot. Score points in a scoring volume."),
            (ChallengeType.Otl,   "OTL",   "Own-The-Lot. Multi-spot chain. (Pipeline stub.)"),
            (ChallengeType.Photo, "Photo", "Photo challenge. (Pipeline stub.)"),
            (ChallengeType.Film,  "Film",  "Film challenge. (Pipeline stub.)"),
            (ChallengeType.Race,  "Race",  "Race / Death Race. Gate trigger volumes + time limit."),
            (ChallengeType.Skate, "Skate", "Game of S.K.A.T.E. Turn-based copy-trick (online)."),
        };

        foreach (var opt in options)
        {
            if (ImGui.Button(opt.Label, new Vector2(160f, 0f)))
            {
                AddChallenge(opt.Type);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            ImGui.TextDisabled(opt.Hint);
        }

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("Cancel", new Vector2(80f, 0f)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    /// Add a challenge of the given type to the active DIST. Replaces the
    /// old `AddOtsChallenge`. Type is fixed at creation — the inspector no
    /// longer offers a type-changing combo (different types have different
    /// authoring needs, mutating type mid-edit would orphan referenced
    /// volumes / locators / gates).
    private void AddChallenge(ChallengeType type)
    {
        if (_scene.ActiveMap is not IMap m) { _statusMessage = "Load a map first."; return; }
        PushUndoSnapshot("Add challenge");
        string namePrefix = type switch
        {
            ChallengeType.Ots => "ots",
            ChallengeType.Otl => "otl",
            ChallengeType.Photo => "photo",
            ChallengeType.Film => "film",
            ChallengeType.Race => "race",
            ChallengeType.Skate => "skate",
            _ => "challenge",
        };
        Challenge c = new()
        {
            Name = $"{namePrefix}_{m.Challenges.Count + 1:D2}",
            Type = type,
        };
        m.Challenges.Add(c);
        _selected = c;
        _statusMessage = $"Added {type} challenge '{c.Name}' to '{m.Name}'.";
    }

    private void LoadDistViaPicker()
    {
        _statusMessage = "Opening folder picker...";
        string? folder;
        try
        {
            folder = FolderPicker.Pick("Pick a DIST folder containing .psf files",
                _scene.ActiveDist?.FolderPath);
        }
        catch (Exception ex)
        {
            _statusMessage = $"Folder picker error: {ex.GetType().Name}: {ex.Message}";
            return;
        }

        if (string.IsNullOrEmpty(folder))
        {
            _statusMessage = "Folder picker canceled.";
            return;
        }
        ImportDistFolder(folder);
    }

    private void ImportSk8ViaPicker()
    {
        string? initial = _scene.ActiveGlbMap?.SourcePath;
        string? path;
        try { path = Sk8FilePicker.PickOpen("Pick a .sk8 to import as a map", initial); }
        catch (Exception ex)
        {
            _statusMessage = $"Sk8 picker error: {ex.GetType().Name}: {ex.Message}";
            return;
        }
        if (string.IsNullOrEmpty(path))
        {
            _statusMessage = "Sk8 picker canceled.";
            return;
        }
        ImportSk8File(path);
    }

    /// Kick off an asynchronous .sk8 parse on the threadpool. The
    /// continuation posts back to the main-thread dispatcher to upload
    /// meshes to Veldrid and reconcile <see cref="GlbMap.Materials"/>. UI
    /// stays responsive during parse.
    private void ImportSk8File(string sk8Path)
    {
        string absolute;
        try { absolute = System.IO.Path.GetFullPath(sk8Path); }
        catch (Exception ex)
        {
            _statusMessage = $"Sk8 path invalid: {ex.GetType().Name}: {ex.Message}";
            return;
        }
        if (!System.IO.File.Exists(absolute))
        {
            _statusMessage = $"Sk8 not found: {absolute}";
            return;
        }

        string fileLabel = System.IO.Path.GetFileName(absolute);
        _statusMessage = $"Importing Sk8: {fileLabel}...";

        Task.Run(() =>
        {
            try
            {
                Sk8Importer.ParsedSk8 parsed = Sk8Importer.Parse(absolute);
                _dispatcher.Post(() => ApplyParsedSk8(absolute, parsed));
            }
            catch (Exception ex)
            {
                _dispatcher.Post(() =>
                    _statusMessage = $"Sk8 import failed ({System.IO.Path.GetFileName(absolute)}): {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    /// Main-thread continuation of <see cref="ImportSk8File"/>: creates or
    /// reuses the <see cref="GlbMap"/> (kept as the editor's internal
    /// "imported map" type — name retained for save-file compat), uploads
    /// each primitive to the GPU, reconciles per-material assignments by
    /// name, and refreshes the spline list.
    private void ApplyParsedSk8(string absolutePath, Sk8Importer.ParsedSk8 parsed)
    {
        // Reuse an existing map with the same source path so re-importing
        // doesn't duplicate the scene tree entry (mirrors DIST behavior).
        GlbMap? existing = _scene.GlbMaps.FirstOrDefault(g =>
            string.Equals(g.SourcePath, absolutePath, StringComparison.OrdinalIgnoreCase));

        GlbMap target;
        if (existing is not null)
        {
            target = existing;
            foreach (var m in target.Meshes) Rendering.Renderer3D.DisposeMesh(m);
            target.Meshes.Clear();
        }
        else
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(absolutePath);
            if (string.IsNullOrEmpty(name)) name = "sk8_map";
            target = _scene.CreateGlbMap(name, absolutePath);
        }

        _scene.ActiveMapId = target.Id;
        _selected = null;

        int meshUploadFailures = 0;
        int textureDecodeFailures = 0;
        int meshesWithTexture = 0;
        int meshesWithoutTexture = 0;
        foreach (Sk8Importer.ParsedMesh pm in parsed.Meshes)
        {
            Veldrid.Texture? dtex = null;
            Veldrid.TextureView? dview = null;
            if (pm.BaseColorImageBytes is byte[] imgBytes && imgBytes.Length > 0)
            {
                if (TryCreateSk8BaseColorTexture(imgBytes, pm.AlphaMode, pm.AlphaCutoff, out dtex, out dview))
                    meshesWithTexture++;
                else
                    textureDecodeFailures++;
            }
            else
            {
                meshesWithoutTexture++;
            }

            try
            {
                ImportedMesh uploaded = _renderer.UploadMesh(
                    pm.Name,
                    absolutePath,
                    pm.PositionsXyz,
                    pm.NormalsXyz,
                    pm.TexCoordsUv,
                    pm.Indices,
                    diffuseTexture: dtex,
                    diffuseView: dview,
                    glbMaterialName: pm.MaterialName);
                target.Meshes.Add(uploaded);
            }
            catch
            {
                // UploadMesh failed — release any texture we just made so we
                // don't leak the GPU resource.
                dview?.Dispose();
                dtex?.Dispose();
                meshUploadFailures++;
            }
        }

        ReconcileMaterials(target, parsed.MaterialNames);
        // Splines come from the Blender exporter's named-empty knot pass
        // (see sk8_blender_exporter.py). Re-import overwrites the list —
        // the .sk8 file is the source of truth for spline geometry.
        target.Splines.Clear();
        foreach (Sk8Importer.ParsedSpline ps in parsed.Splines)
            target.Splines.Add(new ImportedSpline { Name = ps.Name, PointCount = ps.PointCount });

        // Imported lighting is intentionally not supported — the viewport uses
        // a fixed built-in directional light. Any lights in the .sk8 file are
        // ignored.

        _needs3DRedraw = true; // new meshes added → re-render

        // Frame the orbit camera on the actual imported geometry — a huge
        // map otherwise looks empty (camera framed on a 15-unit sphere at
        // origin while the mesh extends thousands of units away). Compute
        // a bounding sphere over every uploaded mesh's world-space AABB
        // and pivot the camera there.
        if (target.Meshes.Count > 0)
        {
            Vector3 bMin = new(float.PositiveInfinity);
            Vector3 bMax = new(float.NegativeInfinity);
            foreach (ImportedMesh m in target.Meshes)
            {
                bMin = Vector3.Min(bMin, m.BoundsMin);
                bMax = Vector3.Max(bMax, m.BoundsMax);
            }
            Vector3 center = (bMin + bMax) * 0.5f;
            float radius = Math.Max((bMax - bMin).Length() * 0.5f, 1f);
            // Extend the camera near/far planes so the entire mesh fits in
            // the view frustum. With the default 5000-unit far plane a
            // map > 5km across (or an accidentally-not-scaled-down GLB coming
            // in at 100×) gets clipped to nothingness and the user can't
            // see where their geometry is. Scale far with the mesh radius;
            // pull near in proportional too so we don't waste depth
            // precision on close geometry.
            _camera.FarPlane = Math.Max(5000f, radius * 20f);
            _camera.NearPlane = Math.Clamp(radius * 0.001f, 0.05f, 5f);
            _camera.FrameSphere(center, radius);
        }

        string fileLabel = System.IO.Path.GetFileName(absolutePath);
        var problems = new List<string>();
        if (meshUploadFailures > 0)    problems.Add($"{meshUploadFailures} mesh upload errors");
        if (textureDecodeFailures > 0) problems.Add($"{textureDecodeFailures} base-color decode errors");
        string suffix = problems.Count > 0 ? $" ({string.Join("; ", problems)})" : "";
        _statusMessage = $"Imported Sk8 '{fileLabel}' [{parsed.Generator}]: {parsed.Meshes.Count} meshes ({meshesWithTexture} textured), {target.Materials.Count} materials, {target.Splines.Count} splines{suffix}.";
    }

    /// Decode a PNG / JPEG (or any other format ImageSharp groks) into an RGBA8
    /// Veldrid texture + view with a full mip chain. Used by the GLB import
    /// path to materialize each primitive's base-color channel onto the
    /// viewport mesh.
    /// <para>
    /// Alpha-mode semantics from the glTF Material.AlphaMode: <c>OPAQUE</c>
    /// forces alpha = 255 so the renderer's alpha-clip shader renders as if
    /// blending were off. <c>MASK</c> snaps alpha to binary 0 / 255 at the
    /// cutoff so edge anti-aliasing doesn't blend depth. <c>BLEND</c> keeps
    /// alpha intact.
    /// </para>
    /// </summary>
    private bool TryCreateSk8BaseColorTexture(
        byte[] encodedBytes,
        Sk8Importer.Sk8AlphaMode alphaMode,
        float alphaCutoff,
        out Veldrid.Texture? texture,
        out Veldrid.TextureView? view)
    {
        texture = null;
        view = null;
        try
        {
            using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(encodedBytes);
            int w = image.Width;
            int h = image.Height;
            if (w <= 0 || h <= 0) return false;

            byte[] pixels = new byte[w * h * 4];
            image.CopyPixelDataTo(pixels);

            // Alpha handling — branched by the material's declared mode.
            // The Blender exporter is conservative about what it labels as
            // non-opaque: it only writes mask/blend when the artist has
            // wired the Principled BSDF's Alpha input, so stale
            // blend_method flags on imported props don't bleed alpha bytes
            // through to the shader (which previously created visual
            // "acid holes" on car bodies / props with packed specular in
            // the alpha channel).
            //
            //   OPAQUE — force a = 255 so the shader's `discard < 0.5`
            //            test can never fire on opaque meshes regardless
            //            of what garbage is in the source PNG's alpha
            //            channel.
            //
            //   MASK   — snap alpha to 0 or 255 at the material's cutoff.
            //            Anti-aliased edge texels collapse to clean binary
            //            cutout instead of writing partial-alpha depth.
            //
            //   BLEND  — leave alpha intact (no sorted second pass for
            //            true back-to-front; rare in Skate-style worlds).
            if (alphaMode == Sk8Importer.Sk8AlphaMode.Opaque)
            {
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            }
            else if (alphaMode == Sk8Importer.Sk8AlphaMode.Mask)
            {
                byte cutoffByte = (byte)Math.Clamp((int)MathF.Round(alphaCutoff * 255f), 0, 255);
                for (int i = 3; i < pixels.Length; i += 4)
                    pixels[i] = pixels[i] >= cutoffByte ? (byte)255 : (byte)0;
            }

            // Canonical Veldrid.ImageSharp pattern: a single Sampled texture
            // with all mip levels, each level uploaded via UpdateTexture. No
            // staging copy, no per-texture WaitForIdle — Veldrid/Vulkan
            // handles the sequencing internally. This matches
            // src/Veldrid.ImageSharp/ImageSharpTexture.cs (CreateTextureViaUpdate)
            // verbatim.
            //
            // On Vulkan the SRV path correctly exposes the full mip chain,
            // unlike Veldrid 4.9's D3D11 backend where only mip 0 was
            // sampleable (the original reason we switched backends).
            int maxDim = Math.Max(w, h);
            int mipLevels = (int)MathF.Floor(MathF.Log2(maxDim)) + 1;
            ResourceFactory factory = _renderer.GraphicsDevice.ResourceFactory;
            Veldrid.Texture tex = factory.CreateTexture(TextureDescription.Texture2D(
                (uint)w, (uint)h, (uint)mipLevels, arrayLayers: 1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.Sampled));

            _renderer.GraphicsDevice.UpdateTexture(tex, pixels, 0, 0, 0, (uint)w, (uint)h, 1, 0, 0);
            byte[] currentMip = pixels;
            int currentW = w;
            int currentH = h;
            for (int level = 1; level < mipLevels; level++)
            {
                int mipW = Math.Max(1, currentW / 2);
                int mipH = Math.Max(1, currentH / 2);
                byte[] mipPixels = BoxDownsampleRgba8(currentMip, currentW, currentH, mipW, mipH);
                _renderer.GraphicsDevice.UpdateTexture(
                    tex, mipPixels, 0, 0, 0,
                    (uint)mipW, (uint)mipH, 1,
                    (uint)level, 0);
                currentMip = mipPixels;
                currentW = mipW;
                currentH = mipH;
            }

            texture = tex;
            view = factory.CreateTextureView(tex);
            return true;
        }
        catch
        {
            // Best-effort; the mesh just renders with the white fallback.
            view?.Dispose();
            texture?.Dispose();
            texture = null;
            view = null;
            return false;
        }
    }

    /// 2×2 box-filter RGBA8 downsample. Each output pixel is the average of
    /// the four source pixels it covers; for odd dimensions the rightmost /
    /// bottom column / row clamps. Used to build a mip chain on the CPU at
    /// GLB import time when a driver-side GenerateMipmaps pass either fails
    /// or doesn't actually fill the sub-levels (observed as moiré on tiled
    /// brick / asphalt textures at glancing angles).
    private static byte[] BoxDownsampleRgba8(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH * 4];
        for (int y = 0; y < dstH; y++)
        {
            int sy0 = Math.Min(y * 2,     srcH - 1);
            int sy1 = Math.Min(sy0 + 1,   srcH - 1);
            int row0 = sy0 * srcW * 4;
            int row1 = sy1 * srcW * 4;
            int dstRow = y * dstW * 4;
            for (int x = 0; x < dstW; x++)
            {
                int sx0 = Math.Min(x * 2,   srcW - 1);
                int sx1 = Math.Min(sx0 + 1, srcW - 1);
                int i00 = row0 + sx0 * 4;
                int i01 = row0 + sx1 * 4;
                int i10 = row1 + sx0 * 4;
                int i11 = row1 + sx1 * 4;
                int o   = dstRow + x * 4;
                // +2 rounds to nearest instead of truncating toward zero.
                dst[o + 0] = (byte)((src[i00 + 0] + src[i01 + 0] + src[i10 + 0] + src[i11 + 0] + 2) >> 2);
                dst[o + 1] = (byte)((src[i00 + 1] + src[i01 + 1] + src[i10 + 1] + src[i11 + 1] + 2) >> 2);
                dst[o + 2] = (byte)((src[i00 + 2] + src[i01 + 2] + src[i10 + 2] + src[i11 + 2] + 2) >> 2);
                dst[o + 3] = (byte)((src[i00 + 3] + src[i01 + 3] + src[i10 + 3] + src[i11 + 3] + 2) >> 2);
            }
        }
        return dst;
    }

    /// Match parsed material names against existing assignments by name. Keeps
    /// user edits intact across Blender re-exports, adds defaults for new
    /// materials, drops assignments whose material no longer appears.
    private static void ReconcileMaterials(GlbMap target, IReadOnlyList<string> parsedNames)
    {
        var byName = target.Materials.ToDictionary(a => a.MaterialName, StringComparer.Ordinal);
        target.Materials.Clear();
        foreach (string name in parsedNames)
        {
            if (byName.TryGetValue(name, out GlbMaterialAssignment? existing))
            {
                target.Materials.Add(existing);
                byName.Remove(name);
            }
            else
            {
                target.Materials.Add(new GlbMaterialAssignment { MaterialName = name });
            }
        }
        // Any entries left in byName correspond to removed materials — drop them.
    }

    private void ImportDistFolder(string folder)
    {
        // Reject upfront if the folder doesn't actually look like a DIST — we
        // never want a placeholder DIST entry sitting in the tree with nothing
        // behind it.
        string[] psfFiles = System.IO.Directory.EnumerateFiles(folder, "*.psf", System.IO.SearchOption.AllDirectories).ToArray();
        if (psfFiles.Length == 0)
        {
            _statusMessage = $"No .psf files under {folder} — nothing imported.";
            return;
        }

        // If a Dist with this FolderPath already exists in the scene (saved
        // from a .cescn load), reuse it — populate its empty Meshes list
        // instead of creating a duplicate "Challenge DIST" tree node.
        Dist? existing = _scene.Dists.FirstOrDefault(d =>
            !string.IsNullOrEmpty(d.FolderPath) &&
            string.Equals(d.FolderPath, folder, StringComparison.OrdinalIgnoreCase));

        Dist target;
        if (existing is not null)
        {
            target = existing;
            // Drop any previously-uploaded meshes on this Dist before re-loading
            // so toggling Load DIST doesn't double the GPU resource count.
            foreach (var m in target.Meshes) Rendering.Renderer3D.DisposeMesh(m);
            target.Meshes.Clear();
        }
        else
        {
            string distName = System.IO.Path.GetFileName(folder.TrimEnd('/', '\\'));
            if (string.IsNullOrEmpty(distName)) distName = "DIST";
            target = _scene.CreateDist(distName, folder);
        }

        _scene.ActiveDistId = target.Id;
        _selected = null;

        var stats = LoadMeshesIntoDist(target, psfFiles);
        _camera.FrameSphere(Vector3.Zero, 15f);

        string verb = existing is not null ? "Re-imported" : "Imported";
        _statusMessage = $"{verb} {stats.TotalMeshes} meshes ({stats.TotalVerts} verts, {stats.TotalTris} tris) from {stats.FilesWithMeshes}/{stats.FilesScanned} PSFs in {stats.ElapsedMs} ms" +
                         (stats.ChunkErrors > 0 ? $" ({stats.ChunkErrors} chunk errors)" : "");
        _needs3DRedraw = true;
    }

    /// Scans every PSF under the DIST folder, extracts meshes, and uploads them
    /// to the GPU as `ImportedMesh` entries on the supplied Dist. Returns
    /// stats for the caller to surface in the status bar.
    private MeshLoadStats LoadMeshesIntoDist(Dist dist, string[] psfFiles)
    {
        int totalMeshes = 0;
        long totalVerts = 0;
        long totalTris = 0;
        int filesScanned = 0;
        int filesWithMeshes = 0;
        int chunkErrors = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var texIndex = string.IsNullOrEmpty(dist.FolderPath)
            ? new Dictionary<ulong, string>()
            : Psg.PsgDistTextureIndex.Scan(dist.FolderPath);

        // Build one DIST-wide PSF texture index first. Many mesh PSFs rely on GUIDs present only
        // in cPres_Global.psf (or other PSFs without meshes), so per-file indexing causes grey meshes.
        var texturesFromDistPsf = new Dictionary<ulong, List<Psg.PsgEmbeddedTextureCatalog.TextureRef>>();
        foreach (string texPath in psfFiles)
        {
            Psg.PsfReader.File texPsf;
            try { texPsf = Psg.PsfReader.Read(texPath); }
            catch { continue; }

            foreach (var chunk in texPsf.Chunks)
            {
                byte[] raw;
                try { raw = Psg.PsfReader.DecompressChunk(chunk); }
                catch { continue; }
                if (!Psg.PsgReader.LooksLikePsg(raw)) continue;
                Psg.PsgEmbeddedTextureCatalog.RegisterChunk(texturesFromDistPsf, raw);
            }
        }

        foreach (string path in psfFiles)
        {
            filesScanned++;
            Psg.PsfReader.File psf;
            try { psf = Psg.PsfReader.Read(path); }
            catch { continue; }

            if (psf.Chunks.Count == 0) continue;

            int meshesFromThisFile = 0;
            foreach (var chunk in psf.Chunks)
            {
                byte[] psgBytes;
                try { psgBytes = Psg.PsfReader.DecompressChunk(chunk); }
                catch { chunkErrors++; continue; }

                if (!Psg.PsgReader.LooksLikePsg(psgBytes)) continue;

                Psg.PsgReader psg;
                try
                {
                    psg = new Psg.PsgReader(psgBytes);
                    psg.Parse();
                }
                catch { chunkErrors++; continue; }

                List<Psg.PsgMeshExtractor.Mesh> meshes;
                try { meshes = Psg.PsgMeshExtractor.ExtractMeshes(psg); }
                catch { chunkErrors++; continue; }

                foreach (var m in meshes)
                {
                    if (m.Indices.Length == 0 || m.Positions.Length == 0) continue;
                    string name = $"{System.IO.Path.GetFileNameWithoutExtension(path)}#{chunk.AssetId:X16}#{m.OptiMeshEntryIndex}";

                    Veldrid.Texture? dtex = null;
                    Veldrid.TextureView? dview = null;
                    bool hasGuid = Psg.PsgMaterialDiffuse.TryGetDiffuseTextureGuid(psg, m.OptiMeshDataOffset, out ulong dg);
                    if (!hasGuid)
                    {
                        // Match blender_psg_material_importer.py create_mesh fallback:
                        // when material_ptr doesn't resolve, pick material by mesh index modulo material count.
                        hasGuid = Psg.PsgMaterialDiffuse.TryGetDiffuseTextureGuidByMeshIndexFallback(psg, m.OptiMeshEntryIndex, out dg);
                    }
                    if (hasGuid)
                    {
                        if (texturesFromDistPsf.TryGetValue(dg, out List<Psg.PsgEmbeddedTextureCatalog.TextureRef>? texRefs) && texRefs.Count > 0)
                        {
                            foreach (var texRef in texRefs)
                            {
                                if (Psg.PsgTextureGpuLoader.TryCreateSampledTexture(
                                        _renderer.GraphicsDevice, texRef.PsgBytes, texRef.TextureDictIndex, out dtex, out dview))
                                {
                                    break;
                                }
                            }
                        }
                        else if (texIndex.TryGetValue(dg, out string? texPath))
                        {
                            Psg.PsgTextureGpuLoader.TryCreateSampledTexture(_renderer.GraphicsDevice, texPath, out dtex, out dview);
                        }
                    }

                    var imported = _renderer.UploadMesh(
                        name, path,
                        m.Positions, m.Normals, m.TexCoords, m.Indices,
                        dtex, dview);
                    dist.Meshes.Add(imported);
                    totalMeshes++;
                    totalVerts += m.Positions.Length / 3;
                    totalTris += m.Indices.Length / 3;
                    meshesFromThisFile++;
                }
            }
            if (meshesFromThisFile > 0) filesWithMeshes++;
        }

        watch.Stop();
        return new MeshLoadStats(totalMeshes, totalVerts, totalTris, filesScanned, filesWithMeshes, chunkErrors, watch.ElapsedMilliseconds);
    }

    private readonly record struct MeshLoadStats(
        int TotalMeshes, long TotalVerts, long TotalTris,
        int FilesScanned, int FilesWithMeshes, int ChunkErrors, long ElapsedMs);

    private void ClearImportedMeshes()
    {
        foreach (var m in _scene.Meshes) Rendering.Renderer3D.DisposeMesh(m);
        _scene.Meshes.Clear();
        _statusMessage = "Cleared imported meshes for active map.";
    }

    private string _lastBuildLog = string.Empty;
    private bool _showBuildLog;

    // ── Scene save/load ──────────────────────────────────────────────────

    /// Reset the scene to empty. Releases GPU mesh resources first so we don't
    /// leak Veldrid buffers on the dropped Dists.
    private void NewScene()
    {
        DisposeAllMeshes();
        _scene.Dists.Clear();
        _scene.GlbMaps.Clear();
        _scene.ActiveMapId = Guid.Empty;
        _scene.PackageName = "MyDLC";
        _selected = null;
        _currentScenePath = null;
        _statusMessage = "New scene.";
    }

    private void OpenSceneViaPicker()
    {
        string? path;
        try { path = SceneFilePicker.PickOpen("Open Scene", _currentScenePath); }
        catch (Exception ex) { _statusMessage = $"Picker error: {ex.GetType().Name}: {ex.Message}"; return; }
        if (string.IsNullOrEmpty(path)) { _statusMessage = "Open canceled."; return; }
        OpenSceneFromPath(path);
    }

    /// Load a scene from an explicit path (used by file-association startup and
    /// by the Open Scene picker).
    public void OpenSceneFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _statusMessage = "Open failed: scene path is empty.";
            return;
        }

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex)
        {
            _statusMessage = $"Open failed: invalid scene path ({ex.GetType().Name}: {ex.Message}).";
            return;
        }
        if (!File.Exists(fullPath))
        {
            _statusMessage = $"Open failed: scene file not found: {fullPath}";
            return;
        }

        try
        {
            // Dispose GPU mesh resources before clearing — SceneSerializer.Load
            // replaces _scene.Dists wholesale and the GPU buffers on outgoing
            // meshes need explicit cleanup.
            System.IO.File.AppendAllText(@"C:\Users\Ethans Desktop 2.0\Desktop\editor_open_scene.log", $"\n[{DateTime.Now:HH:mm:ss.fff}] fullPath={fullPath}");
            DisposeAllMeshes();
            System.IO.File.AppendAllText(@"C:\Users\Ethans Desktop 2.0\Desktop\editor_open_scene.log", $"\n[{DateTime.Now:HH:mm:ss.fff}] disposed");
            SceneSerializer.Load(_scene, fullPath);
            System.IO.File.AppendAllText(@"C:\Users\Ethans Desktop 2.0\Desktop\editor_open_scene.log", $"\n[{DateTime.Now:HH:mm:ss.fff}] Load done; Dists={_scene.Dists.Count} GlbMaps={_scene.GlbMaps.Count}");
            SceneSerializer.ResolveDistFolderPaths(_scene, fullPath);
            _currentScenePath = fullPath;
            _selected = null;
        }
        catch (Exception ex)
        {
            _statusMessage = $"Open failed: {ex.GetType().Name}: {ex.Message}";
            return;
        }

        // Re-import meshes for every loaded Dist whose FolderPath still
        // resolves on disk. This populates the existing Dist objects in place
        // so the user keeps all their authored locators / volumes / challenges
        // (which already round-tripped through the JSON) — no new "Challenge
        // DIST" tree nodes get created.
        int distsWithMeshes = 0;
        int distsMissing = 0;
        int totalMeshes = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var dist in _scene.Dists)
        {
            if (string.IsNullOrEmpty(dist.FolderPath)) continue;
            if (!System.IO.Directory.Exists(dist.FolderPath))
            {
                distsMissing++;
                continue;
            }

            string[] psfFiles = System.IO.Directory.EnumerateFiles(
                dist.FolderPath, "*.psf", System.IO.SearchOption.AllDirectories).ToArray();
            if (psfFiles.Length == 0) continue;

            // Drop any meshes already attached (defensive — shouldn't happen
            // post-load, but the helper rebuilds GPU buffers and would leak
            // otherwise).
            foreach (var m in dist.Meshes) Rendering.Renderer3D.DisposeMesh(m);
            dist.Meshes.Clear();

            var stats = LoadMeshesIntoDist(dist, psfFiles);
            totalMeshes += stats.TotalMeshes;
            distsWithMeshes++;
        }
        watch.Stop();

        // Kick off GLB re-imports in the background — they post mesh upload
        // back through the dispatcher so the open call doesn't block on
        // potentially large GLB parses. Missing source files show up as a
        // status message and the GlbMap stays in the scene with empty Meshes.
        int glbDispatched = 0;
        int glbMissing = 0;
        foreach (GlbMap glb in _scene.GlbMaps)
        {
            if (string.IsNullOrEmpty(glb.SourcePath) || !File.Exists(glb.SourcePath))
            {
                glbMissing++;
                continue;
            }
            ImportSk8File(glb.SourcePath);
            glbDispatched++;
        }

        _camera.FrameSphere(Vector3.Zero, 15f);

        string fileLabel = Path.GetFileName(fullPath);
        string distPart = distsMissing > 0
            ? $"re-imported {totalMeshes} meshes across {distsWithMeshes} DIST(s) in {watch.ElapsedMilliseconds} ms; {distsMissing} DIST folder(s) missing"
            : $"re-imported {totalMeshes} meshes across {distsWithMeshes} DIST(s) in {watch.ElapsedMilliseconds} ms";
        string glbPart = (glbDispatched + glbMissing) > 0
            ? $"; {glbDispatched} GLB(s) re-importing in background" + (glbMissing > 0 ? $", {glbMissing} GLB source file(s) missing" : "")
            : "";
        _statusMessage = $"Opened {fileLabel}: {distPart}{glbPart}.";
        _needs3DRedraw = true;
        System.IO.File.AppendAllText(@"C:\Users\Ethans Desktop 2.0\Desktop\editor_open_scene.log", $"\n[{DateTime.Now:HH:mm:ss.fff}] DONE status={_statusMessage}");
    }

    /// Save to current path if known and `saveAs=false`; otherwise prompt.
    private void SaveScene(bool saveAs)
    {
        string? path = (_currentScenePath is not null && !saveAs)
            ? _currentScenePath
            : null;

        if (path is null)
        {
            try
            {
                path = SceneFilePicker.PickSave("Save Scene", _currentScenePath);
            }
            catch (Exception ex) { _statusMessage = $"Picker error: {ex.GetType().Name}: {ex.Message}"; return; }
            if (string.IsNullOrEmpty(path)) { _statusMessage = "Save canceled."; return; }
        }

        try
        {
            SceneSerializer.Save(_scene, path);
            _currentScenePath = path;
            _statusMessage = $"Saved scene: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _statusMessage = $"Save failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// Release GPU mesh buffers across all maps. Called before any operation
    /// that drops the scene (New/Open) so we don't leak Veldrid resources.
    private void DisposeAllMeshes()
    {
        foreach (var d in _scene.Dists)
        {
            foreach (var m in d.Meshes)
                Rendering.Renderer3D.DisposeMesh(m);
            d.Meshes.Clear();
        }
        foreach (var g in _scene.GlbMaps)
        {
            foreach (var m in g.Meshes)
                Rendering.Renderer3D.DisposeMesh(m);
            g.Meshes.Clear();
        }
    }

    // Export name prompt state. ImGui modals are stateful: BeginExportDlcFlow()
    // sets _exportPromptOpenRequested so DrawExportNamePrompt() can call
    // OpenPopup once on the next frame, then BeginPopupModal stays open until
    // the user clicks Export or Cancel.
    private bool _exportPromptOpenRequested;
    private string _exportPromptName = "";

    /// Trigger the Export DLC flow. Pre-fills the name from the scene's last-
    /// used value (default "MyDLC") and opens the modal that asks the user to
    /// confirm or change it before picking an output folder.
    private void BeginExportDlcFlow()
    {
        _exportPromptName = string.IsNullOrWhiteSpace(_scene.PackageName)
            ? "MyDLC"
            : _scene.PackageName;
        _exportPromptOpenRequested = true;
    }

    /// Called from DrawInspector every frame. Renders the export-name modal
    /// when triggered. Splitting the popup-open call from BeginPopupModal is
    /// the standard ImGui pattern: OpenPopup can only be called once per
    /// trigger or the modal flickers; BeginPopupModal must run every frame
    /// while the modal is visible.
    private void DrawExportNamePrompt()
    {
        if (_exportPromptOpenRequested)
        {
            ImGui.OpenPopup("Export DLC");
            _exportPromptOpenRequested = false;
        }

        // Center the popup against the current display. Veldrid.ImGui 5.72
        // predates GetMainViewport(), so DisplaySize/2 is the portable form.
        Vector2 center = ImGui.GetIO().DisplaySize * 0.5f;
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        // BeginPopupModal in this Veldrid.ImGui version has no (name, flags)
        // overload — call the 3-arg form with a stub `open` ref so we can
        // pass AlwaysAutoResize.
        bool _open = true;
        if (!ImGui.BeginPopupModal("Export DLC", ref _open, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.Text("Package name");
        ImGui.SetNextItemWidth(280);
        // Local-copy pattern matches the rest of the inspector's InputText
        // usages — the older Veldrid.ImGui's overload resolver picks a
        // wrong overload when a `ref string` field is passed directly.
        string nameBuf = _exportPromptName;
        if (ImGui.InputText("##exportname", ref nameBuf, 64))
            _exportPromptName = nameBuf;

        // Live validation echo so the user knows what slug + folder will be
        // produced before they hit Export (same rules as DlcSpec.ToSlug + packer).
        string trimmed = _exportPromptName.Trim();
        string manifestSlug = DlcSpec.ToSlug(trimmed);
        if (string.IsNullOrEmpty(manifestSlug))
        {
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.55f, 1f),
                "Name needs at least one letter or digit.");
        }
        else
        {
            string clamped = manifestSlug.Length <= 4 ? manifestSlug : manifestSlug[..4];
            string folder = BigFilePacker.PackageSlugToDlcFolderName(manifestSlug);
            string bigName = BigFilePacker.PackageSlugToFinalBigEdatFileName(manifestSlug);
            ImGui.TextDisabled($"Engine slug: dlc_{clamped}");
            ImGui.TextDisabled($"Output folder: {folder}/{bigName}");
            if (manifestSlug.Length > 4)
                ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f),
                    $"Note: slug clamped to 4 chars ('{clamped}') for engine compatibility.");
        }

        ImGui.Separator();
        // Veldrid.ImGui 5.72 has no BeginDisabled/EndDisabled — manually
        // suppress the Export action when the slug derives empty by
        // dimming the button label and refusing the click.
        bool canExport = !string.IsNullOrEmpty(manifestSlug);
        if (!canExport)
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.3f, 0.3f, 0.3f, 1f));
        if (ImGui.Button("Export...", new Vector2(120, 0)) && canExport)
        {
            _scene.PackageName = trimmed;   // remember for next time
            if (!canExport) ImGui.PopStyleColor();
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            ExportDlcViaPicker(trimmed);
            return;
        }
        if (!canExport) ImGui.PopStyleColor();
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)))
        {
            _statusMessage = "DLC export canceled.";
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void ExportDlcViaPicker(string packageName)
    {
        string? outDir;
        try { outDir = FolderPicker.Pick("Pick an output folder for the DLC build", null); }
        catch (Exception ex) { _statusMessage = $"Picker error: {ex.GetType().Name}: {ex.Message}"; return; }
        if (string.IsNullOrEmpty(outDir)) { _statusMessage = "DLC export canceled."; return; }

        var pkg = SceneToPackageInput.Convert(_scene, packageNameOverride: packageName);
        _statusMessage = $"Building DLC '{pkg.PackageName}' ({pkg.Maps.Count} map{(pkg.Maps.Count == 1 ? "" : "s")})...";

        // Always run the full pack pipeline: stage under <outDir>/data, pack
        // every OTS cSim_Global into cSim_Global.psf, then wrap the whole
        // staging tree into <outDir>/<DlcFolder>/custom_<slug>.big.edat
        // that's ready to drop into RPCS3's USRDIR.
        DlcBuilder.IDlcBuilder builder = DlcBuilder.DlcBuilders.CreateDefault();
        DlcBuilder.Outputs.BuildResult result;
        try { result = builder.Build(pkg, outDir, DlcBuilder.BuildOptions.FullPack); }
        catch (Exception ex)
        {
            _statusMessage = $"Builder threw: {ex.GetType().Name}: {ex.Message}";
            _lastBuildLog = ex.ToString();
            _showBuildLog = true;
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Status:    {result.Status}");
        sb.AppendLine($"Output:    {result.OutputDirectory}");
        sb.AppendLine($"Elapsed:   {result.Elapsed.TotalMilliseconds:F1} ms");
        sb.AppendLine($"Files written ({result.WrittenFiles.Count}):");
        foreach (var f in result.WrittenFiles) sb.Append("  ").AppendLine(f);
        sb.AppendLine();
        sb.AppendLine($"Diagnostics ({result.Diagnostics.Count}):");
        foreach (var d in result.Diagnostics) sb.Append("  [").Append(d.Level).Append("] ").Append(d.Source).Append(": ").AppendLine(d.Message);
        _lastBuildLog = sb.ToString();
        _showBuildLog = result.Status != DlcBuilder.Outputs.BuildStatus.Succeeded;

        // Pull the .big.edat path out of the written-file list so the success
        // toast points at the artifact the user actually cares about.
        string? bigEdat = result.WrittenFiles
            .FirstOrDefault(p => p.EndsWith(".big.edat", StringComparison.OrdinalIgnoreCase));

        _statusMessage = result.Status switch
        {
            DlcBuilder.Outputs.BuildStatus.Succeeded =>
                bigEdat is not null
                    ? $"DLC packed → {bigEdat}"
                    : $"DLC build OK → {outDir}",
            DlcBuilder.Outputs.BuildStatus.SucceededWithWarnings =>
                bigEdat is not null
                    ? $"DLC packed (with warnings) → {bigEdat}  (File ▸ Show Last Build Log)"
                    : $"DLC build OK with warnings → {outDir}  (File ▸ Show Last Build Log)",
            _ => "DLC build failed. See File ▸ Show Last Build Log for what to fix.",
        };
    }

    private void DrawBuildLogWindow()
    {
        if (!_showBuildLog) return;
        ImGui.SetNextWindowSize(new Vector2(720, 480), ImGuiCond.FirstUseEver);
        bool open = _showBuildLog;
        if (ImGui.Begin("DLC Build Log", ref open))
        {
            if (ImGui.SmallButton("Copy")) ImGui.SetClipboardText(_lastBuildLog);
            ImGui.SameLine();
            if (ImGui.SmallButton("Close")) open = false;
            ImGui.Separator();
            ImGui.InputTextMultiline("##buildlog", ref _lastBuildLog,
                (uint)Math.Max(_lastBuildLog.Length + 1, 4096),
                new Vector2(-1, -1), ImGuiInputTextFlags.ReadOnly);
        }
        ImGui.End();
        _showBuildLog = open;
    }

    private void PushUndoSnapshot(string reason)
    {
        _undoStates.Add(CaptureUndoState(reason));
        if (_undoStates.Count > MaxUndoStates)
            _undoStates.RemoveAt(0);
        // Every mutation path that needs an undo entry also changes the
        // visible scene, so the 3D viewport must redraw next frame.
        _needs3DRedraw = true;
    }

    private void PerformUndo()
    {
        if (_undoStates.Count == 0)
        {
            _statusMessage = "Nothing to undo.";
            return;
        }

        UndoState state = _undoStates[^1];
        _undoStates.RemoveAt(_undoStates.Count - 1);
        RestoreUndoState(state);
        _statusMessage = $"Undo: {state.Reason}";
    }

    private UndoState CaptureUndoState(string reason)
    {
        string? selectedType = null;
        Guid? selectedId = null;
        switch (_selected)
        {
            case TriggerVolume v: selectedType = "volume"; selectedId = v.Id; break;
            case Locator l: selectedType = "locator"; selectedId = l.Id; break;
            case Challenge c: selectedType = "challenge"; selectedId = c.Id; break;
            case GlbMaterialAssignment ma: selectedType = "glbmat"; selectedId = ma.Id; break;
        }

        var distStates = _scene.Dists.Select(d => new UndoDistState(
            d.Id,
            d.Name,
            d.FolderPath,
            d.TriggerVolumes.Select(CloneTriggerVolume).ToList(),
            d.Locators.Select(CloneLocator).ToList(),
            d.Challenges.Select(CloneChallenge).ToList(),
            d.Meshes.ToList())).ToList();

        var glbStates = _scene.GlbMaps.Select(g => new UndoGlbMapState(
            g.Id,
            g.Name,
            g.SourcePath,
            g.TriggerVolumes.Select(CloneTriggerVolume).ToList(),
            g.Locators.Select(CloneLocator).ToList(),
            g.Challenges.Select(CloneChallenge).ToList(),
            g.Materials.Select(CloneMaterial).ToList(),
            g.Meshes.ToList())).ToList();

        return new UndoState(reason, distStates, glbStates, _scene.ActiveMapId, selectedType, selectedId);
    }

    private void RestoreUndoState(UndoState state)
    {
        _scene.Dists.Clear();
        foreach (UndoDistState ds in state.Dists)
        {
            var d = new Dist
            {
                Id = ds.Id,
                Name = ds.Name,
                FolderPath = ds.FolderPath,
            };
            d.TriggerVolumes.AddRange(ds.TriggerVolumes.Select(CloneTriggerVolume));
            d.Locators.AddRange(ds.Locators.Select(CloneLocator));
            d.Challenges.AddRange(ds.Challenges.Select(CloneChallenge));
            d.Meshes.AddRange(ds.Meshes);
            _scene.Dists.Add(d);
        }

        _scene.GlbMaps.Clear();
        foreach (UndoGlbMapState gs in state.GlbMaps)
        {
            var g = new GlbMap
            {
                Id = gs.Id,
                Name = gs.Name,
                SourcePath = gs.SourcePath,
            };
            g.TriggerVolumes.AddRange(gs.TriggerVolumes.Select(CloneTriggerVolume));
            g.Locators.AddRange(gs.Locators.Select(CloneLocator));
            g.Challenges.AddRange(gs.Challenges.Select(CloneChallenge));
            g.Materials.AddRange(gs.Materials.Select(CloneMaterial));
            g.Meshes.AddRange(gs.Meshes);
            _scene.GlbMaps.Add(g);
        }

        _scene.ActiveMapId = state.ActiveMapId;
        _selected = ResolveSelection(state.SelectedType, state.SelectedId);
        _activeGizmoAxis = GizmoAxis.None;
        _gizmoUndoCaptured = false;
        _needs3DRedraw = true; // undo replaced scene state → re-render
    }

    private object? ResolveSelection(string? selectedType, Guid? selectedId)
    {
        if (selectedType is null || selectedId is null) return null;
        foreach (Dist d in _scene.Dists)
        {
            switch (selectedType)
            {
                case "volume":
                    if (d.TriggerVolumes.FirstOrDefault(v => v.Id == selectedId) is { } v) return v;
                    break;
                case "locator":
                    if (d.Locators.FirstOrDefault(l => l.Id == selectedId) is { } l) return l;
                    break;
                case "challenge":
                    if (d.Challenges.FirstOrDefault(c => c.Id == selectedId) is { } c) return c;
                    break;
            }
        }
        foreach (GlbMap g in _scene.GlbMaps)
        {
            switch (selectedType)
            {
                case "volume":
                    if (g.TriggerVolumes.FirstOrDefault(v => v.Id == selectedId) is { } v) return v;
                    break;
                case "locator":
                    if (g.Locators.FirstOrDefault(l => l.Id == selectedId) is { } l) return l;
                    break;
                case "challenge":
                    if (g.Challenges.FirstOrDefault(c => c.Id == selectedId) is { } c) return c;
                    break;
                case "glbmat":
                    if (g.Materials.FirstOrDefault(ma => ma.Id == selectedId) is { } ma) return ma;
                    break;
            }
        }
        return null;
    }

    private static TriggerVolume CloneTriggerVolume(TriggerVolume v) => new()
    {
        Id = v.Id,
        Name = v.Name,
        Center = v.Center,
        HalfExtents = v.HalfExtents,
        RotationDegrees = v.RotationDegrees,
        Owner = v.Owner,
        OwnerChallengeId = v.OwnerChallengeId,
    };

    private static Locator CloneLocator(Locator l) => new()
    {
        Id = l.Id,
        Name = l.Name,
        Position = l.Position,
        RotationDegrees = l.RotationDegrees,
        Kind = l.Kind,
        Owner = l.Owner,
        OwnerChallengeId = l.OwnerChallengeId,
        Category = l.Category,
    };

    private static Challenge CloneChallenge(Challenge c)
    {
        var n = new Challenge
        {
            Id = c.Id,
            Name = c.Name,
            Type = c.Type,
            StartLocatorId = c.StartLocatorId,
            ScoringVolumeId = c.ScoringVolumeId,
            DiscoveryBoundaryId = c.DiscoveryBoundaryId,
            ChallengeBoundaryId = c.ChallengeBoundaryId,
            VisualSignupLocatorId = c.VisualSignupLocatorId,
            OwnedPoints = c.OwnedPoints,
            KilledItPoints = c.KilledItPoints,
            OnlineBonusXp = c.OnlineBonusXp,
        };
        n.InChallengeRibbonArrowLocatorIds.AddRange(c.InChallengeRibbonArrowLocatorIds);
        n.ChevronLocatorIds.AddRange(c.ChevronLocatorIds);
        return n;
    }

    private static GlbMaterialAssignment CloneMaterial(GlbMaterialAssignment a) => new()
    {
        Id = a.Id,
        MaterialName = a.MaterialName,
        Physics = a.Physics,
        Audio = a.Audio,
        Pattern = a.Pattern,
        MaterialClass = a.MaterialClass,
        ExcludeCollision = a.ExcludeCollision,
        ExcludePres = a.ExcludePres,
    };

    private sealed record UndoState(
        string Reason,
        List<UndoDistState> Dists,
        List<UndoGlbMapState> GlbMaps,
        Guid ActiveMapId,
        string? SelectedType,
        Guid? SelectedId);

    private sealed record UndoDistState(
        Guid Id,
        string Name,
        string? FolderPath,
        List<TriggerVolume> TriggerVolumes,
        List<Locator> Locators,
        List<Challenge> Challenges,
        List<ImportedMesh> Meshes);

    private sealed record UndoGlbMapState(
        Guid Id,
        string Name,
        string SourcePath,
        List<TriggerVolume> TriggerVolumes,
        List<Locator> Locators,
        List<Challenge> Challenges,
        List<GlbMaterialAssignment> Materials,
        List<ImportedMesh> Meshes);

    private void RemoveActiveDist()
    {
        Dist? d = _scene.ActiveDist;
        if (d == null)
        {
            _statusMessage = "No active DIST to remove.";
            return;
        }
        // Free GPU buffers for any imported meshes belonging to this DIST.
        foreach (var m in d.Meshes) Rendering.Renderer3D.DisposeMesh(m);
        string name = d.Name;
        _scene.RemoveDist(d.Id);
        _selected = null;
        _statusMessage = $"Removed DIST '{name}'.";
    }

    private void RemoveActiveGlbMap()
    {
        GlbMap? g = _scene.ActiveGlbMap;
        if (g == null)
        {
            _statusMessage = "No active GLB map to remove.";
            return;
        }
        foreach (var m in g.Meshes) Rendering.Renderer3D.DisposeMesh(m);
        string name = g.Name;
        _scene.RemoveMap(g.Id);
        _selected = null;
        _statusMessage = $"Removed GLB map '{name}'.";
    }

    /// "- Map" toolbar action. Dispatches by whichever kind is active so the
    /// button works regardless of map source.
    private void RemoveActiveMap()
    {
        switch (_scene.ActiveMap)
        {
            case Dist:   RemoveActiveDist(); break;
            case GlbMap: RemoveActiveGlbMap(); break;
            default:     _statusMessage = "No active map to remove."; break;
        }
    }

    /// Frames the orbit camera on the current selection. If nothing's selected,
    /// frames all imported meshes; if there are no meshes, frames the editor's
    /// scene objects; otherwise resets to the origin. Sets both the orbit pivot
    /// (Target) and Distance so the framed object fills a comfortable portion
    /// of the viewport.
    private void FrameSelectedOrAll()
    {
        Vector3 center;
        float radius;

        switch (_selected)
        {
            case TriggerVolume v:
                center = v.Center;
                radius = MathF.Max(v.HalfExtents.Length(), 1f);
                _statusMessage = $"Framed '{v.Name}'.";
                break;
            case Locator l:
                center = l.Position;
                radius = 1.5f;
                _statusMessage = $"Framed '{l.Name}'.";
                break;
            default:
                if (_scene.Meshes.Count > 0)
                {
                    var bMin = new Vector3(float.PositiveInfinity);
                    var bMax = new Vector3(float.NegativeInfinity);
                    foreach (var m in _scene.Meshes)
                    {
                        bMin = Vector3.Min(bMin, m.BoundsMin);
                        bMax = Vector3.Max(bMax, m.BoundsMax);
                    }
                    center = (bMin + bMax) * 0.5f;
                    radius = MathF.Max((bMax - bMin).Length() * 0.5f, 1f);
                    _statusMessage = $"Framed all {_scene.Meshes.Count} imported meshes.";
                }
                else if (_scene.TriggerVolumes.Count > 0 || _scene.Locators.Count > 0)
                {
                    var bMin = new Vector3(float.PositiveInfinity);
                    var bMax = new Vector3(float.NegativeInfinity);
                    foreach (var v in _scene.TriggerVolumes) { bMin = Vector3.Min(bMin, v.Center - v.HalfExtents); bMax = Vector3.Max(bMax, v.Center + v.HalfExtents); }
                    foreach (var l in _scene.Locators)        { bMin = Vector3.Min(bMin, l.Position - Vector3.One); bMax = Vector3.Max(bMax, l.Position + Vector3.One); }
                    center = (bMin + bMax) * 0.5f;
                    radius = MathF.Max((bMax - bMin).Length() * 0.5f, 1f);
                    _statusMessage = "Framed scene.";
                }
                else
                {
                    center = Vector3.Zero;
                    radius = 15f;
                    _statusMessage = "Nothing to frame — reset to origin.";
                }
                break;
        }

        _camera.FrameSphere(center, radius);
    }

    private void DrawKeybindsWindow(InputSnapshot input)
    {
        if (!_showKeybindsWindow)
            return;

        ImGui.SetNextWindowSize(new Vector2(560, 480), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Keybinds", ref _showKeybindsWindow))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped(
            "Click “Set…”, then press a key or a mouse button (left / right / middle). Escape cancels capture. " +
            "Shift still boosts camera speed. File shortcuts use Ctrl plus your chosen key. " +
            "If “toggle fly” is bound to the right mouse button, orbit and fly look use middle drag instead, and pan uses Shift+middle. " +
            "If it is bound to middle, look uses right drag and pan uses Shift+right.");
        ImGui.Separator();
        if (ImGui.Button("Reset to defaults"))
        {
            _binds = EditorKeybinds.CreateDefaults();
            try { _binds.WriteToDisk(); _statusMessage = "Keybinds reset to defaults."; }
            catch (Exception ex) { _statusMessage = $"Reset OK (save failed): {ex.Message}"; }
        }
        ImGui.SameLine();
        if (ImGui.Button("Open keybinds folder"))
        {
            try
            {
                string dir = Path.GetDirectoryName(EditorKeybinds.DefaultConfigPath())!;
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch { /* ignore */ }
        }

        if (_keybindCaptureProperty != null)
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f),
                $"Waiting for input: {EditorKeybinds.LabelForProperty(_keybindCaptureProperty)}  (Esc = cancel)");
        }

        ImGui.Separator();
        ImGui.BeginChild("##keybind_scroll", new Vector2(0, -8), ImGuiChildFlags.Borders);

        foreach (string prop in EditorKeybinds.AllPropertyNames)
        {
            ImGui.PushID(prop);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(EditorKeybinds.LabelForProperty(prop));
            ImGui.SameLine(320);
            ImGui.TextUnformatted(_binds.GetBinding(prop).DisplayLabel());
            ImGui.SameLine(460);
            if (ImGui.SmallButton("Set…"))
                _keybindCaptureProperty = prop;
            ImGui.PopID();
        }

        ImGui.EndChild();
        ImGui.End();
    }
}
