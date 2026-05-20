using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.SPIRV;

namespace ChallengeEditor.Rendering;

/// Color-space mode for ImGui's output. The legacy <c>Veldrid.ImGui</c>
/// package exposed an identical enum; we redeclare it here because we
/// dropped that package in favor of a vendored renderer (this file).
///
///   <see cref="Legacy"/>: emit colors verbatim — for sRGB-output swapchains
///     that perform their own sRGB → linear conversion in the hardware.
///   <see cref="Linear"/>: gamma-correct on the way out — for linear-output
///     swapchains where ImGui's authored sRGB tints would otherwise look
///     washed-out when composited with linear 3D content.
public enum ColorSpaceHandling { Legacy = 0, Linear = 1 }

/// Modern ImGui renderer for Veldrid. Replaces the upstream `Veldrid.ImGui`
/// 5.72.0 package, which is built against ImGui ~1.72 and uses the legacy
/// <c>ImGuiIOPtr.KeyMap</c> input API — removed in ImGui.NET 1.91. We need
/// the 1.91+ binding for the docking branch of cimgui (dockable panels,
/// floating tear-offs, persistent layout). So this file feeds input through
/// the modern <c>AddKeyEvent</c> / <c>AddMouseButtonEvent</c> / etc API and
/// owns its own pipeline + font atlas + dynamic vertex/index buffers.
///
/// The public API is intentionally identical to the upstream package
/// signature so call sites (Program.cs, Renderer3D.cs) don't have to change:
///   • <c>new ImGuiRenderer(gd, outputDesc, w, h, ColorSpaceHandling)</c>
///   • <c>Update(dt, InputSnapshot)</c>
///   • <c>Render(gd, CommandList)</c>
///   • <c>WindowResized(w, h)</c>
///   • <c>GetOrCreateImGuiBinding(factory, Texture)</c> — bind a render
///     target as an ImGui texture so it can be drawn with <c>ImGui.Image</c>
///   • <c>RemoveImGuiBinding(Texture)</c>
///   • <c>Dispose()</c>
///
/// Shader pair: standard ImGui draw shader (pos2 + uv2 + color8). Compiled
/// from inline GLSL at startup via Veldrid.SPIRV → backend-native bytecode.
public sealed class ImGuiRenderer : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly ColorSpaceHandling _colorSpaceHandling;

    // GPU resources owned by this renderer.
    private DeviceBuffer? _vertexBuffer;
    private DeviceBuffer? _indexBuffer;
    private DeviceBuffer _projMatrixBuffer = null!;
    private Texture _fontTexture = null!;
    private TextureView _fontTextureView = null!;
    private Sampler _fontSampler = null!;
    private ResourceLayout _layout = null!;
    private ResourceLayout _textureLayout = null!;
    private Pipeline _pipeline = null!;
    private ResourceSet _mainResourceSet = null!;
    private ResourceSet _fontTextureResourceSet = null!;

    private int _windowWidth;
    private int _windowHeight;
    private Vector2 _scaleFactor = Vector2.One;
    private IntPtr _imguiContext;

    // ID → (Texture, TextureView, ResourceSet) mapping for ImGui.Image
    // calls that reference render targets other than the font atlas.
    // Allocated lazily by GetOrCreateImGuiBinding.
    private readonly Dictionary<TextureView, ResourceSetInfo> _setsByView = new();
    private readonly Dictionary<Texture, TextureView> _autoViewsByTexture = new();
    private readonly Dictionary<IntPtr, ResourceSetInfo> _viewsById = new();
    private readonly List<IDisposable> _ownedResources = new();
    private int _nextBindingId = 100;

    private record struct ResourceSetInfo(IntPtr ImGuiBinding, ResourceSet ResourceSet);

    public ImGuiRenderer(
        GraphicsDevice gd,
        OutputDescription outputDescription,
        int width,
        int height,
        ColorSpaceHandling colorSpaceHandling)
    {
        _gd = gd;
        _colorSpaceHandling = colorSpaceHandling;
        _windowWidth = width;
        _windowHeight = height;

        _imguiContext = ImGui.CreateContext();
        ImGui.SetCurrentContext(_imguiContext);

        ImGuiIOPtr io = ImGui.GetIO();
        io.Fonts.AddFontDefault();
        io.Fonts.Flags |= ImFontAtlasFlags.NoBakedLines;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.BackendFlags |= ImGuiBackendFlags.HasMouseCursors;
        io.BackendFlags |= ImGuiBackendFlags.HasSetMousePos;

        CreateDeviceResources(outputDescription);
        SetPerFrameImGuiData(1f / 60f);
        // Frame lifecycle: Update() opens the frame with NewFrame(); Render()
        // closes it with Render(). We intentionally do NOT call NewFrame() in
        // the constructor — if we did, the first Update() would call NewFrame
        // a second time without an intervening Render and ImGui asserts
        // "Forgot to call Render() or EndFrame() at the end of the previous
        // frame". Callers MUST invoke Update() before any ImGui.Begin/End
        // each frame.
    }

    /// Resizes the projection viewport. Call from your window resize event.
    public void WindowResized(int width, int height)
    {
        _windowWidth = width;
        _windowHeight = height;
    }

    /// Bind a render-target texture so it can be drawn with <c>ImGui.Image</c>.
    /// Returns a stable handle that survives across frames. Re-call after
    /// a render target is recreated (different Texture instance).
    public IntPtr GetOrCreateImGuiBinding(ResourceFactory factory, Texture texture)
    {
        if (!_autoViewsByTexture.TryGetValue(texture, out TextureView? view))
        {
            view = factory.CreateTextureView(texture);
            _autoViewsByTexture[texture] = view;
            _ownedResources.Add(view);
        }
        return GetOrCreateImGuiBinding(factory, view);
    }

    /// Bind an existing TextureView for use with <c>ImGui.Image</c>.
    public IntPtr GetOrCreateImGuiBinding(ResourceFactory factory, TextureView view)
    {
        if (_setsByView.TryGetValue(view, out var existing))
            return existing.ImGuiBinding;

        ResourceSet set = factory.CreateResourceSet(new ResourceSetDescription(_textureLayout, view));
        _ownedResources.Add(set);
        IntPtr id = (IntPtr)(++_nextBindingId);
        var info = new ResourceSetInfo(id, set);
        _setsByView[view] = info;
        _viewsById[id] = info;
        return id;
    }

    /// Drops the cached binding for a texture. Call when the underlying
    /// render target is about to be disposed and recreated at a new size.
    public void RemoveImGuiBinding(Texture texture)
    {
        if (_autoViewsByTexture.TryGetValue(texture, out TextureView? view))
        {
            RemoveImGuiBinding(view);
            _autoViewsByTexture.Remove(texture);
            view.Dispose();
            _ownedResources.Remove(view);
        }
    }

    public void RemoveImGuiBinding(TextureView view)
    {
        if (_setsByView.TryGetValue(view, out var info))
        {
            info.ResourceSet.Dispose();
            _ownedResources.Remove(info.ResourceSet);
            _setsByView.Remove(view);
            _viewsById.Remove(info.ImGuiBinding);
        }
    }

    /// Per-frame update: pumps input into ImGui, advances IO, calls
    /// <c>ImGui.NewFrame()</c>. Pair with <see cref="Render"/> AFTER your
    /// panels have been drawn.
    public void Update(float deltaSeconds, InputSnapshot snapshot)
    {
        SetPerFrameImGuiData(deltaSeconds);
        UpdateImGuiInput(snapshot);
        UpdateMouseCursor();
        ImGui.NewFrame();
    }

    /// Submit ImGui's accumulated draw data through the command list. Call
    /// after every <c>ImGui.Begin/End</c> for the frame is complete.
    public void Render(GraphicsDevice gd, CommandList cl)
    {
        ImGui.Render();
        RenderImDrawData(ImGui.GetDrawData(), gd, cl);
    }

    public void Dispose()
    {
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        _projMatrixBuffer.Dispose();
        _fontTexture.Dispose();
        _fontTextureView.Dispose();
        _fontSampler.Dispose();
        _layout.Dispose();
        _textureLayout.Dispose();
        _pipeline.Dispose();
        _mainResourceSet.Dispose();
        _fontTextureResourceSet.Dispose();
        foreach (var res in _ownedResources) res.Dispose();
        _ownedResources.Clear();
        if (_imguiContext != IntPtr.Zero)
        {
            ImGui.DestroyContext(_imguiContext);
            _imguiContext = IntPtr.Zero;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Device resource creation
    // ─────────────────────────────────────────────────────────────────────
    private void CreateDeviceResources(OutputDescription outputDescription)
    {
        var factory = _gd.ResourceFactory;

        _projMatrixBuffer = factory.CreateBuffer(new BufferDescription(64, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
        _projMatrixBuffer.Name = "ImGui.NET ProjectionMatrixBuffer";

        // Compile shaders from inline GLSL via Veldrid.SPIRV so we don't
        // ship per-backend bytecode files. Standard ImGui draw shader.
        byte[] vertexBytes = System.Text.Encoding.UTF8.GetBytes(VertexShaderGlsl);
        byte[] fragmentBytes = System.Text.Encoding.UTF8.GetBytes(
            _colorSpaceHandling == ColorSpaceHandling.Linear ? FragmentShaderGlslLinear : FragmentShaderGlsl);

        var vertexShaderDesc = new ShaderDescription(ShaderStages.Vertex, vertexBytes, "main");
        var fragmentShaderDesc = new ShaderDescription(ShaderStages.Fragment, fragmentBytes, "main");
        Shader[] shaders = factory.CreateFromSpirv(vertexShaderDesc, fragmentShaderDesc);

        // All elements use TextureCoordinate semantic — Veldrid.SPIRV's HLSL
        // cross-compilation emits every vertex input as `TEXCOORD<location>`
        // regardless of the original GLSL location's intended role. If we
        // declared Position / Color here, D3D11 would fail input-layout
        // creation with E_INVALIDARG because the shader signature only has
        // TEXCOORD slots to bind to.
        VertexLayoutDescription[] vertexLayouts =
        {
            new VertexLayoutDescription(
                new VertexElementDescription("in_position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("in_texCoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("in_color", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Byte4_Norm))
        };

        _layout = factory.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("ProjectionMatrixBuffer", ResourceKind.UniformBuffer, ShaderStages.Vertex),
            new ResourceLayoutElementDescription("MainSampler", ResourceKind.Sampler, ShaderStages.Fragment)));
        _textureLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("MainTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment)));

        var blendDesc = BlendStateDescription.SingleAlphaBlend;

        GraphicsPipelineDescription pd = new(
            blendDesc,
            new DepthStencilStateDescription(false, false, ComparisonKind.Always),
            new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, true, true),
            PrimitiveTopology.TriangleList,
            new ShaderSetDescription(vertexLayouts, shaders),
            new[] { _layout, _textureLayout },
            outputDescription,
            ResourceBindingModel.Default);
        _pipeline = factory.CreateGraphicsPipeline(pd);

        _fontSampler = _gd.LinearSampler;

        _mainResourceSet = factory.CreateResourceSet(new ResourceSetDescription(
            _layout, _projMatrixBuffer, _fontSampler));

        RecreateFontDeviceTexture();
    }

    /// Build/rebuild the font atlas. Call whenever fonts are added/removed.
    private void RecreateFontDeviceTexture()
    {
        ImGuiIOPtr io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out int _);
        io.Fonts.SetTexID((IntPtr)1);

        _fontTexture?.Dispose();
        _fontTextureView?.Dispose();

        var factory = _gd.ResourceFactory;
        _fontTexture = factory.CreateTexture(TextureDescription.Texture2D(
            (uint)width, (uint)height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
        _fontTexture.Name = "ImGui.NET Font Texture";
        _gd.UpdateTexture(_fontTexture, pixels, (uint)(width * height * 4),
            0, 0, 0, (uint)width, (uint)height, 1, 0, 0);
        _fontTextureView = factory.CreateTextureView(_fontTexture);

        _fontTextureResourceSet?.Dispose();
        _fontTextureResourceSet = factory.CreateResourceSet(new ResourceSetDescription(_textureLayout, _fontTextureView));

        io.Fonts.ClearTexData();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Per-frame input feeding (modern AddXxxEvent API)
    // ─────────────────────────────────────────────────────────────────────
    private void SetPerFrameImGuiData(float deltaSeconds)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = new Vector2(_windowWidth / _scaleFactor.X, _windowHeight / _scaleFactor.Y);
        io.DisplayFramebufferScale = _scaleFactor;
        io.DeltaTime = deltaSeconds > 0 ? deltaSeconds : 1f / 60f;
    }

    private void UpdateImGuiInput(InputSnapshot snapshot)
    {
        ImGuiIOPtr io = ImGui.GetIO();

        // Mouse position + buttons + wheel
        io.AddMousePosEvent(snapshot.MousePosition.X, snapshot.MousePosition.Y);
        io.AddMouseButtonEvent(0, snapshot.IsMouseDown(MouseButton.Left));
        io.AddMouseButtonEvent(1, snapshot.IsMouseDown(MouseButton.Right));
        io.AddMouseButtonEvent(2, snapshot.IsMouseDown(MouseButton.Middle));
        io.AddMouseButtonEvent(3, snapshot.IsMouseDown(MouseButton.Button1));
        io.AddMouseButtonEvent(4, snapshot.IsMouseDown(MouseButton.Button2));
        io.AddMouseWheelEvent(0f, snapshot.WheelDelta);

        // Typed characters
        for (int i = 0; i < snapshot.KeyCharPresses.Count; i++)
            io.AddInputCharacter(snapshot.KeyCharPresses[i]);

        // Keyboard key events. ImGui distinguishes "key down/up edge" from
        // "currently held"; the snapshot only carries edges, so we feed each
        // KeyEvent directly. ImGui's internal state handles repeat.
        for (int i = 0; i < snapshot.KeyEvents.Count; i++)
        {
            var keyEvent = snapshot.KeyEvents[i];
            ImGuiKey imguiKey = TranslateKey(keyEvent.Key);
            if (imguiKey != ImGuiKey.None)
                io.AddKeyEvent(imguiKey, keyEvent.Down);

            // Modifier flags also fire as their own ImGuiMod_* events so
            // shortcut routing sees Ctrl/Shift/Alt/Super state. ImGui 1.87+
            // requires explicit mod events alongside key events.
            switch (keyEvent.Key)
            {
                case Key.LControl: case Key.RControl:
                    io.AddKeyEvent(ImGuiKey.ModCtrl, keyEvent.Down); break;
                case Key.LShift: case Key.RShift:
                    io.AddKeyEvent(ImGuiKey.ModShift, keyEvent.Down); break;
                case Key.LAlt: case Key.RAlt:
                    io.AddKeyEvent(ImGuiKey.ModAlt, keyEvent.Down); break;
                case Key.LWin: case Key.RWin:
                    io.AddKeyEvent(ImGuiKey.ModSuper, keyEvent.Down); break;
            }
        }
    }

    private void UpdateMouseCursor()
    {
        ImGuiIOPtr io = ImGui.GetIO();
        if ((io.ConfigFlags & ImGuiConfigFlags.NoMouseCursorChange) != 0) return;
        // We let SDL2 / OS draw the cursor based on ImGui's request. Veldrid
        // doesn't expose a cursor-change API, so this is currently a no-op;
        // the OS default cursor is shown. If we ever want hand/IBeam/resize
        // cursors over ImGui widgets, hook SDL_SetCursor from here.
        _ = ImGui.GetMouseCursor();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Draw data submission
    // ─────────────────────────────────────────────────────────────────────
    private unsafe void RenderImDrawData(ImDrawDataPtr drawData, GraphicsDevice gd, CommandList cl)
    {
        uint vertexOffsetInVertices = 0;
        uint indexOffsetInElements = 0;
        if (drawData.CmdListsCount == 0) return;

        uint totalVbSize = (uint)(drawData.TotalVtxCount * sizeof(ImDrawVert));
        if (_vertexBuffer == null || totalVbSize > _vertexBuffer.SizeInBytes)
        {
            _vertexBuffer?.Dispose();
            _vertexBuffer = gd.ResourceFactory.CreateBuffer(new BufferDescription(
                (uint)(totalVbSize * 1.5f), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
        }
        uint totalIbSize = (uint)(drawData.TotalIdxCount * sizeof(ushort));
        if (_indexBuffer == null || totalIbSize > _indexBuffer.SizeInBytes)
        {
            _indexBuffer?.Dispose();
            _indexBuffer = gd.ResourceFactory.CreateBuffer(new BufferDescription(
                (uint)(totalIbSize * 1.5f), BufferUsage.IndexBuffer | BufferUsage.Dynamic));
        }

        for (int i = 0; i < drawData.CmdListsCount; i++)
        {
            ImDrawListPtr cmdList = drawData.CmdLists[i];
            cl.UpdateBuffer(_vertexBuffer, vertexOffsetInVertices * (uint)sizeof(ImDrawVert),
                cmdList.VtxBuffer.Data, (uint)(cmdList.VtxBuffer.Size * sizeof(ImDrawVert)));
            cl.UpdateBuffer(_indexBuffer, indexOffsetInElements * sizeof(ushort),
                cmdList.IdxBuffer.Data, (uint)(cmdList.IdxBuffer.Size * sizeof(ushort)));
            vertexOffsetInVertices += (uint)cmdList.VtxBuffer.Size;
            indexOffsetInElements += (uint)cmdList.IdxBuffer.Size;
        }

        // Orthographic projection (top-left origin → clip space).
        var io = ImGui.GetIO();
        Matrix4x4 mvp = Matrix4x4.CreateOrthographicOffCenter(
            0f, io.DisplaySize.X,
            io.DisplaySize.Y, 0f,
            -1f, 1f);
        gd.UpdateBuffer(_projMatrixBuffer, 0, mvp);

        cl.SetVertexBuffer(0, _vertexBuffer);
        cl.SetIndexBuffer(_indexBuffer, IndexFormat.UInt16);
        cl.SetPipeline(_pipeline);
        cl.SetGraphicsResourceSet(0, _mainResourceSet);

        drawData.ScaleClipRects(io.DisplayFramebufferScale);

        int vtxOffset = 0;
        int idxOffset = 0;
        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            ImDrawListPtr cmdList = drawData.CmdLists[n];
            for (int cmd_i = 0; cmd_i < cmdList.CmdBuffer.Size; cmd_i++)
            {
                ImDrawCmdPtr pcmd = cmdList.CmdBuffer[cmd_i];
                if (pcmd.UserCallback != IntPtr.Zero) continue;

                // Texture bind for this draw — font atlas (TextureId == 1)
                // or a custom binding from GetOrCreateImGuiBinding.
                if (pcmd.TextureId != IntPtr.Zero)
                {
                    if (pcmd.TextureId == (IntPtr)1)
                        cl.SetGraphicsResourceSet(1, _fontTextureResourceSet);
                    else if (_viewsById.TryGetValue(pcmd.TextureId, out var info))
                        cl.SetGraphicsResourceSet(1, info.ResourceSet);
                    else
                        cl.SetGraphicsResourceSet(1, _fontTextureResourceSet);
                }

                cl.SetScissorRect(0,
                    (uint)pcmd.ClipRect.X,
                    (uint)pcmd.ClipRect.Y,
                    (uint)(pcmd.ClipRect.Z - pcmd.ClipRect.X),
                    (uint)(pcmd.ClipRect.W - pcmd.ClipRect.Y));

                cl.DrawIndexed(pcmd.ElemCount, 1,
                    pcmd.IdxOffset + (uint)idxOffset,
                    (int)(pcmd.VtxOffset + vtxOffset),
                    0);
            }
            idxOffset += cmdList.IdxBuffer.Size;
            vtxOffset += cmdList.VtxBuffer.Size;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Veldrid Key → ImGuiKey map. Veldrid mirrors SDL2 scancodes; ImGui has
    // its own enum. This table covers every key the editor's bindings can
    // reference (alphanumerics, function keys, navigation, modifiers).
    // ─────────────────────────────────────────────────────────────────────
    private static ImGuiKey TranslateKey(Key key) => key switch
    {
        Key.Tab => ImGuiKey.Tab,
        Key.Left => ImGuiKey.LeftArrow,
        Key.Right => ImGuiKey.RightArrow,
        Key.Up => ImGuiKey.UpArrow,
        Key.Down => ImGuiKey.DownArrow,
        Key.PageUp => ImGuiKey.PageUp,
        Key.PageDown => ImGuiKey.PageDown,
        Key.Home => ImGuiKey.Home,
        Key.End => ImGuiKey.End,
        Key.Insert => ImGuiKey.Insert,
        Key.Delete => ImGuiKey.Delete,
        Key.BackSpace => ImGuiKey.Backspace,
        Key.Space => ImGuiKey.Space,
        Key.Enter => ImGuiKey.Enter,
        Key.Escape => ImGuiKey.Escape,
        Key.ControlLeft => ImGuiKey.LeftCtrl,
        Key.ControlRight => ImGuiKey.RightCtrl,
        Key.ShiftLeft => ImGuiKey.LeftShift,
        Key.ShiftRight => ImGuiKey.RightShift,
        Key.AltLeft => ImGuiKey.LeftAlt,
        Key.AltRight => ImGuiKey.RightAlt,
        Key.WinLeft => ImGuiKey.LeftSuper,
        Key.WinRight => ImGuiKey.RightSuper,
        Key.A => ImGuiKey.A, Key.B => ImGuiKey.B, Key.C => ImGuiKey.C, Key.D => ImGuiKey.D,
        Key.E => ImGuiKey.E, Key.F => ImGuiKey.F, Key.G => ImGuiKey.G, Key.H => ImGuiKey.H,
        Key.I => ImGuiKey.I, Key.J => ImGuiKey.J, Key.K => ImGuiKey.K, Key.L => ImGuiKey.L,
        Key.M => ImGuiKey.M, Key.N => ImGuiKey.N, Key.O => ImGuiKey.O, Key.P => ImGuiKey.P,
        Key.Q => ImGuiKey.Q, Key.R => ImGuiKey.R, Key.S => ImGuiKey.S, Key.T => ImGuiKey.T,
        Key.U => ImGuiKey.U, Key.V => ImGuiKey.V, Key.W => ImGuiKey.W, Key.X => ImGuiKey.X,
        Key.Y => ImGuiKey.Y, Key.Z => ImGuiKey.Z,
        Key.Number0 => ImGuiKey._0, Key.Number1 => ImGuiKey._1, Key.Number2 => ImGuiKey._2,
        Key.Number3 => ImGuiKey._3, Key.Number4 => ImGuiKey._4, Key.Number5 => ImGuiKey._5,
        Key.Number6 => ImGuiKey._6, Key.Number7 => ImGuiKey._7, Key.Number8 => ImGuiKey._8,
        Key.Number9 => ImGuiKey._9,
        Key.F1 => ImGuiKey.F1, Key.F2 => ImGuiKey.F2, Key.F3 => ImGuiKey.F3, Key.F4 => ImGuiKey.F4,
        Key.F5 => ImGuiKey.F5, Key.F6 => ImGuiKey.F6, Key.F7 => ImGuiKey.F7, Key.F8 => ImGuiKey.F8,
        Key.F9 => ImGuiKey.F9, Key.F10 => ImGuiKey.F10, Key.F11 => ImGuiKey.F11, Key.F12 => ImGuiKey.F12,
        Key.Comma => ImGuiKey.Comma,
        Key.Period => ImGuiKey.Period,
        Key.Slash => ImGuiKey.Slash,
        Key.BackSlash => ImGuiKey.Backslash,
        Key.Semicolon => ImGuiKey.Semicolon,
        Key.Quote => ImGuiKey.Apostrophe,
        Key.Tilde => ImGuiKey.GraveAccent,
        Key.Minus => ImGuiKey.Minus,
        Key.Plus => ImGuiKey.Equal,
        Key.BracketLeft => ImGuiKey.LeftBracket,
        Key.BracketRight => ImGuiKey.RightBracket,
        Key.Keypad0 => ImGuiKey.Keypad0, Key.Keypad1 => ImGuiKey.Keypad1, Key.Keypad2 => ImGuiKey.Keypad2,
        Key.Keypad3 => ImGuiKey.Keypad3, Key.Keypad4 => ImGuiKey.Keypad4, Key.Keypad5 => ImGuiKey.Keypad5,
        Key.Keypad6 => ImGuiKey.Keypad6, Key.Keypad7 => ImGuiKey.Keypad7, Key.Keypad8 => ImGuiKey.Keypad8,
        Key.Keypad9 => ImGuiKey.Keypad9,
        Key.KeypadAdd => ImGuiKey.KeypadAdd,
        Key.KeypadSubtract => ImGuiKey.KeypadSubtract,
        Key.KeypadMultiply => ImGuiKey.KeypadMultiply,
        Key.KeypadDivide => ImGuiKey.KeypadDivide,
        Key.KeypadDecimal => ImGuiKey.KeypadDecimal,
        Key.KeypadEnter => ImGuiKey.KeypadEnter,
        Key.CapsLock => ImGuiKey.CapsLock,
        Key.NumLock => ImGuiKey.NumLock,
        Key.ScrollLock => ImGuiKey.ScrollLock,
        Key.PrintScreen => ImGuiKey.PrintScreen,
        Key.Pause => ImGuiKey.Pause,
        _ => ImGuiKey.None,
    };

    // ─────────────────────────────────────────────────────────────────────
    // Inline shader source. Standard ImGui draw vertex format. The fragment
    // shader has two variants — sRGB-output (legacy) and linear (when the
    // host swapchain is treating ImGui's colors as linear; the editor uses
    // Linear so the editor 3D viewport's tonemapped output blends correctly).
    // ─────────────────────────────────────────────────────────────────────
    private const string VertexShaderGlsl = @"#version 450

layout(location = 0) in vec2 in_position;
layout(location = 1) in vec2 in_texCoord;
layout(location = 2) in vec4 in_color;

layout(set = 0, binding = 0) uniform ProjectionMatrixBuffer { mat4 projection_matrix; };

layout(location = 0) out vec4 out_color;
layout(location = 1) out vec2 out_texCoord;

void main()
{
    gl_Position = projection_matrix * vec4(in_position, 0, 1);
    out_color = in_color;
    out_texCoord = in_texCoord;
}";

    private const string FragmentShaderGlsl = @"#version 450

layout(location = 0) in vec4 in_color;
layout(location = 1) in vec2 in_texCoord;

layout(set = 0, binding = 1) uniform sampler MainSampler;
layout(set = 1, binding = 0) uniform texture2D MainTexture;

layout(location = 0) out vec4 out_color;

void main()
{
    out_color = in_color * texture(sampler2D(MainTexture, MainSampler), in_texCoord);
}";

    /// Linear-output fragment shader. ImGui authors colors in sRGB space; if
    /// the swapchain expects linear input, gamma-correct on the way out so
    /// chrome (text, panels) doesn't look washed out. The font atlas is
    /// already R8_G8_B8_A8_UNorm (linear sampling); the per-vertex tint is
    /// what needs conversion.
    private const string FragmentShaderGlslLinear = @"#version 450

layout(location = 0) in vec4 in_color;
layout(location = 1) in vec2 in_texCoord;

layout(set = 0, binding = 1) uniform sampler MainSampler;
layout(set = 1, binding = 0) uniform texture2D MainTexture;

layout(location = 0) out vec4 out_color;

vec3 SrgbToLinear(vec3 c)
{
    return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(0.04045, c));
}

void main()
{
    vec4 tinted = in_color * texture(sampler2D(MainTexture, MainSampler), in_texCoord);
    out_color = vec4(SrgbToLinear(tinted.rgb), tinted.a);
}";
}
