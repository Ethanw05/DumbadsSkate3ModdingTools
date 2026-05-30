using System.Diagnostics;
using System.Numerics;
using System.Windows.Forms;
using ChallengeEditor.Rendering;
using ImGuiNET;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;

namespace ChallengeEditor;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--psf-probe")
        {
            AllocConsole();
            Environment.Exit(Psg.PsfReaderProbe.Run(args));
            return;
        }

        // Published single-file + Veldrid/SDL often fail before any window exists;
        // WinExe hides console exception text — surface errors in a dialog + log.
        try
        {
            RunEditor(args);
        }
        catch (Exception ex)
        {
            string detail = ex.ToString();
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ChallengeEditor");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "startup_error.txt"), detail);
            }
            catch
            {
                // ignore log failures
            }

            try
            {
                MessageBox.Show(
                    detail,
                    "Challenge Editor — failed to start",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // last resort: no UI available
            }

            Environment.Exit(1);
        }
    }

    private static void RunEditor(string[] args)
    {
        // Start in a normal decorated window at a safe on-screen position.
        // Some systems intermittently restore maximized SDL windows with the
        // title bar pushed outside the top-left work area, making the window
        // hard to drag back. Starting in normal state avoids that edge case.
        WindowCreateInfo windowCi = new()
        {
            X = 120, Y = 80,
            WindowWidth = 1480, WindowHeight = 900,
            WindowTitle = "Challenge Editor",
            WindowInitialState = WindowState.Normal,
        };
        Sdl2Window window = VeldridStartup.CreateWindow(ref windowCi);
        // Parent file/folder pickers under the editor HWND so their COM/WinForms pumps
        // nest under our pump instead of racing it. Without this, certain selection
        // patterns freeze the editor (FolderPicker selecting from INSIDE a folder;
        // SceneFilePicker re-open going black).
        FolderPicker.OwnerHwnd = window.Handle;
        SceneFilePicker.OwnerHwnd = window.Handle;
        Sk8FilePicker.OwnerHwnd = window.Handle;

        GraphicsDeviceOptions gdOptions = new(
            debug: false,
            swapchainDepthFormat: PixelFormat.R32_Float,
            syncToVerticalBlank: true,
            resourceBindingModel: ResourceBindingModel.Improved,
            preferDepthRangeZeroToOne: true,
            preferStandardClipSpaceYDirection: true);
        // Switched from Direct3D11 → Vulkan. Veldrid 4.9's D3D11 backend has
        // an SRV / mip-sampling issue on this machine (every upload + filter
        // permutation samples only mip 0 — verified with textureLod, staging
        // copy, driver GenerateMipmaps, all of them). Vulkan uses an entirely
        // separate codepath with its own SRV/image-view setup, which avoids
        // the bug. The shaders are already Vulkan-flavored GLSL with
        // explicit set/binding decorations, so no shader changes are needed.
        GraphicsDevice gd = VeldridStartup.CreateGraphicsDevice(window, gdOptions, GraphicsBackend.Vulkan);

        ImGuiRenderer imguiRenderer = new(
            gd,
            gd.MainSwapchain.Framebuffer.OutputDescription,
            window.Width, window.Height,
            ColorSpaceHandling.Linear);

        ImGuiIOPtr io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        // Docking gives us dockable, tabbed, drag-to-rearrange panels (Maya /
        // Blender / Visual Studio style). The cimgui native shipped with
        // ImGui.NET 1.91.6.1 is built from the docking branch so the flag is
        // honored at runtime. The dockspace itself is set up each frame in
        // SetupDockspace() — without it, dockable windows just float free.
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        // Persist dock layout across sessions next to the editor exe.
        ApplyAuroraTheme();

        Renderer3D renderer3D = new(gd);
        OrbitCamera camera = new();

        window.Resized += () =>
        {
            gd.MainSwapchain.Resize((uint)window.Width, (uint)window.Height);
            imguiRenderer.WindowResized(window.Width, window.Height);
        };

        CommandList cl = gd.ResourceFactory.CreateCommandList();
        EditorScene scene = new();
        EditorUi ui = new(scene, renderer3D, camera, imguiRenderer);

        // File-association launch: `ChallengeEditor.exe "<path>.cescn"`.
        // Use the first non-switch argument as a candidate scene path.
        string? startupScenePath = args.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith("--", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(startupScenePath)
            && Path.GetExtension(startupScenePath).Equals(".cescn", StringComparison.OrdinalIgnoreCase)
            && File.Exists(startupScenePath))
        {
            ui.OpenSceneFromPath(startupScenePath);
        }

        Stopwatch clock = Stopwatch.StartNew();
        TimeSpan lastTick = clock.Elapsed;
        bool lastFrame3DRendered = true; // first frame must render

        // Frame-rate cap. Targets come from EditorUi's RenderRateMode setting
        // (View ▸ Performance Mode / High FPS Mode). Performance keeps idle
        // GPU near zero; High FPS gives smoother interaction at the cost of
        // more redraws.

        while (window.Exists)
        {
            TimeSpan frameStart = clock.Elapsed;

            InputSnapshot input = window.PumpEvents();
            if (!window.Exists) break;

            // When minimized the swapchain has nothing to present to, so VSync
            // stops throttling and the loop spins at whatever rate PumpEvents
            // returns at — observed as ~95% GPU. Sleep a frame, skip all
            // rendering work, and pick back up when the user restores. Input
            // pumping still happens so the window-restore message gets through.
            if (window.WindowState == WindowState.Minimized)
            {
                System.Threading.Thread.Sleep(16);
                lastTick = clock.Elapsed;
                continue;
            }

            TimeSpan now = clock.Elapsed;
            float dt = (float)(now - lastTick).TotalSeconds;
            lastTick = now;

            // Run editor hotkeys before ImGui's Update: Veldrid.ImGui consumes KeyEvents from the
            // snapshot when feeding ImGui, so key-bound actions (fly toggle, tools, etc.) would not fire.
            ui.ProcessInputBeforeImGui(input);
            imguiRenderer.Update(dt, input);
            // Set up the host dockspace BEFORE drawing panels — every panel
            // that calls ImGui.Begin() will dock into this space (or float
            // free, user's choice). Must come after Update() and before any
            // panel calls Begin(). The menu bar is reserved at top so we
            // offset the dockspace work area to skip the menu bar height.
            SetupDockspace(window);
            ui.Draw(input, dt);

            cl.Begin();

            // Render the 3D scene to the off-screen framebuffer ONLY when
            // something changed. Veldrid framebuffer contents persist across
            // frames; if we skip the render pass the offscreen FB keeps its
            // previous image and ImGui samples that instead. Idle GPU drops
            // close to zero because the heavy mesh pass + draw-call flood
            // stops happening on idle frames. ImGui itself still re-renders
            // every frame so UI hover/click state stays responsive.
            bool didRender3D = ui.Needs3DRedraw;
            if (didRender3D)
            {
                renderer3D.Render(cl, camera, scene, ui.Selected, ui.Tool, ui.HoveredGizmoAxis, ui.ActiveGizmoAxis, ui.ActiveGizmoOrientation, ui.SelectedLocatorGizmoAccent);
                ui.Consume3DRedraw();
            }
            else
            {
                ui.Consume3DSkip();
            }

            // …then the swapchain target with ImGui (which samples the off-screen color target).
            cl.SetFramebuffer(gd.MainSwapchain.Framebuffer);
            cl.ClearColorTarget(0, new RgbaFloat(0.10f, 0.10f, 0.11f, 1.0f));
            cl.ClearDepthStencil(1f);
            imguiRenderer.Render(gd, cl);
            cl.End();

            gd.SubmitCommands(cl);
            gd.SwapBuffers(gd.MainSwapchain);

            // Frame-rate cap. Active = ANY of:
            //   • A 3D pass ran this frame (user-visible change happened).
            //   • The previous frame ran (so a press + release cluster
            //     doesn't immediately drop to the slow rate).
            //   • The user is in the MIDDLE of an interaction (gizmo drag,
            //     camera orbit/pan, fly-mode). During a drag the mouse can
            //     pause for a frame or two between movements — without this
            //     `IsActivelyInteracting` check, those frames classified as
            //     idle and the editor felt "asleep" while the user was
            //     actively dragging. Keeping the active rate for the entire
            //     gesture is essential for responsive feedback.
            // Idle uses a much longer target so GPU sits near zero between
            // interactions. Thread.Sleep is granular (~1ms on Windows with
            // the timer resolution boost from .NET 9), good enough for an
            // editor's idle loop.
            bool active = didRender3D || lastFrame3DRendered || ui.IsActivelyInteracting;
            lastFrame3DRendered = didRender3D;
            double targetMs = active ? ui.ActiveFrameTargetMs : ui.IdleFrameTargetMs;
            double elapsedMs = (clock.Elapsed - frameStart).TotalMilliseconds;
            int sleepMs = (int)(targetMs - elapsedMs);
            if (sleepMs > 0)
                System.Threading.Thread.Sleep(sleepMs);
        }

        gd.WaitForIdle();
        renderer3D.Dispose();
        imguiRenderer.Dispose();
        cl.Dispose();
        gd.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("kernel32")]
    private static extern bool AllocConsole();

    /// Lay out a full-window invisible host window with a dockspace inside it.
    /// Every dockable panel that calls <c>ImGui.Begin()</c> after this docks
    /// into the resulting tree (or floats / tears off, user's choice). User-
    /// adjusted dock layout persists in <c>imgui.ini</c> next to the exe.
    ///
    /// The dockspace is a "passthrough" host: it doesn't render its own
    /// background — the 3D viewport behind it shows through unchanged. The
    /// menu bar reserves its own slot at the top via <c>BeginMainMenuBar()</c>;
    /// we offset the dockspace work area below it.
    private static void SetupDockspace(Sdl2Window window)
    {
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.SetNextWindowViewport(viewport.ID);

        ImGuiWindowFlags hostFlags =
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.Begin("##EditorDockHost", hostFlags);
        ImGui.PopStyleVar(3);

        // PassthruCentralNode = the central docking node has no background
        // (so the 3D viewport behind shows through). The viewport panel can
        // dock its own image into this central node, but if it floats, the
        // bare scene is still visible.
        uint dockId = ImGui.GetID("##EditorMainDockspace");
        ImGui.DockSpace(dockId, Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);
        ImGui.End();
    }

    /// Aurora theme — polished modern dark palette for the editor. Inspired
    /// by VS Code Dark+ and JetBrains Darcula:
    ///   • neutral charcoal backgrounds (no blue tint)
    ///   • cool-blue accent (#5078E0-ish) reserved for active states + selection
    ///   • subtle borders + dividers (not invisible, not heavy)
    ///   • tighter spacing than ImGui defaults so dense editor panels read clean
    private static void ApplyAuroraTheme()
    {
        ImGui.StyleColorsDark();
        ImGuiStylePtr s = ImGui.GetStyle();

        // Geometry — rounded but not overdone, tight but readable.
        s.WindowRounding       = 6f;
        s.ChildRounding        = 4f;
        s.FrameRounding        = 4f;
        s.PopupRounding        = 6f;
        s.ScrollbarRounding    = 6f;
        s.GrabRounding         = 3f;
        s.TabRounding          = 4f;
        s.WindowBorderSize     = 1f;
        s.FrameBorderSize      = 0f;
        s.PopupBorderSize      = 1f;
        s.WindowPadding        = new Vector2(10, 8);
        s.FramePadding         = new Vector2(8, 4);
        s.ItemSpacing          = new Vector2(8, 4);
        s.ItemInnerSpacing     = new Vector2(6, 4);
        s.IndentSpacing        = 18f;
        s.ScrollbarSize        = 12f;
        s.GrabMinSize          = 12f;
        s.WindowTitleAlign     = new Vector2(0.0f, 0.5f);
        s.TabBarBorderSize     = 1f;
        s.SeparatorTextBorderSize = 2f;

        // Palette anchors — keep all derived colors in sync with these.
        // Lifted dark, not near-black. Target sits between Blender's dark
        // theme (#303030) and JetBrains Darcula (#3C3F41) — comfortable for
        // long sessions without disappearing into the monitor bezel.
        Vector4 bg0       = new(0.145f, 0.157f, 0.188f, 1f);  // window background  #252830
        Vector4 bg1       = new(0.180f, 0.196f, 0.235f, 1f);  // child / frame      #2E323C
        Vector4 bg2       = new(0.227f, 0.247f, 0.298f, 1f);  // hovered frame      #3A3F4C
        Vector4 bg3       = new(0.275f, 0.298f, 0.361f, 1f);  // active frame       #464C5C
        Vector4 line      = new(0.329f, 0.349f, 0.416f, 1f);  // border / separator #54596A
        Vector4 text      = new(0.918f, 0.925f, 0.945f, 1f);  //                    #EAECF1
        Vector4 textDim   = new(0.620f, 0.650f, 0.720f, 1f);  //                    #9EA6B8
        Vector4 accent    = new(0.380f, 0.545f, 0.945f, 1f);  // brighter blue, reads cleanly against the lifted bg
        Vector4 accentHi  = new(0.475f, 0.620f, 0.995f, 1f);
        Vector4 accentLo  = new(0.290f, 0.420f, 0.745f, 1f);
        Vector4 warn      = new(0.965f, 0.720f, 0.220f, 1f);
        Vector4 danger    = new(0.940f, 0.380f, 0.395f, 1f);

        var c = s.Colors;
        c[(int)ImGuiCol.Text]                   = text;
        c[(int)ImGuiCol.TextDisabled]           = textDim;
        c[(int)ImGuiCol.WindowBg]               = bg0;
        c[(int)ImGuiCol.ChildBg]                = new Vector4(0, 0, 0, 0);
        c[(int)ImGuiCol.PopupBg]                = new Vector4(bg0.X, bg0.Y, bg0.Z, 0.98f);
        c[(int)ImGuiCol.Border]                 = line;
        c[(int)ImGuiCol.BorderShadow]           = new Vector4(0, 0, 0, 0);
        c[(int)ImGuiCol.FrameBg]                = bg1;
        c[(int)ImGuiCol.FrameBgHovered]         = bg2;
        c[(int)ImGuiCol.FrameBgActive]          = bg3;
        c[(int)ImGuiCol.TitleBg]                = bg1;
        c[(int)ImGuiCol.TitleBgActive]          = bg2;
        c[(int)ImGuiCol.TitleBgCollapsed]       = bg1;
        c[(int)ImGuiCol.MenuBarBg]              = bg1;
        c[(int)ImGuiCol.ScrollbarBg]            = new Vector4(0, 0, 0, 0);
        c[(int)ImGuiCol.ScrollbarGrab]          = bg3;
        c[(int)ImGuiCol.ScrollbarGrabHovered]   = new Vector4(0.30f, 0.32f, 0.36f, 1f);
        c[(int)ImGuiCol.ScrollbarGrabActive]    = accent;
        c[(int)ImGuiCol.CheckMark]              = accent;
        c[(int)ImGuiCol.SliderGrab]             = accent;
        c[(int)ImGuiCol.SliderGrabActive]       = accentHi;
        c[(int)ImGuiCol.Button]                 = bg2;
        c[(int)ImGuiCol.ButtonHovered]          = new Vector4(0.235f, 0.345f, 0.620f, 1f);
        c[(int)ImGuiCol.ButtonActive]           = accent;
        c[(int)ImGuiCol.Header]                 = new Vector4(accent.X, accent.Y, accent.Z, 0.32f);
        c[(int)ImGuiCol.HeaderHovered]          = new Vector4(accent.X, accent.Y, accent.Z, 0.45f);
        c[(int)ImGuiCol.HeaderActive]           = new Vector4(accent.X, accent.Y, accent.Z, 0.60f);
        c[(int)ImGuiCol.Separator]              = line;
        c[(int)ImGuiCol.SeparatorHovered]       = accentLo;
        c[(int)ImGuiCol.SeparatorActive]        = accent;
        c[(int)ImGuiCol.ResizeGrip]             = new Vector4(line.X, line.Y, line.Z, 0.50f);
        c[(int)ImGuiCol.ResizeGripHovered]      = accentLo;
        c[(int)ImGuiCol.ResizeGripActive]       = accent;
        c[(int)ImGuiCol.Tab]                    = bg1;
        c[(int)ImGuiCol.TabHovered]             = new Vector4(accent.X, accent.Y, accent.Z, 0.55f);
        c[(int)ImGuiCol.TabSelected]            = accent;
        c[(int)ImGuiCol.TabSelectedOverline]    = accentHi;
        c[(int)ImGuiCol.TabDimmed]              = bg1;
        c[(int)ImGuiCol.TabDimmedSelected]      = accentLo;
        c[(int)ImGuiCol.TabDimmedSelectedOverline] = new Vector4(accentLo.X, accentLo.Y, accentLo.Z, 0f);
        c[(int)ImGuiCol.DockingPreview]         = new Vector4(accent.X, accent.Y, accent.Z, 0.55f);
        c[(int)ImGuiCol.DockingEmptyBg]         = bg0;
        c[(int)ImGuiCol.PlotLines]              = textDim;
        c[(int)ImGuiCol.PlotLinesHovered]       = accentHi;
        c[(int)ImGuiCol.PlotHistogram]          = accent;
        c[(int)ImGuiCol.PlotHistogramHovered]   = accentHi;
        c[(int)ImGuiCol.TableHeaderBg]          = bg1;
        c[(int)ImGuiCol.TableBorderStrong]      = line;
        c[(int)ImGuiCol.TableBorderLight]       = new Vector4(line.X, line.Y, line.Z, 0.50f);
        c[(int)ImGuiCol.TableRowBg]             = new Vector4(0, 0, 0, 0);
        c[(int)ImGuiCol.TableRowBgAlt]          = new Vector4(1, 1, 1, 0.025f);
        c[(int)ImGuiCol.TextSelectedBg]         = new Vector4(accent.X, accent.Y, accent.Z, 0.40f);
        c[(int)ImGuiCol.DragDropTarget]         = warn;
        c[(int)ImGuiCol.NavCursor]              = accent;
        c[(int)ImGuiCol.NavWindowingHighlight]  = accentHi;
        c[(int)ImGuiCol.NavWindowingDimBg]      = new Vector4(0, 0, 0, 0.60f);
        c[(int)ImGuiCol.ModalWindowDimBg]       = new Vector4(0, 0, 0, 0.55f);
    }
}
