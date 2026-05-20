using System.Numerics;

namespace DlcBuilder.Inputs;

public enum LocatorKind
{
    /// Generic player spawn used for freeskate.
    Spawn,
    /// Where the player is placed when a challenge starts.
    ChallengeStart,
    /// Anchor point referenced by the freeskate spawn-graph.
    FreeskateAnchor,
    /// Child locator referenced by another locator (e.g. respawn fallback).
    Sub,
}

/// Single point + facing in PSG/Skate Y-up world space.
public sealed record LocatorInput
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required Vector3 Position { get; init; }

    /// Euler angles in degrees. Y is the typical yaw; pitch/roll are usually 0.
    public Vector3 RotationDegrees { get; init; } = Vector3.Zero;

    public LocatorKind Kind { get; init; } = LocatorKind.Spawn;

    /// True when this locator is a DLC freeskate menu entry (one row per such locator).
    /// Used to validate <see cref="ChallengeInput.HostFreeskateLocatorId"/>.
    public bool IsFreeskateMenuSlot { get; init; }
}
