using System.Numerics;

namespace ArenaBuilder.Collision.Math;

/// <summary>
/// Vector math helpers matching Python vec_dot, vec_cross, vec_normalize.
/// Ported from Collision_Export_Dumbad_Tuukkas_original.py lines 149-165.
/// </summary>
public static class Vector3Extensions
{
    public static float Dot(Vector3 a, Vector3 b) => Vector3.Dot(a, b);

    public static Vector3 Cross(Vector3 a, Vector3 b) => Vector3.Cross(a, b);

    /// <summary>Normalize; returns zero vector if length squared &lt;= 1e-20. Python line 163: length_sq &lt;= 1e-20.</summary>
    public static Vector3 Normalize(Vector3 v)
    {
        float lengthSq = v.X * v.X + v.Y * v.Y + v.Z * v.Z;
        if (lengthSq <= 1e-20f)
            return Vector3.Zero;
        float inv = 1f / MathF.Sqrt(lengthSq);
        return new Vector3(v.X * inv, v.Y * inv, v.Z * inv);
    }

    /// <summary>Get component by axis (0=X, 1=Y, 2=Z).</summary>
    public static float GetComponent(Vector3 v, int axis)
    {
        return axis switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => throw new ArgumentOutOfRangeException(nameof(axis)) };
    }

    /// <summary>Set one component; returns new Vector3.</summary>
    public static Vector3 WithComponent(Vector3 v, int axis, float value)
    {
        return axis switch
        {
            0 => new Vector3(value, v.Y, v.Z),
            1 => new Vector3(v.X, value, v.Z),
            2 => new Vector3(v.X, v.Y, value),
            _ => throw new ArgumentOutOfRangeException(nameof(axis))
        };
    }
}
