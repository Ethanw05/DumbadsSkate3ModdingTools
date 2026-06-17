using ArenaBuilder.Core.Psg;

using ArenaBuilder.Core.Platforms.Common.PsgFormat;

namespace ArenaBuilder.Collision;

/// <summary>
/// Xbox 360 (.rx2) sibling of <see cref="CollisionPsgComposer"/>. Collision data is
/// cross-platform clean (docs/X360_Port_Deltas.md §7) — the same <see cref="PsgArenaSpec"/>
/// works on both platforms. Only the arena writer differs.
/// </summary>
public static class CollisionRX2Composer
{
    /// <summary>Composes the spec (delegates to <see cref="CollisionPsgComposer.Compose"/>).</summary>
    public static PsgArenaSpec? Compose(
        ICollisionInput input,
        float granularity,
        bool forceUncompressed,
        bool enableVertexSmoothing,
        bool weldVerticesBeforeClustering = true)
        => CollisionPsgComposer.Compose(input, granularity, forceUncompressed, enableVertexSmoothing, weldVerticesBeforeClustering);

    /// <summary>
    /// Composes and writes the collision arena as an X360 .rx2 file. Returns false when input is
    /// empty (no triangles) — caller treats as "skip output" rather than error.
    /// </summary>
    public static bool Write(
        ICollisionInput input,
        string outputPath,
        float granularity,
        bool forceUncompressed,
        bool enableVertexSmoothing,
        bool weldVerticesBeforeClustering = true)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path is required.", nameof(outputPath));

        var spec = Compose(input, granularity, forceUncompressed, enableVertexSmoothing, weldVerticesBeforeClustering);
        if (spec is null) return false;

        string fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var fs = File.Create(fullPath);
        GeneralArenaBuilder.Write(spec, fs, ArenaPlatform.Xbox360, fullPath);
        return true;
    }
}
