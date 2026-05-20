using System.Numerics;

namespace ChallengeEditor.Rendering;

/// <summary>
/// View matrix math in <c>double</c> for large world coordinates, then cast to <see cref="Matrix4x4"/>.
/// Matches <see cref="Matrix4x4.CreateLookAt"/> (left-handed) within float precision for typical ranges;
/// at extreme distances (tens of thousands of units) it avoids visible drift in clip-space / depth.
/// </summary>
public static class LargeWorldMatrices
{
    /// <summary>When max(|eye|, |target|) exceeds this, use double-precision look-at.</summary>
    public const float StableLookAtThreshold = 2500f;

    public static bool ShouldUseStableLookAt(Vector3 eye, Vector3 target) =>
        MathF.Max(LengthSquared(eye), LengthSquared(target)) > StableLookAtThreshold * StableLookAtThreshold;

    /// <summary>Left-handed look-at, same basis as <see cref="Matrix4x4.CreateLookAt"/>.</summary>
    public static Matrix4x4 CreateLookAtLh(Vector3 eye, Vector3 target, Vector3 up)
    {
        double ex = eye.X, ey = eye.Y, ez = eye.Z;
        double tx = target.X, ty = target.Y, tz = target.Z;
        double ux = up.X, uy = up.Y, uz = up.Z;

        // zaxis = normalize(target - eye)
        double zx = tx - ex, zy = ty - ey, zz = tz - ez;
        double zLen = Math.Sqrt(zx * zx + zy * zy + zz * zz);
        if (zLen < 1e-20) return Matrix4x4.Identity;
        zx /= zLen; zy /= zLen; zz /= zLen;

        // xaxis = normalize(cross(up, zaxis))
        double xx = uy * zz - uz * zy;
        double xy = uz * zx - ux * zz;
        double xz = ux * zy - uy * zx;
        double xLen = Math.Sqrt(xx * xx + xy * xy + xz * xz);
        if (xLen < 1e-20) return Matrix4x4.Identity;
        xx /= xLen; xy /= xLen; xz /= xLen;

        // yaxis = cross(zaxis, xaxis)
        double yx = zy * xz - zz * xy;
        double yy = zz * xx - zx * xz;
        double yz = zx * xy - zy * xx;

        double t41 = -(xx * ex + xy * ey + xz * ez);
        double t42 = -(yx * ex + yy * ey + yz * ez);
        double t43 = -(zx * ex + zy * ey + zz * ez);

        return new Matrix4x4(
            (float)xx, (float)yx, (float)zx, 0f,
            (float)xy, (float)yy, (float)zy, 0f,
            (float)xz, (float)yz, (float)zz, 0f,
            (float)t41, (float)t42, (float)t43, 1f);
    }

    private static float LengthSquared(Vector3 v) => v.X * v.X + v.Y * v.Y + v.Z * v.Z;
}
