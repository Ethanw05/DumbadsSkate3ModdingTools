using System.Collections.Generic;
using System.IO;
using System.Threading;
using ArenaBuilder.Glb;
using ArenaBuilder.Texture;
using SharpGLTF.Schema2;

namespace ArenaBuilder.Build;

/// <summary>
/// IBuildSource wrapper around an on-disk .glb file. Calls to <see cref="LoadMeshes"/>
/// and <see cref="ResolveTextures"/> share a single <see cref="ModelRoot.Load"/> via
/// <see cref="Lazy{T}"/> so a large multi-material GLB (BlenRose's scene.glb) is parsed
/// and its image buffers materialized once for the whole build, not once per
/// (mesh phase + every material in the texture phase).
/// </summary>
public sealed class GlbBuildSource : IBuildSource
{
    private readonly string _glbPath;
    private readonly string? _materialsJsonPath;
    private readonly DerivedTextureGenerator.NormalSynthSettings? _normalSynth;
    private readonly Lazy<ModelRoot> _model;

    public GlbBuildSource(string glbPath, string? materialsJsonPath, DerivedTextureGenerator.NormalSynthSettings? normalSynthSettings)
    {
        _glbPath = glbPath;
        _materialsJsonPath = materialsJsonPath;
        _normalSynth = normalSynthSettings;
        _model = new Lazy<ModelRoot>(() => ModelRoot.Load(_glbPath), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string SourceKey  => _glbPath;
    public string SourceStem => Path.GetFileNameWithoutExtension(_glbPath);

    public IReadOnlyList<MeshVertexFlattener.Result> LoadMeshes(CancellationToken cancellationToken)
        => MeshVertexFlattener.FlattenAllWithOverflowSplits(_model.Value, cancellationToken);

    public GlbTextureAutoBuilder.ResolvedGlbTextureSources ResolveTextures(
        string materialName,
        string guidNamespace,
        CancellationToken cancellationToken)
        => GlbTextureAutoBuilder.ResolveSourcesFromModel(
            _model.Value,
            _glbPath,
            generateMipMaps: true,
            materialsJsonPath: _materialsJsonPath,
            materialNameOverride: materialName,
            guidNamespace: guidNamespace,
            normalSynthSettings: _normalSynth,
            cancellationToken: cancellationToken);
}
