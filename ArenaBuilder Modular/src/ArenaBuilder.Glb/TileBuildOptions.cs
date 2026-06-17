using ArenaBuilder.Core.Platforms.Common.PsgFormat;
using ArenaBuilder.NavPower;

namespace ArenaBuilder.Glb;

/// <summary>
/// Configuration for tile-based streaming output. Layout:
/// <list type="bullet">
///   <item><c>cPres_U_V_high</c> — mesh PSGs for the cPres tile, plus full-resolution texture
///   PSGs for any texture used by exactly one cPres tile (its owning tile).</item>
///   <item><c>cPres_Global</c> — full-resolution texture PSGs for any texture shared by two or
///   more cPres tiles (promoted by <see cref="ArenaBuilder.Build.TexturePlacementPlanner"/>).</item>
///   <item><c>cSim_U_V_high</c> — collision data for the cSim tile.</item>
/// </list>
/// Mesh material channels reference the FULL GUID <c>G</c>; the engine resolves it via
/// <c>cPres_Global</c> (always resident) or the local cPres tile (always resident in the 3×3 window).
/// In <see cref="GlobalOnly"/> mode, all presentation (meshes + textures) lands in
/// <c>cPres_Global</c> and collision in <c>cSim_Global</c>.
/// </summary>
public sealed record TileBuildOptions
{
    public float TileSize { get; init; } = WorldTileGrid.DefaultTileSize;
    public float OriginX { get; init; } = WorldTileGrid.DefaultOriginX;
    public float OriginY { get; init; } = WorldTileGrid.DefaultOriginY;
    public string TileSuffix { get; init; } = "high";

    /// <summary>
    /// Target console for all emitted PSG/arena files. PS3 writes <c>.psg</c> (magic "ps3");
    /// Xbox 360 writes <c>.rx2</c> (magic "xb2"). Mesh + texture use the X360-specific composers;
    /// collision / AIPath / irradiance / NavPower / WorldPainter are cross-platform-clean and only
    /// differ in the arena header the writer emits.
    /// </summary>
    public ArenaPlatform TargetPlatform { get; init; } = ArenaPlatform.Ps3;

    /// <summary>File extension for the target platform: <c>.psg</c> (PS3) or <c>.rx2</c> (Xbox 360).</summary>
    public string PsgExtension => TargetPlatform == ArenaPlatform.Xbox360 ? ".rx2" : ".psg";

    /// <summary>
    /// Safety cap: maximum number of tiles a single triangle is allowed to cover.
    /// Prevents runaway output from corrupt or wildly scaled geometry.
    /// </summary>
    public int MaxTilesPerTriangle { get; init; } = 4096;

    /// <summary>
    /// Safety cap: reject tile indices outside this absolute range.
    /// Prevents runaway folder creation when bad transforms/scales push geometry far away.
    /// </summary>
    public int MaxAbsoluteTileIndex { get; init; } = 2048;

    /// <summary>
    /// Optional safety cap: maximum PSG outputs in one build. <c>0</c> = unlimited (no cap).
    /// Large arena builds (10k+ PSGs) need unlimited output; a positive value aborts the build
    /// when the count is exceeded.
    /// </summary>
    public int MaxOutputPsgFiles { get; init; } = 0;

    /// <summary>
    /// Maximum mesh parts (materials) per mesh PSG. Real game cPres PSGs use ~29–30.
    /// Parts from multiple GLBs are merged into one PSG up to this limit.
    /// </summary>
    public int MaxMeshesPerPsg { get; init; } = 30;

    /// <summary>
    /// Soft cap for total unique vertices packed into one render mesh PSG.
    /// Oversized tile output is split into additional mesh PSGs before writing.
    /// </summary>
    public int MaxVerticesPerMeshPsg { get; init; } = 30000;

    /// <summary>
    /// cPres prefix for presentation (mesh) tiles.
    /// </summary>
    public const string CPresPrefix = "cPres";

    /// <summary>
    /// cSim prefix for simulation (collision) tiles. No global folder for cSim in tiled mode.
    /// </summary>
    public const string CSimPrefix = "cSim";

    /// <summary>
    /// cPres global folder used by <see cref="GlobalOnly"/> (no tiles) and by the per-build
    /// shared-texture collection. Holds both mesh PSGs (in GlobalOnly) and texture PSGs.
    /// </summary>
    public const string CPresGlobalFolder = "cPres_Global";

    /// <summary>
    /// cSim global folder for a single collision output when not using tiles.
    /// </summary>
    public const string CSimGlobalFolder = "cSim_Global";

    /// <summary>
    /// When true, do not slice into tiles: presentation output (mesh + textures) goes to <see cref="CPresGlobalFolder"/>
    /// and collision goes to <see cref="CSimGlobalFolder"/>. Can be combined with <see cref="CPresOnly"/>.
    /// </summary>
    public bool GlobalOnly { get; init; }

    /// <summary>
    /// When true, build only mesh + texture PSGs (no collision).
    /// With <see cref="GlobalOnly"/>, only <see cref="CPresGlobalFolder"/> is created.
    /// </summary>
    public bool CPresOnly { get; init; }

    /// <summary>
    /// When true, build only collision PSGs (no cPres mesh / texture output).
    /// With <see cref="GlobalOnly"/>, only <see cref="CSimGlobalFolder"/> is created.
    /// </summary>
    public bool CSimOnly { get; init; }

    /// <summary>
    /// Optional suffix appended to every cPres/cSim folder name (e.g. "_proxy" for proxy builds).
    /// When set, folders are named like cPres_50_50_high_proxy, cPres_Global_proxy.
    /// Typically set when the DIST/map name contains "_Proxy".
    /// Note: only the cPres stream type has a dedicated <c>_proxy</c> suffix in the engine
    /// (<c>cPres_%d_%d_high_proxy</c> at <c>0x8229afb4</c>); cSim proxy folders are a builder convention.
    /// </summary>
    public string FolderSuffix { get; init; } = "";

    /// <summary>
    /// Normal map synth strength for auto-generated normals (when a normal texture is missing).
    /// </summary>
    public float? NormalSynthStrength { get; init; }

    /// <summary>
    /// Normal map synth level for auto-generated normals (when a normal texture is missing).
    /// </summary>
    public float? NormalSynthLevel { get; init; }

    /// <summary>
    /// Normal map synth blur/sharp amount for auto-generated normals (when a normal texture is missing).
    /// </summary>
    public float? NormalSynthBlurSharp { get; init; }

    /// <summary>
    /// When true (and not <see cref="CPresOnly"/>), emit one Skate-style NavPower PSG per collision tile beside collision/WP
    /// (<c>tNavPowerData</c> + BabelFlux legacy v23 graph). Uses coarse XZ bucketing of collision triangles as placeholder areas.
    /// </summary>
    public bool EmitNavPower { get; init; }

    /// <summary>
    /// NavPower/Recast build options used when emitting NavPower PSGs.
    /// Keep all nav tuning in <see cref="NavPowerBuildOptions"/> as the single source of truth.
    /// </summary>
    public NavPowerBuildOptions NavPower { get; init; } = new();
}
