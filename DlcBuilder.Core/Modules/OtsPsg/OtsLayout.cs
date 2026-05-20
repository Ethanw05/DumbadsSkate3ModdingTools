using System.Collections.Generic;
using System.Numerics;
using ArenaBuilder.Core;
using DlcBuilder.Builders;
using DlcBuilder.Modules.LocatorPsg;

namespace DlcBuilder.Modules.OtsPsg;

/// Bundled output of <see cref="OtsLayout.BuildSpawnRelative"/> — the three
/// pieces an OTS challenge needs.
public sealed record OtsLayoutResult(
    IReadOnlyList<OtsTriggerVolume> Triggers,
    IReadOnlyList<LocationDescDataBuilder.SubLocSpec> SubLocators,
    Transform44 AnchorTransform);

/// Derive every world-space coordinate an OTS challenge needs from a single
/// spawn point + yaw, so the same builder can produce a valid challenge for
/// any DIST without hand-pasted retail coordinates.
///
/// Conventions (Skate 3 is Y-up, world coords in metres):
///   Forward (yaw=0) = +Z   →  forward(yaw) = (sin yaw, 0, cos yaw)
///   Right   (yaw=0) = +X   →  right(yaw)   = (cos yaw, 0, -sin yaw)
///
/// Trigger volume sizes are deliberately conservative defaults that enclose
/// any reasonable trick line around the spawn but stay small enough to fit
/// inside typical playable areas. Per-challenge tuning (passing different
/// dimensions to BuildSpawnRelative) is a one-line change.
public static class OtsLayout
{
    /// Half-extents (metres) of the outer challenge boundary slab. Player must
    /// stay inside.
    public const float ChallengeHalfXZ = 50f;
    public const float ChallengeHalfY = 20f;

    /// Half-extents of the inner scoring boundary slab. Tricks score only
    /// inside this.
    public const float ScoringHalfXZ = 10f;
    public const float ScoringHalfY = 5f;
    /// Hardcoded discovery volume half-extents (50x50x50 full size).
    public const float DiscoveryHalfXYZ = 25f;

    /// Distance to the side of spawn for the wait locator.
    public const float WaitLocatorOffset = 8f;

    /// Vertical lift for the floating vis indicator.
    public const float VisLocatorLift = 1f;

