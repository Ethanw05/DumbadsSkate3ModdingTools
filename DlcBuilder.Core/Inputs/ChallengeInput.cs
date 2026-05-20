namespace DlcBuilder.Inputs;

public enum ChallengeKind
{
    Ots,
    Otl,
    Photo,
    Film,
    Race,
}

/// One OTS-style challenge authored in the editor. References to other authored
/// objects use stable Guids assigned by the front-end so the builder can resolve
/// links without sharing the editor's CLR types.
public sealed record ChallengeInput
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required ChallengeKind Kind { get; init; }

    /// The locator the player teleports to when starting the challenge. Null
    /// means "use the player's current position" (rare).
    public Guid? StartLocatorId { get; init; }

    /// The trigger volume that scores points (e.g. the ledge / rail bounding box).
    public Guid? ScoringVolumeId { get; init; }

    /// The trigger volume used by challenge_local_data.DiscoveryBoundary.
    /// Null falls back to ScoringVolumeId.
    public Guid? DiscoveryBoundaryId { get; init; }

    /// The trigger volume that fails the run if the player exits it.
    public Guid? ChallengeBoundaryId { get; init; }

    /// Optional locator that controls the signup-phase ribbon-arrow indicator location.
    public Guid? VisualSignupLocatorId { get; init; }

    /// Optional extra ribbon-arrow locators shown during the challenge (same asset family as signup).
    public IReadOnlyList<Guid> InChallengeRibbonArrowLocatorIds { get; init; } =
        Array.Empty<Guid>();

    /// Optional chevron trail locators (ordered).
    public IReadOnlyList<Guid> ChevronLocatorIds { get; init; } =
        Array.Empty<Guid>();

    public int OwnedPoints { get; init; } = 250;
    public int KilledItPoints { get; init; } = 500;
    public int OnlineBonusXp { get; init; } = 1000;

    // ─── Race-only inputs ──────────────────────────────────────────────────
    // Used when Kind == ChallengeKind.Race. Ignored for every other kind.
    // The stock data model is hierarchical: race → heats → legs → gates,
    // matching the EA AttribSys classes (challenge_race_heats,
    // challenge_race_legs, challenge_race_gates) one-to-one.

    /// One or more heats. Stock single-player races have exactly 1 heat; the
    /// online "death race" variant also uses 1 heat but with different audio
    /// fields populated. Multi-heat races exist in stock data but are rare.
    public IReadOnlyList<RaceHeatInput> RaceHeats { get; init; } = Array.Empty<RaceHeatInput>();

    /// When true (default), players who skip past a gate continue forward
    /// instead of failing. Maps to `RaceGateSkipable` on the
    /// `challenge_local_data` row.
    public bool RaceGateSkipable { get; init; } = true;

    /// When true, this is the online death-race variant. The on-disk shape is
    /// the same (heats / legs / gates) but `challenge_local_data` populates
    /// `IntroNIS` + `OutroNIS` instead of `AudioPlayerQuitChallenge`, and the
    /// produced key/filename is suffixed `_ol`.
    public bool IsDeathRace { get; init; }
}

/// One gate (checkpoint) along a race leg. The trigger volume is the AABB
/// the player must pass through; the time bonus is added to the remaining
/// heat clock when the gate fires.
public sealed record RaceGateInput
{
    /// Gate trigger volume. Must resolve to a `TriggerVolumeInput` on the
    /// owning `MapInput`. The volume's center + half-extents drive the
    /// engine-side gate hit test.
    public required Guid TriggerVolumeId { get; init; }

    /// Seconds added to the heat clock when this gate is hit. Stock races
    /// usually leave this at 0 and rely on heat-level `TimeLimitSeconds`.
    public int TimeBonusSeconds { get; init; }
}

/// One leg — an ordered list of gates the player must hit in sequence. A
/// heat is a sequence of legs; most races have one leg per heat. Legs also
/// own split-time trigger volumes used by the engine for sectional timing.
public sealed record RaceLegInput
{
    /// Gates in run order. The first gate is the leg's start checkpoint;
    /// the last is the leg's finish.
    public required IReadOnlyList<RaceGateInput> Gates { get; init; }

    /// Optional split-time trigger volumes (sectional timing). Empty in
    /// most stock races.
    public IReadOnlyList<Guid> SplitTimeTriggerVolumeIds { get; init; } =
        Array.Empty<Guid>();
}

/// One heat — an ordered list of legs with a heat-wide time limit. Stock
/// races run a single heat; multi-heat configurations exist for race series.
public sealed record RaceHeatInput
{
    /// Legs in run order. Stock races usually have 1 leg per heat.
    public required IReadOnlyList<RaceLegInput> Legs { get; init; }

    /// Heat timer (whole seconds). Falls to 0 → heat fails.
    /// Maps to `TimeLimit` on `challenge_race_heats`.
    public int TimeLimitSeconds { get; init; } = 180;

    /// Time-to-beat for the "Killed It" tier (seconds, may be fractional).
    /// Maps to `KilledItTime` on `challenge_race_heats`.
    public float KilledItSeconds { get; init; } = 90f;

    /// Optional per-heat start locator override. Null = use challenge's
    /// `StartLocatorId`.
    public Guid? StartLocatorId { get; init; }
}
