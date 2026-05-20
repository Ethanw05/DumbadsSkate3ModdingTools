using System.Collections.Generic;
using System.IO;
using System.Threading;
using ArenaBuilder.Glb;
using ArenaBuilder.Texture;
using SharpGLTF.Schema2;

namespace ArenaBuilder.Build;

/// <summary>
/// IBuildSource wrapper around an on-disk .glb file — preserves the existing
/// ArenaBuilder behaviour. Calling <see cref="LoadMeshes"/> loads the GLB and
/// flattens its primitives; <see cref="ResolveTextures"/> calls into the existing
/// <see cref="GlbTextureAutoBuilder.ResolveSourcesFromGlb"/> path.
/// </summary>
public sealed class GlbBuildSource : IBuildSource
{
    private readonly string _glbPath;
    private readonly string? _materialsJsonPath;
    private readonly DerivedTextureGenerator.NormalSynthSettings? _normalSynth;

    public GlbBuildSource(string glbPath, string? materialsJsonPath, DerivedTextureGenerator.NormalSynthSettings? normalSynthSettings)
    {
        _glbPath = glbPath;
        _materialsJsonPath = materialsJsonPath;
        _normalSynth = normalSynthSettings;
    }

    public string SourceKey  => _glbPath;
    public string SourceStem => Path.GetFileNameWithoutExtension(_glbPath);

    public IReadOnlyList<MeshVertexFlattener.Result> LoadMeshes(CancellationToken cancellationToken)
    {
        ModelRoot model = ModelRoot.Load(_glbPath);
        return MeshVertexFlattener.FlattenAllWithOverflowSplits(model, cancellationToken);
    }

    public GlbTextureAutoBuilder.ResolvedGlbTextureSources ResolveTextures(
        string materialName,
        string guidNamespace,
        CancellationToken cancellationToken)
        => GlbTextureAutoBuilder.ResolveSourcesFromGlb(
            _glbPath,
            generateMipMaps: true,
            materialsJsonPath: _materialsJsonPath,
            materialNameOverride: materialName,
            guidNamespace: guidNamespace,
            normalSynthSettings: _normalSynth,
            cancellationToken: cancellationToken);
}