    /// Build the full per-challenge spatial layout (trigger volumes +
    /// sub-locators + anchor transform) centred on a single spawn point.
    /// Every coordinate is derived from the spawn — no map-specific constants.
    ///
    /// The scoring boundary defines where the player can earn points. When the
    /// caller has an authored scoring volume, pass its actual centre +
    /// half-extents via <paramref name="scoringCenter"/> /
    /// <paramref name="scoringHalfExtents"/> so the engine's geometric
    /// scoring check (sub_2D04F8 walks the trigger volume's polygon every
    /// frame and only counts points while the player is inside) restricts
    /// scoring to the user-authored box. With null we fall back to the
    /// hardcoded ScoringHalf* defaults centred on spawn — a safety net when
    /// no scoring volume was authored, but it makes the entire ~10m cube
    /// around spawn count, which is the failure mode the user hit ("I can
    /// score anywhere in the map").
    public static OtsLayoutResult BuildSpawnRelative(
        string challengeKey,
        string worldStreamName,
        string distKey,
        float spawnX,
        float spawnY,
        float spawnZ,
        float spawnYawDegrees,
        (float X, float Y, float Z)? scoringCenter = null,
        (float X, float Y, float Z)? scoringHalfExtents = null,
        (float X, float Y, float Z)? startLocatorPosition = null,
        float? startLocatorYawDegrees = null,
        (float X, float Y, float Z)? visualSignupPosition = null,
        float? visualSignupYawDegrees = null,
        IReadOnlyList<(float X, float Y, float Z, Vector3 RotationDegrees)>? authoredChevronTransforms = null,
        IReadOnlyList<(float X, float Y, float Z, float YawDeg)>? inChallengeRibbonTransforms = null,
        (float X, float Y, float Z)? discoveryBoundaryCenter = null,
        (float X, float Y, float Z)? discoveryBoundaryHalfExtents = null,
        (float X, float Y, float Z)? challengeBoundaryCenter = null,
        (float X, float Y, float Z)? challengeBoundaryHalfExtents = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(worldStreamName);
        ArgumentException.ThrowIfNullOrWhiteSpace(distKey);

        float yaw = spawnYawDegrees * (MathF.PI / 180f);
        float fx = MathF.Sin(yaw), fz = MathF.Cos(yaw);    // forward
        float rx = MathF.Cos(yaw), rz = -MathF.Sin(yaw);   // right

        // Resolve scoring volume — caller's actual dimensions if supplied,
        // otherwise the spawn-centred default.
        var (scx, scy, scz) = scoringCenter ?? (spawnX, spawnY, spawnZ);
        var (shx, shy, shz) = scoringHalfExtents ?? (ScoringHalfXZ, ScoringHalfY, ScoringHalfXZ);

        // Resolve visual locator placement points.
        var (vsx, vsy, vsz) = visualSignupPosition ?? (spawnX, spawnY, spawnZ);
        float vsyaw = visualSignupYawDegrees ?? spawnYawDegrees;
        // Discovery is intentionally hardcoded to 50x50x50; center can be
        // authored, otherwise it follows challenge boundary.
        var (dbx, dby, dbz) = discoveryBoundaryCenter ?? challengeBoundaryCenter ?? (spawnX, spawnY, spawnZ);
        var (dbhx, dbhy, dbhz) = (DiscoveryHalfXYZ, DiscoveryHalfXYZ, DiscoveryHalfXYZ);

        // Resolve challenge boundary — user's authored ChallengeBoundary
        // volume drives the actual on-disk slab. The fallback (spawn-centred
        // 50×20×50m) is the failure mode the user hits when no boundary is
        // wired through: the engine's OOB tracker fires far beyond the
        // authored area, and the player gets "leaving challenge" prompts at
        // up to 50m from spawn even when the authored boundary is much
        // smaller. With the authored centre + half-extents the engine's
        // OTSTriggerBoundary tracking exactly mirrors the user's box.
        var (cbx, cby, cbz) = challengeBoundaryCenter ?? (spawnX, spawnY, spawnZ);
        var (cbhx, cbhy, cbhz) = challengeBoundaryHalfExtents ?? (ChallengeHalfXZ, ChallengeHalfY, ChallengeHalfXZ);

        // Resolve the start locator. The engine's StartLocation HAL on the
        // per-instance challenge_local_data row resolves through
        // LocationManager to *this* sub-locator's transform — so it dictates
        // where the player gets teleported when they accept the challenge AND
        // which way they face.
        // Earlier shape pinned _startlocator to the scoring volume's centre,
        // facing the scoring volume's yaw — wrong on both axes when the user
        // authored a separate ChallengeStart locator (the symptom: spawn
        // offset by ~10m, facing the wrong way).
        var (slx, sly, slz) = startLocatorPosition ?? (spawnX, spawnY, spawnZ);
        float slYaw = startLocatorYawDegrees ?? spawnYawDegrees;
        float slYawRad = slYaw * (MathF.PI / 180f);

        // ── Trigger volumes ───────────────────────────────────────────────
        // Three authored volumes per OTS:
        //   - `discoveryboundary` (freeskate discovery path, hardcoded 50x50x50)
        //   - `challengeboundary` (outer): serves both signup detection AND
        //     OOB tracking; bound to OTSTriggerBoundary on the
        //     challenge_global_data row and to ChallengeBoundary on the
        //     per-instance challenge_local_data row.
        //   - `scoringboundary` (inner): serves discovery, geometric scoring,
        //     AND the EnteredVolume gate that arms the run. Bound to
        //     OTSScoringBoundary + DiscoveryBoundary on challenge_local_data
        //     and to the ObjectiveDefinition Lua's EnteredVolume call.
        //
        // Retail DW also ships a third `spotvolume_1` trigger as the gate
        // target, but only because DW's level designer wanted a smaller
        // activation gate inside a bigger scoring polygon. Our authoring
        // model exposes a single user-drawn ScoringVolume that means
        // "scoring counts here", so a separate gate would just be a
        // duplicate or a magic-number drift away from the authored box.
        // OtsChallengeRowsBuilder rebinds the EnteredVolume Lua to this
        // `scoringboundary` trigger by name.
        var triggers = new List<OtsTriggerVolume>
        {
            MakeAxisAlignedVolume("discoveryboundary", challengeKey, worldStreamName, distKey,
                dbx, dby, dbz, dbhx, dbhy, dbhz),
            MakeAxisAlignedVolume("challengeboundary", challengeKey, worldStreamName, distKey,
                cbx, cby, cbz, cbhx, cbhy, cbhz),
            MakeAxisAlignedVolume("scoringboundary", challengeKey, worldStreamName, distKey,
                scx, scy, scz, shx, shy, shz),
        };

        // ── Sub-locators ──────────────────────────────────────────────────
        // Chevrons line up forward of the start locator (so they trace the
        // path the player runs into the scoring volume); vis floats above
        // scoring centre; wait locator sits to the player's right.
        var subLocators = new List<LocationDescDataBuilder.SubLocSpec>();

        // `_chev_*` subs: authored → real trail + VisualIndicators. When none
        // are authored, emit three **structural** chevrons stacked on the start
        // locator (not the old 4/8/12 m forward trail) so mission PSG / Sim.loc
        // keep retail ordering (`chev_1` first → stable LocationDescData TOC
        // identity) without drawing chevron ribbons (`OmitFromChallengeLocal…`).
        if (authoredChevronTransforms is { Count: > 0 } authored)
        {
            for (int i = 0; i < authored.Count; i++)
            {
                var (cx, cy, cz, rotDeg) = authored[i];
                subLocators.Add(new LocationDescDataBuilder.SubLocSpec(
                    $"{challengeKey}_chev_{i + 1}",
                    Transform44.YawPitchRollAt(cx, cy, cz, rotDeg)));
            }
        }
        else
        {
            for (int i = 0; i < 3; i++)
            {
                subLocators.Add(new LocationDescDataBuilder.SubLocSpec(
                    $"{challengeKey}_chev_{i + 1}",
                    Transform44.YawAt(slx, sly, slz, slYaw),
                    RibbonIndicatorCollectionKey: null,
                    OmitFromChallengeLocalVisualIndicators: true));
            }
        }

        subLocators.Add(new LocationDescDataBuilder.SubLocSpec(
            $"{challengeKey}_startlocator",
            Transform44.YawAt(slx, sly, slz, slYaw)));

        // Signup indicator position (visual locator branch).
        subLocators.Add(new LocationDescDataBuilder.SubLocSpec(
            $"{challengeKey}_vis_1",
            Transform44.YawAt(vsx, vsy + VisLocatorLift, vsz, vsyaw)));

        // In-challenge ribbon arrows — engine + challenge_local_data.VisualIndicators
        // reference <c>{key}_vis_2</c>, <c>_vis_3</c>, … ( retail uses multiple vis slots ).
        if (inChallengeRibbonTransforms is { Count: > 0 } ribbons)
        {
            for (int i = 0; i < ribbons.Count; i++)
            {
                var (vx, vy, vz, vyaw) = ribbons[i];
                subLocators.Add(new LocationDescDataBuilder.SubLocSpec(
                    $"{challengeKey}_vis_{i + 2}",
                    Transform44.YawAt(vx, vy + VisLocatorLift, vz, vyaw)));
            }
        }

        subLocators.Add(new LocationDescDataBuilder.SubLocSpec(
            $"{challengeKey}_waitlocator",
            Transform44.YawAt(slx + WaitLocatorOffset * MathF.Cos(slYawRad), sly, slz - WaitLocatorOffset * MathF.Sin(slYawRad), slYaw)));

        // Anchor = in-world OTS arrow / mini-map signpost; keep it tied to
        // signup visual placement (not to signup prompt gating).
        var anchor = Transform44.YawAt(vsx, vsy, vsz, vsyaw);

        return new OtsLayoutResult(triggers, subLocators, anchor);
    }

