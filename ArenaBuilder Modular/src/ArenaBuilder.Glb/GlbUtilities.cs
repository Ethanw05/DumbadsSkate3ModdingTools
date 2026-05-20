using System.Numerics;

namespace ArenaBuilder.Glb;

/// <summary>
/// Shared utility methods for GLB processing.
/// </summary>
public static class GlbUtilities
{
    /// <summary>
    /// Picks the material name from the result with the most triangles (dominant geometry).
    /// Ensures texture linkage uses the material that covers most of the combined mesh.
    /// </summary>
    public static string PickDominantMaterial<T>(IReadOnlyList<T> results, Func<T, string> getMaterialName, Func<T, int> getTriangleCount)
    {
        if (results == null || results.Count == 0)
            throw new ArgumentException("Results list cannot be empty.", nameof(results));

        int bestTriCount = -1;
        string bestMat = getMaterialName(results[0]);
        foreach (var r in results)
        {
            int triCount = getTriangleCount(r);
            if (triCount > bestTriCount)
            {
                bestTriCount = triCount;
                bestMat = getMaterialName(r);
            }
        }
        return bestMat;
    }

    /// <summary>
    /// Computes bounding box from a list of vertices.
    /// </summary>
    public static (Vector3 Min, Vector3 Max) ComputeBounds(IReadOnlyList<Vector3> vertices)
    {
        if (vertices == null || vertices.Count == 0)
            return (Vector3.Zero, Vector3.Zero);

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var p in vertices)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Z < minZ) minZ = p.Z;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
            if (p.Z > maxZ) maxZ = p.Z;
        }
        return (new Vector3(minX, minY, minZ), new Vector3(maxX, maxY, maxZ));
    }
}
