using ArenaBuilder.Collision.Validation;
using System.Numerics;

namespace ArenaBuilder.Tests.RenderWare;

/// <summary>
/// Mirrors the parts of RenderWare 6.14.00 tests/clusteredmeshbuilder/test-trianglegeometry.cpp
/// that exist in our port (TriangleValidator::IsTriangleValid).
/// </summary>
public sealed class RenderWareTriangleGeometryTests
{
    [Fact]
    public void IsTriangleValid_DegenerateTriangle_IsInvalid()
    {
        var p0 = new Vector3(0, 0, 0);
        var p1 = new Vector3(0, 0, 1);
        var p2 = new Vector3(0, 0, 1);

        Assert.False(TriangleValidation.IsTriangleValid(p0, p1, p2));
    }

    [Fact]
    public void IsTriangleValid_NonDegenerateTriangle_IsValid()
    {
        var p0 = new Vector3(0, 0, 0);
        var p1 = new Vector3(0, 0, 1);
        var p2 = new Vector3(1, 0, 0);

        Assert.True(TriangleValidation.IsTriangleValid(p0, p1, p2));
    }

    [Fact]
    public void IsTriangleValid_AtMinimumSquaredLength_IsInvalid()
    {
        // |cross|^2 = eps^2; RW uses strict > MINIMUM_RECIPROCAL
        float eps = MathF.Sqrt(TriangleValidation.MinimumReciprocal);
        var p0 = new Vector3(0, 0, 0);
        var p1 = new Vector3(1, 0, 0);
        var p2 = new Vector3(0, eps, 0);
        Assert.False(TriangleValidation.IsTriangleValid(p0, p1, p2));
    }

    [Fact]
    public void ValidateTrianglesWithOriginalIndices_EmptyTriangles_ReturnsEmpty()
    {
        var verts = new List<Vector3> { Vector3.Zero };
        var tris = Array.Empty<(int, int, int)>();
        var r = TriangleValidation.ValidateTrianglesWithOriginalIndices(verts, tris);
        Assert.Empty(r);
    }

    [Fact]
    public void ValidateTrianglesWithOriginalIndices_OutOfRangeIndex_Throws()
    {
        var verts = new List<Vector3> { Vector3.Zero, Vector3.UnitX, Vector3.UnitY };
        var tris = new[] { (0, 1, 9) };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TriangleValidation.ValidateTrianglesWithOriginalIndices(verts, tris));
    }
}

