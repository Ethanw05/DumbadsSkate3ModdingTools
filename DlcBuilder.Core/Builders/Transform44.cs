using System.Numerics;

namespace DlcBuilder.Builders;

/// 4x4 row-major affine transform, 16 floats. Used by both LocXml (`<Transform>`
/// element) and LocatorPsg (tLocationDesc payload). Storage layout:
///   m00, m01, m02, m03,  m10, m11, m12, m13,  m20, m21, m22, m23,  tx, ty, tz, 1
/// Bottom row is translation + 1.0 terminator.
///
/// Front-ends usually have a `System.Numerics.Matrix4x4` already; use
/// `FromMatrix` to convert. Identity rotation + a position is the most common
/// case (`IdentityAt(x,y,z)` / `Translation(p)`). Skate's locator transforms
/// are typically yaw-only around Y — `YawAt(x,y,z,deg)` produces the matching
/// shape.
public readonly struct Transform44
{
    public readonly float[] Rows;

    public Transform44(float[] sixteen)
    {
        ArgumentNullException.ThrowIfNull(sixteen);
        if (sixteen.Length != 16)
            throw new ArgumentException($"Transform44 needs 16 floats; got {sixteen.Length}.", nameof(sixteen));
        Rows = sixteen;
    }

    /// Identity 4x4 (no rotation, no translation).
    public static Transform44 Identity => new(new float[]
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    });

    /// Pure translation, identity rotation, position from a Vector3.
    public static Transform44 Translation(Vector3 t) => IdentityAt(t.X, t.Y, t.Z);

    /// Pure translation, identity rotation, position from x/y/z scalars.
    public static Transform44 IdentityAt(float tx, float ty, float tz) => new(new float[]
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        tx, ty, tz, 1,
    });

    /// Yaw-only rotation around the Y axis at the given position. Retail
    /// locator transforms are primarily yaw-only — this matches the shape:
    ///   [ cos 0 -sin 0 ; 0 1 0 0 ; sin 0 cos 0 ; tx ty tz 1 ]
    public static Transform44 YawAt(float tx, float ty, float tz, float yawDegrees)
    {
        float rad = yawDegrees * (MathF.PI / 180f);
        float c = MathF.Cos(rad);
        float s = MathF.Sin(rad);
        return new Transform44(new float[]
        {
            c, 0, -s, 0,
            0, 1,  0, 0,
            s, 0,  c, 0,
            tx, ty, tz, 1,
        });
    }

    /// Full yaw/pitch/roll rotation at the given position, using the same Euler
    /// composition the editor uses: CreateFromYawPitchRoll(yaw=Y, pitch=X, roll=Z).
    public static Transform44 YawPitchRollAt(float tx, float ty, float tz, Vector3 rotationDegrees)
    {
        float pitch = rotationDegrees.X * (MathF.PI / 180f);
        float yaw = rotationDegrees.Y * (MathF.PI / 180f);
        float roll = rotationDegrees.Z * (MathF.PI / 180f);
        Matrix4x4 m = Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll);
        m.M41 = tx;
        m.M42 = ty;
        m.M43 = tz;
        m.M44 = 1f;
        return FromMatrix(m);
    }

    /// Convert a System.Numerics matrix into a Transform44 in the row-major
    /// .loc/.psg storage layout. System.Numerics stores translation in
    /// M41/M42/M43 — we map to row-major so the on-disk bytes match retail.
    public static Transform44 FromMatrix(Matrix4x4 m) => new(new float[]
    {
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44,
    });

    public float Tx => Rows[12];
    public float Ty => Rows[13];
    public float Tz => Rows[14];
}
