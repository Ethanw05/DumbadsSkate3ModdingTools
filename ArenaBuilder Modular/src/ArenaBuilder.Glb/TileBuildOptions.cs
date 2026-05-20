using ArenaBuilder.NavPower;

namespace ArenaBuilder.Glb;

/// <summary>
/// Configuration for tile-based streaming output. Layout:
/// <list type="bullet">
///   <item><c>cPres_U_V_high</c> — mesh PSGs for the cPres tile, plus a SMALL (1/8 linear of source)
///   "fallback" copy of every texture those meshes reference, written under the SMALL-variant GUID
///   (<c>G | 0x4000_0000_0000_0000</c>; bit 62 set). Always loaded with the geometry so the engine's
///   bit-62 retry path resolves the fallback whenever the cTex tile isn't paged in.</item>
///   <item><c>cSim_U_V_high</c> — collision data for the cSim tile.</item>
  ///   <item><c>cTex_X_Y_high</c> — full-res under FULL GUID <c>G</c>. The builder places <c>G</c> in
  ///   the <b>union</b> of (a) cTex cells overlapping each tile-split mesh AABB, and (b) greedy
  ///   <see cref="WorldTileGrid.AssignPresTilesToCTexCover"/> homes per cPres, so in-game streaming
  ///   can resolve full-res, not only the cPres small fallback.</item>
/// </list>
/// Mesh material channels reference <c>G</c> (bit 62 clear) in all tiles; the engine tries <c>G</c>
/// first and on miss ORs <c>0x4000_0000_0000_0000</c> to find the small fallback.
/// In <see cref="GlobalOnly"/> mode, everything for presentation (meshes + full-resolution textures)
/// lands in <c>cPres_Global</c> under <c>G</c> and collision in <c>cSim_Global</c>; no cTex folders
/// or small variants are emitted in that mode.
/// </summary>
public sealed record TileBuildOptions
{
    public float TileSize { get; init; } = WorldTileGrid.DefaultTileSize;
    public float OriginX { get; init; } = WorldTileGrid.DefaultOriginX;
    public float OriginY { get; init; } = WorldTileGrid.DefaultOriginY;
    public string TileSuffix { get; init; } = "high";

    /// <summary>
    /// Minimum allowed dimension (px) on the longest side of the small cPres-side fallback texture.
    /// The small variant is otherwise sized at <c>1 / <see cref="CPresSmallVariantDownscaleFactor"/></c>
    /// of the source's longest side; this floor prevents tiny sources from collapsing below a usable
    /// BCn block size. 8 is the minimum that keeps DXT 4×4-block encoding valid on both axes.
    /// </summary>
    public int CPresSmallVariantMinDim { get; init; } = 8;

    /// <summary>
    /// Linear downscale factor applied to the cPres-side small fallback variant relative to the source
    /// texture's longest side. Default of 8 mirrors the empirical 8× linear pairing observed in stock
    /// Skate <c>DLC_DW_MegaCompund</c> content (e.g. 512×512 ↔ 64×64, 256×128 ↔ 32×16).
    /// </summary>
    public int CPresSmallVariantDownscaleFactor { get; init; } = 8;
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
    /// cTex prefix for texture stream tiles. cTex tiles hold full-resolution textures keyed by the same
    /// logical GUID as the small fallback in cPres; the engine streams them on top of cPres at
    /// runtime (kStreamType_Texture in <c>AssetPaths::tStreamType</c>; suffix table at <c>0x82d5cad8</c>
    /// maps it to <c>"Tex"</c>). cTex tiles use the half-offset grid (<see cref="WorldTileGrid.CTexTileKey"/>):
    /// cTex tiles centered at <c>origin + CU * tileSize</c> while cPres tiles are at the half-tile
    /// offset (<c>origin + (U + 0.5) * tileSize</c>), matching stock content like
    /// <c>cPres_50_50_high.psf</c> alongside <c>cTex_0_0_high.psf</c>. The engine's tile name
    /// format <c>c%s_%.f_%.f_high</c> (at <c>0x8229ae50</c>) hashes integer coords deterministically.
    /// </summary>
    public const string CTexPrefix = "cTex";

    /// <summary>
    /// cPres global folder used only by <see cref="GlobalOnly"/> (no tiles). Holds both mesh PSGs and
    /// texture PSGs as a single self-contained collection.
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
