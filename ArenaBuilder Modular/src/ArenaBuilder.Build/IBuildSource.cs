using System.Collections.Generic;
using System.Threading;
using ArenaBuilder.Glb;
using ArenaBuilder.Texture;

namespace ArenaBuilder.Build;

/// <summary>
/// Abstraction over "a thing that produces flattened mesh data + resolved texture
/// sources for a single logical mesh source." TileBuildPipeline operates on a
/// list of these (currently only <see cref="GlbBuildSource"/>) so the
/// tile-splitting / texture-emission / PSG-writing core stays decoupled from the
/// input form and a future non-GLB source could implement it without touching
/// the pipeline.
/// </summary>
public interface IBuildSource
{
    /// <summary>
    /// Stable identifier used as the lookup key in per-source dictionaries
    /// (e.g. <c>meshByTile.Parts</c> stores this string; <c>textureBuildByTileGlb</c>
    /// is keyed by (tile, sourceKey)). For GLB sources this is the absolute GLB
    /// path; any future source must supply something deterministic so GUIDs
    /// remain stable across builds.
    /// </summary>
    string SourceKey { get; }

    /// <summary>
    /// Human-readable short name; analogous to <c>Path.GetFileNameWithoutExtension(glbPath)</c>.
    /// Used as the default dominant-material name fallback and in some log lines.
    /// </summary>
    string SourceStem { get; }

    /// <summary>
    /// Phase 1: produce already-flattened, world-space mesh primitives.
    /// For GLB sources this is <c>ModelRoot.Load + MeshVertexFlattener.FlattenAllWithOverflowSplits</c>.
    /// For direct sources callers supply the data as constructed Results.
    /// </summary>
    IReadOnlyList<MeshVertexFlattener.Result> LoadMeshes(CancellationToken cancellationToken);

    /// <summary>
    /// Phase 2A: produce the texture sources for one of the materials this source
    /// contributes to. Called once per source by the parallel texture-emission
    /// loop. <paramref name="materialName"/> is the dominant material picked by
    /// <c>GlbUtilities.PickDominantMaterial</c>; <paramref name="guidNamespace"/>
    /// is the stable namespace string fed to GUID derivation so the same source
    /// produces the same GUIDs across builds.
    /// </summary>
    GlbTextureAutoBuilder.ResolvedGlbTextureSources ResolveTextures(
        string materialName,
        string guidNamespace,
        CancellationToken cancellationToken);
}
