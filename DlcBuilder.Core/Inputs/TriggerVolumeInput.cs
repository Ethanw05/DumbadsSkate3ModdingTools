using System.Numerics;

namespace DlcBuilder.Inputs;

/// Oriented bounding box used as scoring volume / challenge boundary / generic
/// trigger. Stored in world units in PSG/Skate Y-up space (no editor swizzles).
public sealed record TriggerVolumeInput
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required Vector3 Center { get; init; }
    public required Vector3 HalfExtents { get; init; }

    /// Euler angles in degrees. X = pitch, Y = yaw, Z = roll. Composed via
    /// Matrix4x4.CreateFromYawPitchRoll(Y°, X°, Z°) on the consumer side.
    public Vector3 RotationDegrees { get; init; } = Vector3.Zero;
}
