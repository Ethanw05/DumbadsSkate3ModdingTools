using System.Numerics;
using DlcBuilder.Modules.DlcManifest;

namespace DlcBuilder.Modules.Skate;

/// One trigger volume referenced by a Skate spot. Owns the data the
/// VLT writers + PSG writer need to assemble matching
/// `tTriggerVolumeInstanceID` (VLT side) and `tTriggerInstance` (PSG side)
/// records — `Guid` is the 64-bit engine-resolution key, equal to the
/// PSG's `m_uiGuidLocal`.
public sealed record SkateTriggerVolume
{
    public required string Name { get; init; }
    public required Vector3 Center { get; init; }
    public required Vector3 HalfExtents { get; init; }
    public float YawDegrees { get; init; }
    /// 64-bit VolumeID; MUST match the PSG-side tTriggerInstance.m_uiGuidLocal.
    public required ulong Guid { get; init; }
}

/// Declarative description of one Game of S.K.A.T.E. spot. Own type — does
/// NOT share OtsChallengeSpec. S.K.A.T.E. is a distinct challenge type
/// (eChallengeType 0x04, base-game family `s_k_a_t_e`) with its own
/// per-spot spatial shape: 1-2 SpotVolumes + ChallengeBoundary +
/// TurnBasedStartVolume + start/wait locators + 1-2 visual indicators.
///
/// Anchored to the 10 base-game retail S.K.A.T.E. spots dumped at
/// `StockGameData/AttribDumpOut/skate_{dwtn_01..04, indu_01..03, univ_01..03}`.
/// Counts and naming vary across the 10 instances; this spec captures the
/// variation, so callers can mirror base shapes faithfully.
public sealed record SkateChallengeSpec
{
    /// Engine-side challenge key. Drives VLT row keys + per-instance
    /// challenge_local_data filename. Convention: `skate_<slug>`.
    public required string ChallengeKey { get; init; }

    /// The map this spot belongs to.
    public required DlcSpec Map { get; init; }

    /// FE menu title (used to derive `ID_CHALLENGE_<KEY>_TITLE`).
    public required string DisplayTitle { get; init; }

    /// FE menu description (used to derive `ID_CHALLENGE_<KEY>_DESC`).
    public required string Description { get; init; }

    /// 1 or 2 scoring volumes (matches base instance counts).
    public required IReadOnlyList<SkateTriggerVolume> SpotVolumes { get; init; }

    /// Outer "must stay inside" boundary.
    public required SkateTriggerVolume ChallengeBoundary { get; init; }

    /// Turn-based start volume.
    public required SkateTriggerVolume TurnBasedStartVolume { get; init; }

    /// Start locator.
    public required Vector3 StartLocatorPosition { get; init; }
    public required float StartLocatorYawDegrees { get; init; }

    /// Wait locator.
    public required Vector3 WaitLocatorPosition { get; init; }
    public required float WaitLocatorYawDegrees { get; init; }

    /// 1 or 2 visual indicators (ribbon arrows).
    public required IReadOnlyList<(Vector3 Position, float YawDegrees)> VisualIndicators { get; init; }

    /// Per-spot turn timer in seconds. Engine default = 15.0f.
    public float TimeLimitSeconds { get; init; } = 15f;

    /// True → emit the dwtn_01 profile per-instance challenge_global_data;
    /// false → emit the "rest" profile.
    public bool UseDwtn01Profile { get; init; }

    /// OwnedItReward amount when UseDwtn01Profile == false. Base ships 2500.
    public int OwnedItRewardCredits { get; init; } = 2500;

    // ── Derived ────────────────────────────────────────────────────────────

    public string TitleHalId => $"ID_CHALLENGE_{ChallengeKey.ToUpperInvariant()}_TITLE";
    public string DescHalId  => $"ID_CHALLENGE_{ChallengeKey.ToUpperInvariant()}_DESC";

    public string StartLocatorName => $"{ChallengeKey}_round01_startlocator_01";
    public string WaitLocatorName  => $"{ChallengeKey}_round01_waitlocator_01";
    public string VisualIndicatorName(int oneBasedIndex) => $"{ChallengeKey}_vi_{oneBasedIndex:D2}";
}
