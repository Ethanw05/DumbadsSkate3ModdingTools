using System.Numerics;

namespace ArenaBuilder.Collision.Validation;

/// <summary>
/// Triangle validity check. RenderWare <c>TriangleValidator::IsTriangleValid</c> (trianglevalidator.cpp lines 37-53)
/// using <c>lengthSquared &gt; rwpmath::MINIMUM_RECIPROCAL</c>; EA headers are outside this repo — value matches EA/physics convention <c>1e-10f</c>.
/// Batch pass: <c>ClusteredMeshBuilderMethods::ValidateTriangles</c> (rwcclusteredmeshbuildermethods.cpp lines 100-141) marks degenerates disabled but keeps triangle list size;
/// this pipeline <em>filters</em> degenerates out (Skate / simplified mesh list).
/// Ported from Collision_Export_Dumbad_Tuukkas_original.py lines 2320-2390.
/// </summary>
public static class TriangleValidation
{
    /// <summary>Mirror <c>rwpmath::MINIMUM_RECIPROCAL</c> as used in trianglevalidator.cpp (float compare on <c>MagnitudeSquared(cross)</c>).</summary>
    public const float MinimumReciprocal = 1e-10f;

    public readonly record struct ValidatedTriangle(int V0, int V1, int V2, int OriginalIndex);

    /// <summary>True if triangle has area above tolerance (same cross product and squared-length test as RW).</summary>
    public static bool IsTriangleValid(Vector3 v0, Vector3 v1, Vector3 v2)
    {
        var normal = Vector3.Cross(v1 - v0, v2 - v0);
        float lengthSquared = normal.LengthSquared();
        return lengthSquared > MinimumReciprocal;
    }

    /// <summary>Filter out degenerate triangles. Empty <paramref name="tris"/> yields empty list; throws if every triangle is degenerate.</summary>
    public static IReadOnlyList<(int V0, int V1, int V2)> ValidateTriangles(IReadOnlyList<Vector3> verts, IReadOnlyList<(int V0, int V1, int V2)> tris)
    {
        var validated = ValidateTrianglesWithOriginalIndices(verts, tris);
        return validated.Select(t => (t.V0, t.V1, t.V2)).ToList();
    }

    /// <summary>
    /// Filter out degenerate triangles while preserving original indices.
    /// This is required to keep per-face metadata (e.g. surface IDs) aligned after filtering.
    /// </summary>
    public static IReadOnlyList<ValidatedTriangle> ValidateTrianglesWithOriginalIndices(
        IReadOnlyList<Vector3> verts,
        IReadOnlyList<(int V0, int V1, int V2)> tris)
    {
        if (tris.Count == 0)
            return Array.Empty<ValidatedTriangle>();

        var valid = new List<ValidatedTriangle>(tris.Count);
        int vertCount = verts.Count;
        for (int i = 0; i < tris.Count; i++)
        {
            var (v0Idx, v1Idx, v2Idx) = tris[i];
            if ((uint)v0Idx >= (uint)vertCount || (uint)v1Idx >= (uint)vertCount || (uint)v2Idx >= (uint)vertCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(tris),
                    $"Triangle {i} vertex index out of range: ({v0Idx},{v1Idx},{v2Idx}) with vertex count {vertCount}.");
            }

            if (IsTriangleValid(verts[v0Idx], verts[v1Idx], verts[v2Idx]))
                valid.Add(new ValidatedTriangle(v0Idx, v1Idx, v2Idx, i));
        }

        if (valid.Count == 0)
            throw new InvalidOperationException("All triangles are degenerate. Check mesh geometry.");

        return valid;
    }
}
