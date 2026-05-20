namespace DlcBuilder.Modules.OtsPsg;

/// One trigger volume to register inside an OTS challenge's `cSim_Global.psf`:
/// a polygon footprint (extruded to a thin slab between MinY / MaxY) plus the
/// engine-side identification fields baked into the `pegasus::tTriggerInstance`
/// record.
///
/// Polygon vertices are 2D world-space XZ points (Y-up coordinate system,
/// matching Skate 3 worlds). They get triangulated via fan triangulation when
/// the PSG composer builds the underlying ClusteredMesh, so the footprint
/// must be convex (or at least star-shaped from `Polygon[0]`).
///
/// For OTS the convention is two TriggerVolumes per challenge —
/// `challengeboundary` (outer; OOB / signup) and `scoringboundary`
/// (inner; geometric scoring + EnteredVolume gate).
public sealed record OtsTriggerVolume
{
    /// 2D XZ polygon vertices (world-space). Convex, ≥3 points.
    public required IReadOnlyList<(float X, float Z)> Polygon { get; init; }

    /// Min Y (bottom of the slab). Sets the floor of the trigger volume.
    public float MinY { get; init; } = -1000f;

    /// Max Y (top of the slab). Sets the ceiling.
    public float MaxY { get; init; } = 1000f;

    /// Display / lookup name written into the PSG's name pool. Convention:
    /// `"&lt;World&gt;|&lt;challenge&gt;_&lt;volume_kind&gt;|0x&lt;lookup8&gt;"` —
    /// same string referenced by the per-instance `challenge_local_data` row's
    /// `ChallengeBoundary`, `DiscoveryBoundary`, and `OTSScoringBoundary`
    /// attributes.
    public required string Name { get; init; }

    /// Stable per-volume identifier (Lookup8 of the canonical name).
    public required ulong Guid { get; init; }

    /// Engine-side resolution hash (Lookup8 of the volume's canonical name
    /// suffixed with the dist key, so two challenges on different maps with
    /// the same volume kind don't collide). Retail OTS volumes use values
    /// like `0x2c701706003d0bXX`.
    public required ulong GuidLocal { get; init; }

    /// Trigger type — most challenge volumes use Challenge=0.
    public uint TriggerType { get; init; } = 0;

    /// Yaw rotation in radians. The polygon vertices are already in world
    /// space (rotation baked in), but the engine's tTriggerInstance
    /// m_TransformMatrix must carry the rotation so visual indicators
    /// (archway ribbons) orient correctly. 0 = axis-aligned.
    public float YawRadians { get; init; } = 0f;
}
