using ArenaBuilder.Core.Psg;

namespace ArenaBuilder.Collision;

/// <summary>
/// Top-level PSG build. Uses <see cref="CollisionPsgComposer"/> and <see cref="GenericArenaWriter"/>.
/// Same public API and <see cref="ICollisionInput"/> as before.
/// </summary>
public sealed class CollisionPsgBuilder
{
    public bool ForceUncompressed { get; set; } = true;
    public bool EnableVertexSmoothing { get; set; } = true;

    /// <summary>
    /// Weld by position before clustered mesh build. Tile pipeline sets false when collision was already welded after chunk merge.
    /// </summary>
    public bool WeldVerticesBeforeClustering { get; set; } = true;

    public float Granularity { get; set; } = 0.001f;

    /// <summary>
    /// Build full PSG into stream. Returns <c>true</c> if a PSG was written, or <c>false</c> when the input
    /// has no mesh geometry (spline-only / empty). No bytes are written in the false case so the caller can
    /// decide whether to delete/skip the output path without leaving a zero-byte corrupt PSG on disk. This
    /// method never throws for empty input — that guarantee is what lets long tile batch exports keep going.
    /// </summary>
    public bool Build(ICollisionInput input, Stream output)
    {
        PsgArenaSpec? spec = CollisionPsgComposer.Compose(
            input,
            Granularity,
            ForceUncompressed,
            EnableVertexSmoothing,
            WeldVerticesBeforeClustering);
        if (spec == null)
            return false;
        GenericArenaWriter.Write(spec, output);
        return true;
    }
}

/// <summary>Optional: provide per-face surface IDs for cluster serialization.</summary>
public interface ICollisionInputWithSurfaceIds : ICollisionInput
{
    IReadOnlyList<int>? SurfaceIds { get; }
}
