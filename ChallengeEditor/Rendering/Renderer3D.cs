using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Veldrid;
using Veldrid.SPIRV;

namespace ChallengeEditor.Rendering;

/// Off-screen 3D renderer. Draws the world grid + axes, then iterates the EditorScene
/// to draw a wireframe box for each TriggerVolume and a 3-axis marker for each Locator.
/// Selection is conveyed by a per-draw tint uniform that multiplies the vertex color
/// in the fragment shader, so geometry buffers never need to change for highlight.
public sealed class Renderer3D : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct VertexPositionColor
    {
        public Vector3 Position;
        public Vector3 Color;
        public VertexPositionColor(Vector3 p, Vector3 c) { Position = p; Color = c; }
        public const uint SizeBytes = 24;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VertexPositionNormal
    {
        public Vector3 Position;
        public Vector3 Normal;
        public VertexPositionNormal(Vector3 p, Vector3 n) { Position = p; Normal = n; }
        public const uint SizeBytes = 24;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VertexPositionNormalTexcoord
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 Texcoord;
        public VertexPositionNormalTexcoord(Vector3 p, Vector3 n, Vector2 uv)
        {
            Position = p; Normal = n; Texcoord = uv;
        }
        public const uint SizeBytes = 32;
    }

    /// std140: two mat4 + two vec4 = 160 bytes. <see cref="View"/> is world→view for
    /// mesh derivative normals (stable at large world coordinates); lines ignore it.
    [StructLayout(LayoutKind.Sequential)]
    private struct DrawUniform
    {
        public Matrix4x4 MVP;
        public Matrix4x4 View;
        public Vector4 Tint;
        public Vector4 Params;
    }


    private const string VertexShaderSource = """
#version 450

layout(set = 0, binding = 0) uniform DrawBuffer {
    mat4 MVP;
    mat4 View;
    vec4 Tint;
    vec4 Params;
};

layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Color;

layout(location = 0) out vec3 fsin_Color;

void main()
{
    vec4 p = MVP * vec4(Position, 1.0);
    p.xy += Params.xy * p.w;
    gl_Position = p;
    fsin_Color = Color * Tint.rgb;
}
""";

    private const string FragmentShaderSource = """
#version 450

layout(set = 0, binding = 0) uniform DrawBuffer {
    mat4 MVP;
    mat4 View;
    vec4 Tint;
    vec4 Params;
};

layout(location = 0) in vec3 fsin_Color;
layout(location = 0) out vec4 fsout_Color;

void main()
{
    fsout_Color = vec4(fsin_Color, clamp(Params.z, 0.0, 1.0));
}
""";

    // Mesh pipeline: derivative normals from view-space position so shading stays
    // stable when vertex positions are far from the origin (world-space dFdx/dFdy
    // on large coordinates produces visible noise / "static"). Vertex normal fallback
    // when the triangle projects to a degenerate span in screen space.
    private const string MeshVertexShaderSource = """
#version 450

layout(set = 0, binding = 0) uniform DrawBuffer {
    mat4 MVP;
    mat4 View;
    vec4 Tint;
    vec4 Params;
};

layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Normal;
layout(location = 2) in vec2 Texcoord;

layout(location = 0) out vec3 fsin_ViewPos;
layout(location = 1) out vec3 fsin_Normal;
layout(location = 2) out vec2 fsin_UV;

void main()
{
    vec4 worldPos = vec4(Position, 1.0);
    fsin_ViewPos = (View * worldPos).xyz;
    fsin_Normal = Normal;
    gl_Position = MVP * worldPos;
    fsin_UV = Texcoord;
}
""";

    private const string MeshFragmentShaderSource = """
#version 450

layout(set = 0, binding = 0) uniform DrawBuffer {
    mat4 MVP;
    mat4 View;
    vec4 Tint;
    vec4 Params;
};

layout(set = 1, binding = 0) uniform texture2D DiffuseTex;
layout(set = 1, binding = 1) uniform sampler DiffuseSampler;

layout(location = 0) in vec3 fsin_ViewPos;
layout(location = 1) in vec3 fsin_Normal;
layout(location = 2) in vec2 fsin_UV;
layout(location = 0) out vec4 fsout_Color;

void main()
{
    // Lighting in VIEW space so normals and L match. Prefer vertex normals whenever
    // the mesh has them — derivative face normals get very noisy when triangles
    // cover few pixels (camera far / small on-screen area).
    float nn = dot(fsin_Normal, fsin_Normal);
    vec3 n;
    if (nn > 1e-6)
        n = normalize((View * vec4(fsin_Normal, 0.0)).xyz);
    else {
        vec3 du = dFdx(fsin_ViewPos);
        vec3 dv = dFdy(fsin_ViewPos);
        vec3 nDeriv = cross(du, dv);
        float dn = length(nDeriv);
        n = dn > 1e-10 ? (nDeriv / dn) : vec3(0.0, 0.0, 1.0);
    }

    // Auto-LOD sampling — GPU picks the appropriate mip level from
    // screen-space UV derivatives. With the Vulkan backend's correct
    // SRV/mip plumbing and our staging-texture mip upload, distant
    // textures sample lower-frequency mips and high-frequency moiré
    // (visible on detailed textures like brick walls under D3D11) is gone.
    vec4 tex = texture(sampler2D(DiffuseTex, DiffuseSampler), fsin_UV);
    // Alpha-clip / alpha-test pattern: anything below 0.5 is fully clipped,
    // anything at or above renders as fully opaque (alpha written as 1.0 below
    // + opaque pipeline blend). This avoids the "see through to background"
    // bug that semi-transparent edge pixels caused under the old "low-threshold
    // discard + alpha-blend pipeline" combo — those mid-alpha fragments used
    // to pass discard, blend their RGB with the framebuffer, AND write depth,
    // which occluded everything farther at the same pixel. With the 0.5
    // cutoff + opaque blend, every fragment either fully passes or fully
    // discards; no blending, no rogue depth writes.
    if (tex.a < 0.5) discard;
    vec3 baseColor = tex.rgb * Tint.rgb;

    // Fixed built-in three-term shading. Imported lighting is not supported;
    // a single view-stable directional key plus a soft back term keeps geometry
    // readable everywhere without any per-scene light data.
    vec3 Lw = vec3(0.40, 0.80, 0.50);
    vec3 Lv = normalize((View * vec4(Lw, 0.0)).xyz);
    float keyN  = max( dot(n, Lv), 0.0);
    float backN = max(-dot(n, Lv), 0.0);
    float lighting = 1.00 + 0.25 * keyN + 0.10 * backN;
    vec3 col = baseColor * lighting;
    fsout_Color = vec4(col, 1.0);
}
""";

    private readonly GraphicsDevice _gd;

    /// <summary>Exposed for DIST texture uploads (<see cref="Psg.PsgTextureGpuLoader"/>).</summary>
    public GraphicsDevice GraphicsDevice => _gd;

    // Resources that depend on framebuffer size — re-created on resize.
    private Texture? _colorTarget;
    private Texture? _depthTarget;
    private Framebuffer? _framebuffer;
    private IntPtr _imguiBinding;

    // Persistent resources — created once.
    private readonly DeviceBuffer _drawBuffer;
    private readonly DeviceBuffer _gridBuffer;
    private readonly uint _gridVertexCount;
    private readonly DeviceBuffer _cubeWireBuffer;
    private readonly uint _cubeWireVertexCount;
    private readonly DeviceBuffer _locatorBuffer;
    private readonly uint _locatorVertexCount;
    private readonly DeviceBuffer _signupIndicatorBuffer;
    private readonly uint _signupIndicatorVertexCount;
    private readonly DeviceBuffer _chevronIndicatorBuffer;
    private readonly uint _chevronIndicatorVertexCount;
    private readonly DeviceBuffer _gizmoArrowBuffer;
    private readonly uint _gizmoArrowVertexCount;
    private readonly DeviceBuffer _navigatorBuffer;
    private readonly uint _navigatorVertexCount;
    private readonly DeviceBuffer _ringBuffer;
    private readonly uint _ringVertexCount;
    private readonly DeviceBuffer _axisShaftBuffer;
    private readonly uint _axisShaftVertexCount;
    private readonly Pipeline _linePipeline;
    private readonly Pipeline _lineGlowPipeline;
    private readonly Pipeline _meshPipeline;
    private readonly ResourceSet _drawSet;
    private readonly Shader[] _shaders;
    private readonly Shader[] _meshShaders;
    private readonly ResourceLayout _meshTextureSamplerLayout;
    /// <summary>Anisotropic + slight LOD bias so stretched DIST textures do not crawl/shimmer when far from the camera.</summary>
    private readonly Sampler _meshSampler;
    private readonly Texture _whiteFallbackTexture;
    private readonly TextureView _whiteFallbackView;
    /// <summary>Diffuse + linear sampler for meshes without a DIST texture (1×1 white).</summary>
    private readonly ResourceSet _meshDefaultDiffuseSet;

    private uint _width;
    private uint _height;
    // SSAA factor. 2.0 = 4× fragment-shading cost — too aggressive for an
    // editor that needs GPU headroom for lightmap baking, especially on dense
    // DIST scenes where thousands of textured + lit meshes overdraw. Native
    // resolution (1.0) renders fine on modern monitors; gizmo / wireframe
    // lines are still visible without supersampling. Bump to ~1.25 if jagged
    // edges become a real complaint — that's only 1.56× cost. Long term:
    // switch to hardware MSAA (Texture.SampleCount + resolve) which shades
    // only at triangle edges and costs roughly as much as 1.0 native.
    private const float RenderScale = 1.0f;
    private const bool EnableLineGlow = true;
    // Tuned to preserve per-object hues (avoid washing all lines toward white)
    // while still giving a visible halo/thickness boost.
    private const float LineGlowStrength = 0.5f;
    private const float LineGlowTintScale = 1.0f;
    private const float LineThicknessPixels = 2.0f;

    public Renderer3D(GraphicsDevice gd)
    {
        _gd = gd;
        ResourceFactory factory = gd.ResourceFactory;

        _drawBuffer = factory.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<DrawUniform>(),
            BufferUsage.UniformBuffer | BufferUsage.Dynamic));

        _gridBuffer        = UploadLines(factory, gd, BuildGrid(),          out _gridVertexCount);
        _cubeWireBuffer    = UploadLines(factory, gd, BuildUnitCubeWire(),  out _cubeWireVertexCount);
        _locatorBuffer     = UploadLines(factory, gd, BuildLocatorMarker(), out _locatorVertexCount);
        _signupIndicatorBuffer = UploadLines(factory, gd, BuildSignupIndicatorMarker(), out _signupIndicatorVertexCount);
        _chevronIndicatorBuffer = UploadLines(factory, gd, BuildChevronIndicatorMarker(), out _chevronIndicatorVertexCount);
        _gizmoArrowBuffer  = UploadLines(factory, gd, BuildGizmoArrow(),    out _gizmoArrowVertexCount);
        _navigatorBuffer   = UploadLines(factory, gd, BuildNavigator(),     out _navigatorVertexCount);
        _ringBuffer        = UploadLines(factory, gd, BuildRing(64),        out _ringVertexCount);
        _axisShaftBuffer   = UploadLines(factory, gd, BuildAxisShaft(),     out _axisShaftVertexCount);

        _shaders = factory.CreateFromSpirv(
            new ShaderDescription(ShaderStages.Vertex,   Encoding.UTF8.GetBytes(VertexShaderSource),   "main"),
            new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(FragmentShaderSource), "main"));

        VertexLayoutDescription vertexLayout = new(
            new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
            new VertexElementDescription("Color",    VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3));

        // Both stages read DrawBuffer: vertex consumes MVP for transform; fragment
        // reads Tint for shading. Without Fragment in the stage mask, the mesh
        // pipeline's Tint binds as zero and meshes render pure black.
        ResourceLayout drawLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("DrawBuffer", ResourceKind.UniformBuffer, ShaderStages.Vertex | ShaderStages.Fragment)));
        _drawSet = factory.CreateResourceSet(new ResourceSetDescription(drawLayout, _drawBuffer));

        _meshTextureSamplerLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("DiffuseTex", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
            new ResourceLayoutElementDescription("DiffuseSampler", ResourceKind.Sampler, ShaderStages.Fragment)));
        _meshSampler = factory.CreateSampler(new SamplerDescription(
            SamplerAddressMode.Wrap,
            SamplerAddressMode.Wrap,
            SamplerAddressMode.Wrap,
            // Anisotropic 16× — keeps detail crisp on grazing-angle surfaces
            // (long walls / floor tiles viewed at low pitch) while still
            // honoring the mip chain. Trilinear alone blurs too aggressively
            // when minified along one axis but not the other.
            SamplerFilter.Anisotropic,
            comparisonKind: null,
            maximumAnisotropy: 16,
            minimumLod: 0,
            maximumLod: 15,
            lodBias: 0,
            borderColor: SamplerBorderColor.TransparentBlack));

        _whiteFallbackTexture = factory.CreateTexture(TextureDescription.Texture2D(
            1, 1, mipLevels: 1, arrayLayers: 1,
            PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
        gd.UpdateTexture(_whiteFallbackTexture, new byte[] { 255, 255, 255, 255 }, 0, 0, 0, 1, 1, 1, 0, 0);
        _whiteFallbackView = factory.CreateTextureView(_whiteFallbackTexture);
        _meshDefaultDiffuseSet = factory.CreateResourceSet(new ResourceSetDescription(
            _meshTextureSamplerLayout, _whiteFallbackView, _meshSampler));

        // Create initial 1x1 framebuffer; will be resized on first frame.
        Resize(16, 16);

        _linePipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
        {
            BlendState = BlendStateDescription.SingleOverrideBlend,
            DepthStencilState = new DepthStencilStateDescription(
                depthTestEnabled: true, depthWriteEnabled: true, comparisonKind: ComparisonKind.LessEqual),
            RasterizerState = new RasterizerStateDescription(
                cullMode: FaceCullMode.None,
                fillMode: PolygonFillMode.Solid,
                frontFace: FrontFace.CounterClockwise,
                depthClipEnabled: true,
                scissorTestEnabled: false),
            PrimitiveTopology = PrimitiveTopology.LineList,
            ResourceLayouts = new[] { drawLayout },
            ShaderSet = new ShaderSetDescription(new[] { vertexLayout }, _shaders),
            Outputs = _framebuffer!.OutputDescription,
        });

        _lineGlowPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
        {
            BlendState = BlendStateDescription.SingleAlphaBlend,
            DepthStencilState = new DepthStencilStateDescription(
                depthTestEnabled: true, depthWriteEnabled: false, comparisonKind: ComparisonKind.LessEqual),
            RasterizerState = new RasterizerStateDescription(
                cullMode: FaceCullMode.None,
                fillMode: PolygonFillMode.Solid,
                frontFace: FrontFace.CounterClockwise,
                depthClipEnabled: true,
                scissorTestEnabled: false),
            PrimitiveTopology = PrimitiveTopology.LineList,
            ResourceLayouts = new[] { drawLayout },
            ShaderSet = new ShaderSetDescription(new[] { vertexLayout }, _shaders),
            Outputs = _framebuffer!.OutputDescription,
        });

        _meshShaders = factory.CreateFromSpirv(
            new ShaderDescription(ShaderStages.Vertex,   Encoding.UTF8.GetBytes(MeshVertexShaderSource),   "main"),
            new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(MeshFragmentShaderSource), "main"));

        VertexLayoutDescription meshLayout = new(
            new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
            new VertexElementDescription("Normal",   VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
            new VertexElementDescription("Texcoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2));

        _meshPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
        {
            // Opaque blend — meshes use an alpha-clip pattern in the shader
            // (discard < 0.5, output alpha 1.0). Previously SingleAlphaBlend
            // was used so anti-aliased cutout edges could fade — but combined
            // with depth-write that caused alpha-pixel depth to occlude
            // geometry behind the cutout (visible as "see-through to bg" on
            // foliage / decals / dirt patches). Standard alpha-test pattern
            // resolves both depth ordering and the visual hole at once.
            BlendState = BlendStateDescription.SingleOverrideBlend,
            DepthStencilState = new DepthStencilStateDescription(
                depthTestEnabled: true, depthWriteEnabled: true, comparisonKind: ComparisonKind.LessEqual),
            RasterizerState = new RasterizerStateDescription(
                cullMode: FaceCullMode.None,
                fillMode: PolygonFillMode.Solid,
                frontFace: FrontFace.CounterClockwise,
                depthClipEnabled: true,
                scissorTestEnabled: false),
            PrimitiveTopology = PrimitiveTopology.TriangleList,
            ResourceLayouts = new[] { drawLayout, _meshTextureSamplerLayout },
            ShaderSet = new ShaderSetDescription(new[] { meshLayout }, _meshShaders),
            Outputs = _framebuffer!.OutputDescription,
        });
    }

    /// Upload a mesh extracted from PSG (UV per vertex + optional diffuse BC texture from a GUID <c>.psg</c>).
    /// Caller must dispose the returned VB/IB (and GPU textures when <see cref="ImportedMesh.DiffuseTexture"/> is set).
    /// <paramref name="glbMaterialName"/> is set by the GLB import path so the
    /// viewport pick logic can map a mesh hit back to its
    /// <see cref="GlbMaterialAssignment"/>; null for PSF-loaded DIST meshes.
    public ImportedMesh UploadMesh(
        string name,
        string sourcePath,
        ReadOnlySpan<float> positionsXyz,
        ReadOnlySpan<float> normalsXyz,
        ReadOnlySpan<float> texcoordsUv,
        ReadOnlySpan<uint> indices,
        Texture? diffuseTexture,
        TextureView? diffuseView,
        string? glbMaterialName = null)
    {
        if (positionsXyz.Length % 3 != 0) throw new ArgumentException("positions length must be a multiple of 3");
        int vertexCount = positionsXyz.Length / 3;
        if (vertexCount == 0 || indices.Length == 0) throw new ArgumentException("empty mesh");

        bool hasNormals = normalsXyz.Length == positionsXyz.Length;
        bool hasUv = texcoordsUv.Length == vertexCount * 2;
        var verts = new VertexPositionNormalTexcoord[vertexCount];
        var bMin = new Vector3(float.PositiveInfinity);
        var bMax = new Vector3(float.NegativeInfinity);
        for (int i = 0; i < vertexCount; i++)
        {
            var p = new Vector3(positionsXyz[i * 3], positionsXyz[i * 3 + 1], positionsXyz[i * 3 + 2]);
            var n = hasNormals
                ? new Vector3(normalsXyz[i * 3], normalsXyz[i * 3 + 1], normalsXyz[i * 3 + 2])
                : Vector3.UnitY;
            var uv = hasUv
                ? new Vector2(texcoordsUv[i * 2], texcoordsUv[i * 2 + 1])
                : Vector2.Zero;
            verts[i] = new VertexPositionNormalTexcoord(p, n, uv);
            bMin = Vector3.Min(bMin, p);
            bMax = Vector3.Max(bMax, p);
        }

        ResourceFactory factory = _gd.ResourceFactory;
        var vb = factory.CreateBuffer(new BufferDescription((uint)(verts.Length * Marshal.SizeOf<VertexPositionNormalTexcoord>()), BufferUsage.VertexBuffer));
        _gd.UpdateBuffer(vb, 0, verts);

        var idx = indices.ToArray();
        var ib = factory.CreateBuffer(new BufferDescription((uint)(idx.Length * sizeof(uint)), BufferUsage.IndexBuffer));
        _gd.UpdateBuffer(ib, 0, idx);

        var cpuPositions = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            cpuPositions[i] = verts[i].Position;

        ResourceSet? diffuseSet = null;
        if (diffuseTexture != null && diffuseView != null)
            diffuseSet = factory.CreateResourceSet(new ResourceSetDescription(_meshTextureSamplerLayout, diffuseView, _meshSampler));

        return new ImportedMesh
        {
            Name = name,
            SourcePath = sourcePath,
            VertexCount = (uint)vertexCount,
            IndexCount = (uint)idx.Length,
            VertexBuffer = vb,
            IndexBuffer = ib,
            BoundsMin = bMin,
            BoundsMax = bMax,
            CpuPositions = cpuPositions,
            CpuIndices = idx,
            GlbMaterialName = glbMaterialName,
            DiffuseTexture = diffuseTexture,
            DiffuseTextureView = diffuseView,
            DiffuseSamplerSet = diffuseSet,
        };
    }

    public static void DisposeMesh(ImportedMesh m)
    {
        m.VertexBuffer.Dispose();
        m.IndexBuffer.Dispose();
        m.DiffuseSamplerSet?.Dispose();
        m.DiffuseTextureView?.Dispose();
        m.DiffuseTexture?.Dispose();
    }

    public IntPtr GetImGuiBinding(ImGuiRenderer imgui)
    {
        if (_imguiBinding == IntPtr.Zero && _colorTarget != null)
            _imguiBinding = imgui.GetOrCreateImGuiBinding(_gd.ResourceFactory, _colorTarget);
        return _imguiBinding;
    }

    public uint Width  => _width;
    public uint Height => _height;
    public float Aspect => _height == 0 ? 1f : (float)_width / _height;

    public void EnsureSize(uint width, uint height, ImGuiRenderer imgui)
    {
        width  = Math.Max(width, 16);
        height = Math.Max(height, 16);

        // Render to a supersampled off-screen target and let ImGui downsample it
        // into the viewport rectangle. This provides anti-aliased lines/edges
        // without requiring a separate MSAA resolve pipeline.
        uint scaledWidth  = Math.Max((uint)MathF.Ceiling(width * RenderScale), 16u);
        uint scaledHeight = Math.Max((uint)MathF.Ceiling(height * RenderScale), 16u);

        if (scaledWidth == _width && scaledHeight == _height) return;
        Resize(scaledWidth, scaledHeight);
        if (_imguiBinding != IntPtr.Zero && _colorTarget != null) imgui.RemoveImGuiBinding(_colorTarget);
        _imguiBinding = imgui.GetOrCreateImGuiBinding(_gd.ResourceFactory, _colorTarget!);
    }

    private void Resize(uint width, uint height)
    {
        _colorTarget?.Dispose();
        _depthTarget?.Dispose();
        _framebuffer?.Dispose();

        _width = width;
        _height = height;

        ResourceFactory factory = _gd.ResourceFactory;
        _colorTarget = factory.CreateTexture(TextureDescription.Texture2D(
            width, height, mipLevels: 1, arrayLayers: 1,
            PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.RenderTarget | TextureUsage.Sampled));
        _depthTarget = factory.CreateTexture(TextureDescription.Texture2D(
            width, height, mipLevels: 1, arrayLayers: 1,
            PixelFormat.R32_Float, TextureUsage.DepthStencil));
        _framebuffer = factory.CreateFramebuffer(new FramebufferDescription(_depthTarget, _colorTarget));
    }

    /// Tip cube size for the scale gizmo, in world units. Constant — does not scale with
    /// camera distance or object size.
    public static float GetHandleSize() => 0.15f;

    public void Render(
        CommandList cl, OrbitCamera camera, EditorScene scene, object? selected,
        EditorTool tool, GizmoAxis hoveredAxis, GizmoAxis activeAxis,
        Matrix4x4 gizmoOrientation, LocatorGizmoAccent locatorGizmoAccent = LocatorGizmoAccent.None)
    {
        if (_framebuffer is null || _width == 0 || _height == 0) return;

        // Huge Skate DISTs: float32 view×proj loses precision → depth shimmer between
        // stacked meshes. Use double-precision look-at + a wider near plane when coordinates
        // are large; bias mips more when there are many draws (30k+ submeshes).
        Vector3 eye = camera.Position;
        Vector3 target = camera.Target;
        float worldMag = MathF.Sqrt(MathF.Max(eye.LengthSquared(), target.LengthSquared()));

        float nearP = camera.NearPlane;
        if (worldMag > 6000f)
            nearP = MathF.Max(nearP, MathF.Min(50f, worldMag * 0.00014f));

        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(
            camera.FieldOfViewYRadians, Aspect, nearP, camera.FarPlane);

        // Always use the camera's own view matrix. The previous code toggled
        // per-frame between camera.GetViewMatrix() (right-handed
        // Matrix4x4.CreateLookAt, eye→eye+forward) and
        // LargeWorldMatrices.CreateLookAtLh (left-handed, eye→Target) once
        // worldMag crossed the 2500 threshold. Those two matrices use a
        // different handedness AND a different look target (Target is stale in
        // fly mode), so consecutive redrawn frames jumped between two
        // incompatible view solutions — that was the lighting shimmer. Using
        // the camera's matrix unconditionally removes the toggle (no flicker)
        // and is the basis the orbit/fly controls are calibrated against.
        Matrix4x4 view = camera.GetViewMatrix();

        Matrix4x4 viewProj = view * proj;

        float meshExtraTexBias =
            MathF.Min(worldMag / 4500f, 14f)
            + MathF.Min(scene.Meshes.Count / 4000f, 8f);

        cl.SetFramebuffer(_framebuffer);
        cl.SetFullViewports();
        cl.ClearColorTarget(0, new RgbaFloat(0.13f, 0.14f, 0.16f, 1f));
        cl.ClearDepthStencil(1f);

        // Imported PSG meshes — active DIST only (see EditorScene.Meshes). Other
        // DISTs still have meshes in memory after Open Scene; switch active in the tree to draw them.
        if (scene.Meshes.Count > 0)
        {
            cl.SetPipeline(_meshPipeline);
            Vector3 baseColor = new(0.62f, 0.68f, 0.74f);
            // Blender-style selection highlight: warm orange tint multiplied
            // onto the diffuse sample. Triggered when the inspector selection
            // is a GlbMaterialAssignment — every mesh in the active map that
            // shares the material name lights up so the user can see the
            // whole material grouping at once. Tint is well over 1.0 so the
            // shader's tex * tint saturates against the framebuffer and the
            // selected meshes pop visibly even against bright textures.
            string? selectedMaterial = (selected as GlbMaterialAssignment)?.MaterialName;
            Vector3 selectedTint = new(1.8f, 1.1f, 0.45f);
            foreach (var mesh in scene.Meshes)
            {
                bool isSelected = selectedMaterial != null
                    && string.Equals(mesh.GlbMaterialName, selectedMaterial, StringComparison.Ordinal);
                Vector3 tint = isSelected ? selectedTint : baseColor;
                SetDraw(cl, viewProj, view, tint, default, 1f, meshExtraTexBias);
                cl.SetGraphicsResourceSet(0, _drawSet);
                cl.SetGraphicsResourceSet(1, mesh.DiffuseSamplerSet ?? _meshDefaultDiffuseSet);
                cl.SetVertexBuffer(0, mesh.VertexBuffer);
                cl.SetIndexBuffer(mesh.IndexBuffer, IndexFormat.UInt32);
                cl.DrawIndexed(mesh.IndexCount);
            }
        }

        // Grid + world axes — identity model, full-strength tint. Hidden once the
        // user imports a DIST: the grid is just a sandbox at origin and clutters
        // the viewport over real world geometry.
        if (scene.Meshes.Count == 0)
        {
            DrawLineWithEffects(cl, _gridBuffer, _gridVertexCount, viewProj, view, Vector3.One);
        }

        // Trigger volumes — wireframe boxes.
        Vector3 volumeColor         = new(0.36f, 0.72f, 1.00f);  // soft cyan
        Vector3 volumeColorSelected = new(1.00f, 0.78f, 0.26f);  // amber
        foreach (TriggerVolume v in scene.TriggerVolumes)
        {
            Matrix4x4 model = ModelMatrix(v.Center, v.HalfExtents, v.RotationDegrees);
            Vector3 tint = ReferenceEquals(selected, v) ? volumeColorSelected : volumeColor;
            DrawLineWithEffects(cl, _cubeWireBuffer, _cubeWireVertexCount, model * viewProj, view, tint);
        }

        // Locators:
        //  - Regular locators use the classic XYZ marker.
        //  - Ribbon-arrow locators (world + in-challenge) use a billboard-like cyan arrow.
        //  - Chevron locators use the same mesh with an amber tint.
        var ribbonArrowVisualIds = new HashSet<Guid>();
        var chevronVisualIds = new HashSet<Guid>();
        foreach (Challenge c in scene.Challenges)
        {
            if (c.VisualSignupLocatorId is Guid rid)
                ribbonArrowVisualIds.Add(rid);
            foreach (Guid id in c.InChallengeRibbonArrowLocatorIds)
                ribbonArrowVisualIds.Add(id);
            foreach (Guid id in c.ChevronLocatorIds)
                chevronVisualIds.Add(id);
        }

        foreach (Locator l in scene.Locators)
        {
            if (ribbonArrowVisualIds.Contains(l.Id) || chevronVisualIds.Contains(l.Id))
                continue;

            Matrix4x4 model = ModelMatrix(l.Position, Vector3.One, l.RotationDegrees);
            Vector3 tint = ReferenceEquals(selected, l) ? new Vector3(1.7f, 1.4f, 0.5f) : Vector3.One;
            DrawLineWithEffects(cl, _locatorBuffer, _locatorVertexCount, model * viewProj, view, tint);
        }

        Vector3 ribbonScale = new(2f, 2f, 2f);
        foreach (Locator l in scene.Locators)
        {
            if (!ribbonArrowVisualIds.Contains(l.Id))
                continue;

            Matrix4x4 model = ModelMatrix(l.Position, ribbonScale, l.RotationDegrees);
            Vector3 tint = ReferenceEquals(selected, l)
                ? new Vector3(1.8f, 2.0f, 1.0f)
                : new Vector3(0.2f, 1.35f, 2.5f);
            DrawLineWithEffects(cl, _signupIndicatorBuffer, _signupIndicatorVertexCount, model * viewProj, view, tint);
        }

        foreach (Locator l in scene.Locators)
        {
            if (!chevronVisualIds.Contains(l.Id))
                continue;

            Matrix4x4 model = ModelMatrix(l.Position, ribbonScale, l.RotationDegrees);
            Vector3 tint = ReferenceEquals(selected, l)
                ? new Vector3(1.4f, 2.2f, 3.0f)
                : new Vector3(0.25f, 1.45f, 2.9f);
            DrawLineWithEffects(cl, _chevronIndicatorBuffer, _chevronIndicatorVertexCount, model * viewProj, view, tint);
        }

        // Gizmo for the selected object (when its tool is active). Drawn last so it overlays
        // the object outline; cleared depth so it's never occluded.
        if (selected is not null && tool != EditorTool.Select)
        {
            Vector3 origin = selected switch
            {
                TriggerVolume vt => vt.Center,
                Locator lc       => lc.Position,
                _                => Vector3.Zero,
            };
            if (origin != Vector3.Zero || selected is TriggerVolume || selected is Locator)
            {
                cl.ClearDepthStencil(1f);
                float size = GetGizmoSize();
                switch (tool)
                {
                    case EditorTool.Move:
                        DrawMoveGizmo(cl, view, viewProj, origin, size, hoveredAxis, activeAxis, gizmoOrientation, locatorGizmoAccent);
                        break;
                    case EditorTool.Rotate:
                        DrawRotateGizmo(cl, view, viewProj, origin, size, hoveredAxis, activeAxis, gizmoOrientation, locatorGizmoAccent);
                        break;
                    case EditorTool.Scale:
                        DrawScaleGizmo(cl, view, viewProj, origin, size, hoveredAxis, activeAxis, gizmoOrientation, locatorGizmoAccent);
                        break;
                }
            }
        }

        // Navigation gizmo overlay — small XYZ axes in the top-right that rotate with the camera.
        DrawNavigator(cl, camera);
    }

    private void DrawRotateGizmo(
        CommandList cl, Matrix4x4 view, Matrix4x4 viewProj, Vector3 origin, float size,
        GizmoAxis hovered, GizmoAxis active, Matrix4x4 spaceOrientation,
        LocatorGizmoAccent accent = LocatorGizmoAccent.None)
    {
        DrawRing(GizmoAxis.X, Matrix4x4.CreateRotationZ(-MathF.PI / 2f), new Vector3(1, 0.30f, 0.30f));
        DrawRing(GizmoAxis.Y, Matrix4x4.Identity,                         new Vector3(0.35f, 1, 0.35f));
        DrawRing(GizmoAxis.Z, Matrix4x4.CreateRotationX(-MathF.PI / 2f), new Vector3(0.45f, 0.65f, 1));

        void DrawRing(GizmoAxis axis, Matrix4x4 rot, Vector3 baseColor)
        {
            Vector3 tinted = TintGizmoAxis(baseColor, accent);
            Vector3 c = tinted;
            if (active == axis)        c = new Vector3(1.7f, 1.7f, 0.6f);
            else if (hovered == axis)  c = tinted * 1.6f;

            Matrix4x4 model =
                Matrix4x4.CreateScale(size) * rot * spaceOrientation * Matrix4x4.CreateTranslation(origin);
            DrawLineWithEffects(cl, _ringBuffer, _ringVertexCount, model * viewProj, view, c);
        }
    }

    private void DrawScaleGizmo(
        CommandList cl, Matrix4x4 view, Matrix4x4 viewProj, Vector3 origin, float size,
        GizmoAxis hovered, GizmoAxis active, Matrix4x4 spaceOrientation,
        LocatorGizmoAccent accent = LocatorGizmoAccent.None)
    {
        // Tip cube uses a depth-based size so the handle stays roughly constant in screen pixels,
        // independent of the underlying object's HalfExtents.
        float tipSize = GetHandleSize();

        DrawShaftAndTip(GizmoAxis.X, Matrix4x4.CreateRotationY(MathF.PI / 2f), new Vector3(1, 0.30f, 0.30f));
        DrawShaftAndTip(GizmoAxis.Y, Matrix4x4.CreateRotationX(-MathF.PI / 2f), new Vector3(0.35f, 1, 0.35f));
        DrawShaftAndTip(GizmoAxis.Z, Matrix4x4.Identity,                       new Vector3(0.45f, 0.65f, 1));

        void DrawShaftAndTip(GizmoAxis axis, Matrix4x4 rot, Vector3 baseColor)
        {
            Vector3 tinted = TintGizmoAxis(baseColor, accent);
            Vector3 c = tinted;
            if (active == axis)        c = new Vector3(1.7f, 1.7f, 0.6f);
            else if (hovered == axis)  c = tinted * 1.6f;

            // Shaft — per-axis rotation first, then space orientation, then translate.
            Matrix4x4 shaftModel =
                Matrix4x4.CreateScale(size) * rot * spaceOrientation * Matrix4x4.CreateTranslation(origin);
            DrawLineWithEffects(cl, _axisShaftBuffer, _axisShaftVertexCount, shaftModel * viewProj, view, c);

            // Tip cube — parked at the axis tip in whatever space (world or local) the
            // shaft is drawn in, so picking + visual stay aligned.
            Vector3 baseAxis = axis switch
            {
                GizmoAxis.X => Vector3.UnitX,
                GizmoAxis.Y => Vector3.UnitY,
                GizmoAxis.Z => Vector3.UnitZ,
                _           => Vector3.Zero,
            };
            Vector3 axisDir = Vector3.TransformNormal(baseAxis, spaceOrientation);
            if (axisDir.LengthSquared() < 1e-6f) axisDir = baseAxis;
            else axisDir = Vector3.Normalize(axisDir);
            Vector3 tipCentre = origin + axisDir * size;
            Matrix4x4 tipModel =
                Matrix4x4.CreateScale(tipSize) * Matrix4x4.CreateTranslation(tipCentre);
            DrawLineWithEffects(cl, _cubeWireBuffer, _cubeWireVertexCount, tipModel * viewProj, view, c);
        }
    }

    private void DrawNavigator(CommandList cl, OrbitCamera camera)
    {
        const float size = 100f;
        const float padding = 12f;
        if (_width < size + padding * 2 || _height < size + padding * 2) return;

        Viewport navVp = new(_width - size - padding, padding, size, size, 0f, 1f);
        cl.SetViewport(0, ref navVp);
        cl.ClearDepthStencil(1f);

        // View matrix with translation zeroed — keeps the camera's rotation but parks the axes
        // at the centre of the small viewport regardless of camera position.
        Matrix4x4 view = camera.GetViewMatrix();
        view.M41 = 0; view.M42 = 0; view.M43 = 0;

        // Orthographic projection so axes don't perspective-shrink with depth.
        Matrix4x4 proj = Matrix4x4.CreateOrthographic(2.6f, 2.6f, -10f, 10f);

        DrawLineWithEffects(cl, _navigatorBuffer, _navigatorVertexCount, view * proj, view, Vector3.One);

        // Restore main viewport so the next frame's main scene draws fill the framebuffer.
        Viewport fullVp = new(0, 0, _width, _height, 0f, 1f);
        cl.SetViewport(0, ref fullVp);
    }

    /// Gizmo size in world units. Constant — independent of camera distance and object size.
    /// Larger when zoomed in, smaller when zoomed out (because perspective).
    public static float GetGizmoSize() => 2.0f;

    private static Vector3 TintGizmoAxis(Vector3 baseColor, LocatorGizmoAccent accent)
    {
        return accent switch
        {
            LocatorGizmoAccent.RibbonArrow => baseColor * new Vector3(0.72f, 1.12f, 1.28f),
            LocatorGizmoAccent.Chevron => baseColor * new Vector3(1.38f, 1.02f, 0.58f),
            _ => baseColor,
        };
    }

    private void DrawMoveGizmo(
        CommandList cl, Matrix4x4 view, Matrix4x4 viewProj, Vector3 origin, float size,
        GizmoAxis hovered, GizmoAxis active, Matrix4x4 spaceOrientation,
        LocatorGizmoAccent accent = LocatorGizmoAccent.None)
    {
        DrawAxis(GizmoAxis.X, new Vector3(1, 0, 0), new Vector3(1, 0.30f, 0.30f));
        DrawAxis(GizmoAxis.Y, new Vector3(0, 1, 0), new Vector3(0.35f, 1, 0.35f));
        DrawAxis(GizmoAxis.Z, new Vector3(0, 0, 1), new Vector3(0.45f, 0.65f, 1));

        void DrawAxis(GizmoAxis axis, Vector3 dir, Vector3 baseColor)
        {
            // Highlight: brighten when hovered, brightest when actively dragging
            Vector3 tinted = TintGizmoAxis(baseColor, accent);
            Vector3 c = tinted;
            if (active == axis)        c = new Vector3(1.7f, 1.7f, 0.6f);
            else if (hovered == axis)  c = tinted * 1.6f;

            // Orient the unit +Z arrow to face the requested axis direction.
            // (0,0,1) rotated by RotY(+π/2) → (+1,0,0); by RotX(-π/2) → (0,+1,0); identity stays (0,0,1).
            Matrix4x4 rot = Matrix4x4.Identity;
            if (axis == GizmoAxis.X) rot = Matrix4x4.CreateRotationY(MathF.PI / 2f);
            else if (axis == GizmoAxis.Y) rot = Matrix4x4.CreateRotationX(-MathF.PI / 2f);
            // Z is already +Z

            // spaceOrientation is identity in World mode, the selected object's rotation
            // matrix in Local mode. Multiplied AFTER the per-axis rotation so each arrow
            // first becomes its world-X/Y/Z direction, then gets rotated into local space.
            Matrix4x4 model =
                Matrix4x4.CreateScale(size) * rot * spaceOrientation * Matrix4x4.CreateTranslation(origin);
            DrawLineWithEffects(cl, _gizmoArrowBuffer, _gizmoArrowVertexCount, model * viewProj, view, c);
        }
    }

    private void DrawLineWithEffects(
        CommandList cl, DeviceBuffer vb, uint vertexCount, Matrix4x4 mvp, Matrix4x4 worldView, Vector3 tint)
    {
        cl.SetPipeline(_linePipeline);
        cl.SetGraphicsResourceSet(0, _drawSet);
        cl.SetVertexBuffer(0, vb);
        SetDraw(cl, mvp, worldView, tint);
        cl.Draw(vertexCount);

        if (!EnableLineGlow || _width == 0 || _height == 0)
            return;

        cl.SetPipeline(_lineGlowPipeline);
        cl.SetGraphicsResourceSet(0, _drawSet);
        cl.SetVertexBuffer(0, vb);

        float dx = LineThicknessPixels / _width;
        float dy = LineThicknessPixels / _height;
        Vector2[] offsets =
        {
            new(+dx, 0f), new(-dx, 0f), new(0f, +dy), new(0f, -dy),
            new(+dx, +dy), new(-dx, +dy), new(+dx, -dy), new(-dx, -dy),
        };
        Vector3 glowTint = Vector3.Clamp(tint * LineGlowTintScale, Vector3.Zero, Vector3.One);
        foreach (Vector2 offset in offsets)
        {
            SetDraw(cl, mvp, worldView, glowTint, offset, LineGlowStrength);
            cl.Draw(vertexCount);
        }
    }

    private void SetDraw(
        CommandList cl, Matrix4x4 mvp, Matrix4x4 worldView, Vector3 tint,
        Vector2 clipOffset = default, float intensity = 1f, float textureBiasExtra = 0f)
    {
        cl.UpdateBuffer(_drawBuffer, 0, new DrawUniform
        {
            MVP = mvp,
            View = worldView,
            Tint = new Vector4(tint, 1f),
            Params = new Vector4(clipOffset.X, clipOffset.Y, intensity, textureBiasExtra),
        });
    }

    private static Matrix4x4 ModelMatrix(Vector3 translation, Vector3 scale, Vector3 rotationDegrees)
    {
        float pitch = rotationDegrees.X * MathF.PI / 180f;
        float yaw   = rotationDegrees.Y * MathF.PI / 180f;
        float roll  = rotationDegrees.Z * MathF.PI / 180f;
        return Matrix4x4.CreateScale(scale)
             * Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll)
             * Matrix4x4.CreateTranslation(translation);
    }

    public void Dispose()
    {
        _colorTarget?.Dispose();
        _depthTarget?.Dispose();
        _framebuffer?.Dispose();
        _gridBuffer.Dispose();
        _cubeWireBuffer.Dispose();
        _locatorBuffer.Dispose();
        _signupIndicatorBuffer.Dispose();
        _chevronIndicatorBuffer.Dispose();
        _gizmoArrowBuffer.Dispose();
        _navigatorBuffer.Dispose();
        _ringBuffer.Dispose();
        _axisShaftBuffer.Dispose();
        _drawBuffer.Dispose();
        _linePipeline.Dispose();
        _lineGlowPipeline.Dispose();
        _meshPipeline.Dispose();
        _drawSet.Dispose();
        _meshDefaultDiffuseSet.Dispose();
        _whiteFallbackView.Dispose();
        _whiteFallbackTexture.Dispose();
        _meshSampler.Dispose();
        _meshTextureSamplerLayout.Dispose();
        foreach (Shader s in _shaders) s.Dispose();
        foreach (Shader s in _meshShaders) s.Dispose();
    }

    private static DeviceBuffer UploadLines(ResourceFactory factory, GraphicsDevice gd, VertexPositionColor[] lines, out uint count)
    {
        DeviceBuffer buf = factory.CreateBuffer(new BufferDescription(
            VertexPositionColor.SizeBytes * (uint)lines.Length, BufferUsage.VertexBuffer));
        gd.UpdateBuffer(buf, 0, lines);
        count = (uint)lines.Length;
        return buf;
    }

    private static VertexPositionColor[] BuildGrid()
    {
        const int half = 50;
        Vector3 minor = new(0.30f, 0.30f, 0.32f);
        Vector3 major = new(0.45f, 0.45f, 0.50f);

        List<VertexPositionColor> v = new(capacity: (half * 2 + 1) * 4);
        for (int i = -half; i <= half; i++)
        {
            Vector3 c = (i % 10 == 0) ? major : minor;
            v.Add(new VertexPositionColor(new Vector3(i, 0, -half), c));
            v.Add(new VertexPositionColor(new Vector3(i, 0,  half), c));
            v.Add(new VertexPositionColor(new Vector3(-half, 0, i), c));
            v.Add(new VertexPositionColor(new Vector3( half, 0, i), c));
        }
        return v.ToArray();
    }

    /// Small XYZ cross drawn in the top-right corner overlay. Each axis goes from -1 to +1
    /// with a brighter "+" half so the user can read which way each axis is pointing.
    private static VertexPositionColor[] BuildNavigator()
    {
        Vector3 rPos = new(1f,  0.30f, 0.30f);
        Vector3 rNeg = new(0.45f, 0.18f, 0.18f);
        Vector3 gPos = new(0.30f, 1f, 0.30f);
        Vector3 gNeg = new(0.18f, 0.45f, 0.18f);
        Vector3 bPos = new(0.40f, 0.65f, 1f);
        Vector3 bNeg = new(0.18f, 0.30f, 0.55f);

        return new[]
        {
            new VertexPositionColor(Vector3.Zero, rPos), new VertexPositionColor( Vector3.UnitX, rPos),
            new VertexPositionColor(Vector3.Zero, rNeg), new VertexPositionColor(-Vector3.UnitX, rNeg),
            new VertexPositionColor(Vector3.Zero, gPos), new VertexPositionColor( Vector3.UnitY, gPos),
            new VertexPositionColor(Vector3.Zero, gNeg), new VertexPositionColor(-Vector3.UnitY, gNeg),
            new VertexPositionColor(Vector3.Zero, bPos), new VertexPositionColor( Vector3.UnitZ, bPos),
            new VertexPositionColor(Vector3.Zero, bNeg), new VertexPositionColor(-Vector3.UnitZ, bNeg),
        };
    }

    /// 24-vertex unit-cube wireframe in pure white (so the per-draw tint controls colour).
    /// Goes from (-1,-1,-1) to (+1,+1,+1) — a TriggerVolume's HalfExtents is its scale.
    private static VertexPositionColor[] BuildUnitCubeWire()
    {
        Vector3 c = Vector3.One;
        Vector3 P(int x, int y, int z) => new(x, y, z);
        VertexPositionColor V(Vector3 p) => new(p, c);

        Vector3[] corners =
        {
            P(-1,-1,-1), P( 1,-1,-1), P( 1,-1, 1), P(-1,-1, 1),
            P(-1, 1,-1), P( 1, 1,-1), P( 1, 1, 1), P(-1, 1, 1),
        };
        int[] e = {
            0,1, 1,2, 2,3, 3,0,   // bottom
            4,5, 5,6, 6,7, 7,4,   // top
            0,4, 1,5, 2,6, 3,7,   // verticals
        };
        VertexPositionColor[] result = new VertexPositionColor[e.Length];
        for (int i = 0; i < e.Length; i++) result[i] = V(corners[e[i]]);
        return result;
    }

    /// Locator marker: 3-axis cross (X red, Y green, Z blue) plus a forward arrow on +Z.
    /// Designed to sit at the locator's origin and rotate with its yaw.
    private static VertexPositionColor[] BuildLocatorMarker()
    {
        const float r = 0.5f;
        Vector3 red   = new(1f, 0.30f, 0.30f);
        Vector3 green = new(0.30f, 1f, 0.30f);
        Vector3 blue  = new(0.40f, 0.60f, 1f);
        Vector3 white = new(0.95f, 0.95f, 0.95f);

        List<VertexPositionColor> v = new(14);

        // 3-axis cross
        v.Add(new(new(-r, 0, 0), red));   v.Add(new(new( r, 0, 0), red));
        v.Add(new(new( 0,-r, 0), green)); v.Add(new(new( 0, r, 0), green));
        v.Add(new(new( 0, 0,-r), blue));  v.Add(new(new( 0, 0, r), blue));

        // Forward arrow on +Z (yaw=0 faces +Z): two short lines forming a < shape at the tip
        Vector3 tip   = new(0, 0, r);
        Vector3 wingL = new(-0.15f, 0, r - 0.25f);
        Vector3 wingR = new( 0.15f, 0, r - 0.25f);
        v.Add(new(tip, white));   v.Add(new(wingL, white));
        v.Add(new(tip, white));   v.Add(new(wingR, white));

        // Small upward stub so vertical orientation is visually obvious
        v.Add(new(new(0, 0, 0), white));
        v.Add(new(new(0, 0.25f, 0), white));

        return v.ToArray();
    }

    /// Signup visual indicator marker (neon-outline style) used for
    /// challenge visual signup locators. The shape is authored in local XY:
    /// +Y up, -Y down. During render we yaw-billboard it toward camera around
    /// world Y only, so the marker always stays "pointing down."
    private static VertexPositionColor[] BuildSignupIndicatorMarker()
    {
        Vector3 c = Vector3.One;
        var v = new List<VertexPositionColor>(48);
        void L(float x1, float y1, float x2, float y2)
        {
            v.Add(new VertexPositionColor(new Vector3(x1, y1, 0f), c));
            v.Add(new VertexPositionColor(new Vector3(x2, y2, 0f), c));
        }

        // Exact user-provided draw order, uniformly scaled for viewport marker size.
        const float S = 0.12f;
        Vector2 P(float x, float y) => new(x * S, y * S);

        Vector2 n075_7  = P(-0.75f, 7f);
        Vector2 n075_2  = P(-0.75f, 2f);
        Vector2 n2_375  = P(-2f, 3.75f);
        Vector2 n2_2    = P(-2f, 2f);
        Vector2 n075_0  = P(-0.75f, 0f);
        Vector2 p075_0  = P(0.75f, 0f);
        Vector2 p2_2    = P(2f, 2f);
        Vector2 p2_375  = P(2f, 3.75f);
        Vector2 p075_2  = P(0.75f, 2f);
        Vector2 p075_7  = P(0.75f, 7f);

        // Left path.
        L(n075_7.X, n075_7.Y, n075_2.X, n075_2.Y);
        L(n075_2.X, n075_2.Y, n2_375.X, n2_375.Y);
        L(n2_375.X, n2_375.Y, n2_2.X, n2_2.Y);
        L(n2_2.X, n2_2.Y, n075_0.X, n075_0.Y);

        // Mirror path on the right ("back the same way but positives").
        L(n075_0.X, n075_0.Y, p075_0.X, p075_0.Y);
        L(p075_0.X, p075_0.Y, p2_2.X, p2_2.Y);
        L(p2_2.X, p2_2.Y, p2_375.X, p2_375.Y);
        L(p2_375.X, p2_375.Y, p075_2.X, p075_2.Y);
        L(p075_2.X, p075_2.Y, p075_7.X, p075_7.Y);

        // Final top close.
        L(p075_7.X, p075_7.Y, n075_7.X, n075_7.Y);

        return v.ToArray();
    }

    /// Chevron locator marker: a single 6-point outline (hex-like chevron body),
    /// so each locator renders one clean shape.
    private static VertexPositionColor[] BuildChevronIndicatorMarker()
    {
        Vector3 c = Vector3.One;
        var v = new List<VertexPositionColor>(12);
        void L(float x1, float y1, float x2, float y2)
        {
            v.Add(new VertexPositionColor(new Vector3(x1, y1, 0f), c));
            v.Add(new VertexPositionColor(new Vector3(x2, y2, 0f), c));
        }

        const float S = 0.269f;
        Vector2 P(float x, float y) => new(x * S, y * S);

        // Mirrored points, recentered so the locator origin is at glyph center.
        // Original mirrored set: (1,0) (2,2) (1,4) (0,4) (1,2) (0,0)
        // Center offset = (1,2), so we subtract (1,2) from each point.
        Vector2 p0 = P(0f, -2f);
        Vector2 p1 = P(1f,  0f);
        Vector2 p2 = P(0f,  2f);
        Vector2 p3 = P(-1f, 2f);
        Vector2 p4 = P(0f,  0f);
        Vector2 p5 = P(-1f,-2f);

        L(p0.X, p0.Y, p1.X, p1.Y);
        L(p1.X, p1.Y, p2.X, p2.Y);
        L(p2.X, p2.Y, p3.X, p3.Y);
        L(p3.X, p3.Y, p4.X, p4.Y);
        L(p4.X, p4.Y, p5.X, p5.Y);
        L(p5.X, p5.Y, p0.X, p0.Y);

        return v.ToArray();
    }

    /// Unit ring in the XZ plane (centered at origin, radius 1). White vertex colour;
    /// per-draw tint paints the axis colour. Caller rotates the model matrix to orient
    /// the ring around the desired axis.
    private static VertexPositionColor[] BuildRing(int segments)
    {
        Vector3 c = Vector3.One;
        var v = new VertexPositionColor[segments * 2];
        for (int i = 0; i < segments; i++)
        {
            float a0 = (float)i       * MathF.PI * 2f / segments;
            float a1 = (float)(i + 1) * MathF.PI * 2f / segments;
            v[i * 2]     = new VertexPositionColor(new Vector3(MathF.Cos(a0), 0, MathF.Sin(a0)), c);
            v[i * 2 + 1] = new VertexPositionColor(new Vector3(MathF.Cos(a1), 0, MathF.Sin(a1)), c);
        }
        return v;
    }

    /// Plain axis line from origin to +Z, length 1 (caller scales).
    /// Colour comes from per-draw tint.
    private static VertexPositionColor[] BuildAxisShaft()
    {
        Vector3 c = Vector3.One;
        return new[]
        {
            new VertexPositionColor(Vector3.Zero, c),
            new VertexPositionColor(Vector3.UnitZ, c),
        };
    }

    /// Move-gizmo arrow shape, oriented along +Z, length 1 (caller scales to taste).
    /// White vertex colour — actual axis colour comes from the per-draw tint.
    private static VertexPositionColor[] BuildGizmoArrow()
    {
        Vector3 c = Vector3.One;
        List<VertexPositionColor> v = new(8);

        // Shaft
        v.Add(new(new(0, 0, 0), c));
        v.Add(new(new(0, 0, 1), c));

        // Arrowhead — three small lines forming a cone hint at the tip.
        const float headStart = 0.85f;
        const float headWidth = 0.06f;
        v.Add(new(new(0, 0, 1), c));   v.Add(new(new( headWidth, 0, headStart), c));
        v.Add(new(new(0, 0, 1), c));   v.Add(new(new(-headWidth, 0, headStart), c));
        v.Add(new(new(0, 0, 1), c));   v.Add(new(new(0,  headWidth, headStart), c));
        v.Add(new(new(0, 0, 1), c));   v.Add(new(new(0, -headWidth, headStart), c));

        return v.ToArray();
    }

}
