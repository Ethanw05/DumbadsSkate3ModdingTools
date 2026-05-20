using System.Numerics;
using Veldrid;

namespace ChallengeEditor.Rendering;

/// <summary>
/// Orbit camera (default): yaw/pitch around a target at distance — RMB orbit, MMB pan, scroll zoom.
/// Fly mode: free eye position; RMB drag rotates view (yaw/pitch); WASD/QE move; scroll adjusts move speed (editor UI).
/// </summary>
public sealed class OrbitCamera
{
    public Vector3 Target { get; set; } = Vector3.Zero;
    public float Distance { get; set; } = 30f;
    public float YawRadians { get; set; } = MathF.PI * 0.25f;
    public float PitchRadians { get; set; } = -MathF.PI * 0.25f;
    public float FieldOfViewYRadians { get; set; } = MathF.PI / 3f;
    public float NearPlane { get; set; } = 0.1f;
    public float FarPlane { get; set; } = 5000f;

    /// <summary>When true, <see cref="Position"/> is <see cref="FlyPosition"/> and view rotates around the eye.</summary>
    public bool FlyMode { get; private set; }

    /// <summary>World-space eye while <see cref="FlyMode"/> is active.</summary>
    public Vector3 FlyPosition { get; private set; }

    /// <summary>Unit view direction from yaw/pitch (same convention as orbit look direction).</summary>
    public Vector3 ViewForward
    {
        get
        {
            float cp = MathF.Cos(PitchRadians), sp = MathF.Sin(PitchRadians);
            float cy = MathF.Cos(YawRadians), sy = MathF.Sin(YawRadians);
            return new Vector3(cp * sy, sp, cp * cy);
        }
    }

    public Vector3 Position =>
        FlyMode ? FlyPosition : ComputeOrbitEye();

    private Vector3 ComputeOrbitEye()
    {
        Vector3 f = ViewForward;
        return Target - f * Distance;
    }

    public Matrix4x4 GetViewMatrix()
    {
        Vector3 eye = Position;
        Vector3 fwd = ViewForward;
        return Matrix4x4.CreateLookAt(eye, eye + fwd, Vector3.UnitY);
    }

    public Matrix4x4 GetProjectionMatrix(float aspect) =>
        Matrix4x4.CreatePerspectiveFieldOfView(FieldOfViewYRadians, aspect, NearPlane, FarPlane);

    /// <summary>Switch to fly mode preserving current view origin and direction.</summary>
    public void EnterFlyMode()
    {
        if (FlyMode) return;
        FlyPosition = ComputeOrbitEye();
        FlyMode = true;
    }

    /// <summary>Return to orbit mode; places orbit target ahead of the camera.</summary>
    public void ExitFlyMode(float targetAhead = 12f)
    {
        if (!FlyMode) return;
        Vector3 f = ViewForward;
        Target = FlyPosition + f * targetAhead;
        Distance = Math.Clamp(targetAhead, 0.5f, 4000f);
        FlyMode = false;
    }

    public void SetFlyMode(bool fly)
    {
        if (fly == FlyMode) return;
        if (fly) EnterFlyMode();
        else ExitFlyMode();
    }

    /// <summary>Orbit + pan + zoom. Ignored when <see cref="FlyMode"/> (use <see cref="UpdateFly"/>).</summary>
    public void Update(Vector2 mouseDelta, float wheel, bool orbiting, bool panning)
    {
        if (FlyMode) return;

        if (orbiting)
        {
            const float orbitSpeed = 0.005f;
            YawRadians -= mouseDelta.X * orbitSpeed;
            PitchRadians = Math.Clamp(
                PitchRadians - mouseDelta.Y * orbitSpeed,
                -MathF.PI * 0.499f, MathF.PI * 0.499f);
        }
        if (panning)
        {
            float panSpeed = Distance * 0.0015f;
            Vector3 right = Vector3.Cross(Vector3.UnitY, Target - Position);
            if (right.LengthSquared() < 1e-6f) right = Vector3.UnitX; else right = Vector3.Normalize(right);
            Vector3 up = Vector3.Cross(right, Vector3.Normalize(Target - Position));
            Target += right * mouseDelta.X * panSpeed + up * mouseDelta.Y * panSpeed;
        }
        if (wheel != 0)
        {
            const float zoomStep = 1.04f;
            Distance = Math.Clamp(Distance * MathF.Pow(zoomStep, -wheel), 0.1f, 4000f);
        }
    }

    /// <summary>Fly mode: RMB drag rotates view. Wheel is handled by the editor (fly move speed), not zoom/dolly.</summary>
    public void UpdateFly(Vector2 mouseDelta, bool lookDrag)
    {
        if (!FlyMode) return;

        if (lookDrag)
        {
            const float lookSpeed = 0.005f;
            YawRadians -= mouseDelta.X * lookSpeed;
            PitchRadians = Math.Clamp(
                PitchRadians - mouseDelta.Y * lookSpeed,
                -MathF.PI * 0.499f, MathF.PI * 0.499f);
        }
    }

    /// <summary>Camera-relative movement. Orbit: moves pivot. Fly: moves eye.</summary>
    public void MoveLocal(Vector2 horizontal, float vertical, float speed, float dt)
    {
        if (horizontal == Vector2.Zero && vertical == 0) return;

        Vector3 viewForward = ViewForward;

        Vector3 right = Vector3.Cross(viewForward, Vector3.UnitY);
        if (right.LengthSquared() < 1e-6f) right = Vector3.UnitX;
        right = Vector3.Normalize(right);

        Vector3 delta =
            viewForward * horizontal.Y +
            right * horizontal.X +
            Vector3.UnitY * vertical;

        float len = delta.Length();
        if (len < 1e-6f) return;
        delta = delta / len * (speed * dt);

        if (FlyMode)
            FlyPosition += delta;
        else
            Target += delta;
    }

    /// <summary>Frame a world-space bounding sphere (used from editor framing).</summary>
    public void FrameSphere(Vector3 center, float radius)
    {
        float dist = Math.Clamp(radius * 2.2f, 2f, 4000f);
        if (FlyMode)
        {
            Vector3 f = ViewForward;
            if (f.LengthSquared() < 1e-6f) f = new Vector3(0.5f, -0.3f, 0.5f);
            f = Vector3.Normalize(f);
            FlyPosition = center - f * dist;
        }
        else
        {
            Target = center;
            Distance = dist;
        }
    }
}
