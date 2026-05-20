using System.Numerics;

namespace ArenaBuilder.Collision.Math;

/// <summary>
/// Axis-aligned bounding box. Matches Python AABBox (Collision_Export_Dumbad_Tuukkas_original.py lines 215-244).
/// Used for KD-tree SAH and cluster bounds.
/// </summary>
public readonly struct AABBox
{
    public Vector3 Min { get; }
    public Vector3 Max { get; }

    public AABBox(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
    }

    /// <summary>Surface area of the box. 2 * (dx*dy + dy*dz + dz*dx).</summary>
    /// <remarks>
    /// Ported from Python <c>rwc_BBoxSurfaceArea</c> (Collision_Export_Dumbad_Tuukkas_original.py lines 989-1001).
    /// IMPORTANT: Python/RenderWare do NOT clamp inverted boxes to 0 here; inverted boxes are used intentionally.
    /// </remarks>
    public float SurfaceArea()
    {
        var d = Max - Min;
        return 2f * (d.X * d.Y + d.Y * d.Z + d.Z * d.X);
    }

    /// <summary>Double-precision surface area (Python float parity).</summary>
    public double SurfaceAreaD()
    {
        double dx = (double)Max.X - Min.X;
        double dy = (double)Max.Y - Min.Y;
        double dz = (double)Max.Z - Min.Z;
        return 2.0 * (dx * dy + dy * dz + dz * dx);
    }

    /// <summary>Expand this box to include another (returns new box).</summary>
    /// <remarks>Ported from Collision_Export_Dumbad_Tuukkas_original.py lines 227-233.</remarks>
    public AABBox Expand(AABBox other)
    {
        return new AABBox(
            Vector3.Min(Min, other.Min),
            Vector3.Max(Max, other.Max)
        );
    }

    /// <summary>Bounding box of a triangle from three vertices.</summary>
    /// <remarks>Ported from Collision_Export_Dumbad_Tuukkas_original.py lines 235-243 (tri_bbox).</remarks>
    public static AABBox TriBbox(Vector3 v0, Vector3 v1, Vector3 v2)
    {
        var minX = System.Math.Min(System.Math.Min(v0.X, v1.X), v2.X);
        var minY = System.Math.Min(System.Math.Min(v0.Y, v1.Y), v2.Y);
        var minZ = System.Math.Min(System.Math.Min(v0.Z, v1.Z), v2.Z);
        var maxX = System.Math.Max(System.Math.Max(v0.X, v1.X), v2.X);
        var maxY = System.Math.Max(System.Math.Max(v0.Y, v1.Y), v2.Y);
        var maxZ = System.Math.Max(System.Math.Max(v0.Z, v1.Z), v2.Z);
        return new AABBox(new Vector3(minX, minY, minZ), new Vector3(maxX, maxY, maxZ));
    }
}