    /// Build one axis-aligned trigger volume centred on (cx,cy,cz) with the
    /// given half-extents.
    private static OtsTriggerVolume MakeAxisAlignedVolume(
        string kind, string challengeKey, string worldStream, string distKey,
        float cx, float cy, float cz, float halfX, float halfY, float halfZ)
    {
        float minX = cx - halfX, maxX = cx + halfX;
        float minZ = cz - halfZ, maxZ = cz + halfZ;
        // CCW polygon (looking down +Y), starting at (minX, minZ).
        var polygon = new (float X, float Z)[]
        {
            (minX, minZ),
            (maxX, minZ),
            (maxX, maxZ),
            (minX, maxZ),
        };

        string canonicalName = $"{challengeKey}_{kind}";
        ulong guid = Lookup8Hash.HashString(canonicalName);
        ulong guidLocal = Lookup8Hash.HashString($"{canonicalName}_{distKey}");
        // Pipe-separated lookup name matches retail format; engine uses GuidLocal
        // to resolve, this is for human-readable debugging + tooling.
        string fullName = $"{worldStream}|{canonicalName}|0x{guidLocal:x16}";

        return new OtsTriggerVolume
        {
            Polygon = polygon,
            MinY = cy - halfY,
            MaxY = cy + halfY,
            Name = fullName,
            Guid = guid,
            GuidLocal = guidLocal,
            TriggerType = 0,
        };
    }
}
