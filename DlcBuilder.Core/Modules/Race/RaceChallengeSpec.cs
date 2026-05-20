using System.Numerics;
using ArenaBuilder.Core;
using DlcBuilder.Modules.DlcManifest;

namespace DlcBuilder.Modules.Race;

/// Declarative description of one race (or "death race" — the online variant)
/// challenge. Parallels <see cref="OtsPsg.OtsChallengeSpec"/> for the OTS
/// pipeline, but races have a different shape on disk:
///
///   • OTS produces a per-mission folder (boundary.xml + stream.xml + Sim.loc
///     + cSim_Global.psg) PLUS per-OTS challenge_local_data VLT. The XML
///     companions wire the engine's discoverable-marker / streaming systems.
///   • Race produces ONLY a per-race challenge_local_data VLT (+ companion
///     .bin). Gates ARE trigger volumes already authored on the world — no
///     PSG body, no Sim.loc, no boundary polygon, no mission folder.
///
/// The VLT itself is dense, though: each race ships four classes' worth of
/// rows in one file (challenge_local_data, challenge_race_gates,
/// challenge_race_legs, challenge_race_heats) following the inheritance
/// chain  default → dlc_&lt;key&gt; → dlc_&lt;key&gt;_races → race_&lt;name&gt;
/// parent → individual gate / leg / heat instance rows. The
/// <c>dlc_&lt;key&gt;_races</c> family row is what carries the death-race UI
/// strings (<c>ID_MISSION_TEMPLATE_DEATH_RACE_TITLE</c>,
/// <c>ID_MISSION_DEATHRACE_CHALLENGE_DESCRIPTION</c>) — these mirror the
/// stock <c>races</c> family row byte-for-byte in
/// <c>AttribDumpOut/dlc_dwgh_challengebanks/Dump/Skate3_skater/Collections/challenge_global_data/dlc_dwgh_races.xml</c>.
///
/// Byte-level reference for instance rows:
/// <c>AttribDumpOut/dlc_race_dwgh_01/Dump/Skate3_skater/Collections/</c> in
/// the StockGameData DannyWay DLC.
public sealed record RaceChallengeSpec
{
    /// Engine-side challenge key. Drives VLT row keys, FE HAL ids, and the
    /// challengebanks instance row key. We emit ONE row set per race — no
    /// `_ol` companion. Stock retail ships separate `_ol` variants but the
    /// engine surfaces a single `IsDeathRace=true` race in BOTH the offline
    /// challenge menu AND the online multiplayer menu, so emitting both is
    /// redundant and produces a duplicate menu entry.
    public required string ChallengeKey { get; init; }

    /// True when this race should also surface in the online multiplayer
    /// "death race" menu. The engine reads this off the challenge family
    /// chain (`dlc_&lt;key&gt;_races` family row carries the death-race UI
    /// strings) and lists the race in both the single-player race menu and
    /// the multiplayer-race menu. Authoring-only flag; not directly written
    /// to a VLT field.
    public bool IsDeathRace { get; init; }

    /// The map this race belongs to. Provides DistKey, WorldStreamName, and
    /// the world's MapCategory ref used when writing the challenge row.
    public required DlcSpec Map { get; init; }

    /// Display title shown in the FE menu, e.g. "Embarcadero Race".
    /// Materialised via the per-instance `ID_CHALLENGE_<KEY>_TITLE` HALID.
    public required string DisplayTitle { get; init; }

    /// Short description shown under the title. The instance row leaves
    /// this blank and inherits `ID_MISSION_DEATHRACE_CHALLENGE_DESCRIPTION`
    /// (death race) from the `dlc_<key>_races` family row.
    public required string Description { get; init; }

    /// When true (default), the engine lets a player who skips a gate
    /// continue forward instead of failing the heat. Maps to
    /// `RaceGateSkipable` on the instance challenge_local_data row.
    public bool RaceGateSkipable { get; init; } = true;

    /// Race anchor location (4-byte tLocationID). Stock rows store a small
    /// id that resolves through the FE / world spawn-locator catalogue; we
    /// pin it to the resolved start-locator position so the engine teleports
    /// the player there at heat-start. When the input has no
    /// `StartLocatorId`, the orchestrator falls back to the first gate's
    /// volume centre and yaw 0.
    public Vector3 AnchorPosition { get; init; } = Vector3.Zero;
    public float AnchorYawDegrees { get; init; }

    /// Heats in run order. Each heat owns its legs, each leg its gates.
    /// Stock single-player races have exactly 1 heat; the multiplayer death
    /// race variant uses 1 heat too. Multi-heat configurations exist
    /// upstream but are rare.
    public required IReadOnlyList<RaceHeatSpec> Heats { get; init; }

    // ── Derived names / hashes ─────────────────────────────────────────────

    /// HAL ID for the FE Title field on the per-instance challenge_global_data row.
    public string TitleHalId => $"ID_CHALLENGE_{ChallengeKey.ToUpperInvariant()}_TITLE";

    /// HAL ID for the FE Description field (rarely populated — the family
    /// row owns the canonical death-race description).
    public string DescHalId => $"ID_CHALLENGE_{ChallengeKey.ToUpperInvariant()}_DESC";

    /// Lookup8 hash of the challenge key. Used as the row hash for the
    /// race instance parent across all four classes:
    /// challenges / challenge_global_data / challenge_local_data /
    /// challenge_race_gates / _heats / _legs.
    public ulong ChallengeKeyHash => Lookup8Hash.HashString(ChallengeKey);

    /// Stable per-heat key. Mirrors the stock pattern
    /// `race_dwgh_01_0`, `race_dwgh_01_1`, … — one key per heat.
    public string HeatKey(int heatIndex) => $"{ChallengeKey}_{heatIndex}";

    /// Stable per-leg key, namespaced under the heat key. Stock uses a flat
    /// index across the race; we follow the same convention so multiple
    /// legs in the same heat get sequential indices.
    public string LegKey(int legIndex) => $"{ChallengeKey}_{legIndex}";

    /// Stable per-gate key. Stock uses race-flat numbering
    /// (`race_dwgh_01_0`, `_1`, … up to the total gate count).
    public string GateKey(int gateIndex) => $"{ChallengeKey}_{gateIndex}";

    /// Convenience: flat enumeration of every gate across heats/legs in
    /// run order. Index in this enumeration equals the per-gate index
    /// used in <see cref="GateKey"/> and in the local_data VisualIndicators
    /// array layout.
    public IEnumerable<RaceGateSpec> AllGates
    {
        get
        {
            foreach (var h in Heats)
                foreach (var l in h.Legs)
                    foreach (var g in l.Gates)
                        yield return g;
        }
    }

    /// Total gate count across all heats and legs.
    public int TotalGateCount
    {
        get
        {
            int n = 0;
            foreach (var h in Heats)
                foreach (var l in h.Legs)
                    n += l.Gates.Count;
            return n;
        }
    }
}

/// One heat in a race. Owns its legs in run order plus the heat-wide
/// timing. Mirrors the stock challenge_race_heats row.
public sealed record RaceHeatSpec
{
    /// Legs in run order. Most stock races have exactly one leg per heat.
    public required IReadOnlyList<RaceLegSpec> Legs { get; init; }

    /// Heat timer (whole seconds). Maps to `TimeLimit` on
    /// challenge_race_heats. The clock ticks down each frame and the heat
    /// fails when it reaches 0 — gates can add seconds via
    /// <see cref="RaceGateSpec.TimeBonusSeconds"/>.
    public required int TimeLimitSeconds { get; init; }

    /// Time-to-beat for the "Killed It" tier (seconds, may be fractional).
    /// Maps to `KilledItTime` on challenge_race_heats. Stock races use
    /// values like 90.0 / 144.0 — well below the heat time limit so the
    /// tier is reachable but tight.
    public required float KilledItSeconds { get; init; }

    /// Per-heat start position override. Null = inherit the race's
    /// <see cref="RaceChallengeSpec.AnchorPosition"/>. Stock retail mostly
    /// leaves this at the race default.
    public Vector3? StartPosition { get; init; }
    public float? StartYawDegrees { get; init; }
}

/// One leg of a race heat. Owns its gates in run order plus optional
/// sectional-timing trigger volumes. Mirrors the stock
/// challenge_race_legs row.
public sealed record RaceLegSpec
{
    /// Gates in run order. The first gate of a leg is its start checkpoint;
    /// the last is its finish. The engine flips through gates as the
    /// player passes through each gate volume.
    public required IReadOnlyList<RaceGateSpec> Gates { get; init; }

    /// Optional split-time trigger volumes. The engine records a split-time
    /// timestamp each time the player passes through one. Empty in most
    /// stock races; populated for races with sectional leaderboards.
    public IReadOnlyList<TriggerVolumeRef> SplitTimeTriggers { get; init; } =
        Array.Empty<TriggerVolumeRef>();
}

/// One gate (checkpoint) along a leg. The gate's `Volume` is the trigger
/// volume the player must pass through; the engine's gate hit-test reads
/// the volume's centre + half-extents at runtime.
public sealed record RaceGateSpec
{
    /// The trigger volume defining the gate's AABB. Resolved from the
    /// authored `MapInput.TriggerVolumes` by
    /// <see cref="RaceSpecBuilder.FromChallengeInput"/>.
    public required TriggerVolumeRef Volume { get; init; }

    /// Seconds added to the heat clock when the gate fires. Stock races
    /// usually leave this at 0 (heat-level timing dominates) but per-gate
    /// bonuses exist for branching tracks.
    public int TimeBonusSeconds { get; init; }
}

/// Resolved reference to a trigger volume. Carries the data the
/// race-VLT byte writers need to assemble a `Sk8::Challenge::tTriggerVolumeInstanceID`
/// (16-byte struct: 4-byte name pool offset + 12-byte volume hash).
public sealed record TriggerVolumeRef
{
    /// Canonical name of the volume — feeds the bin-pool string ref at
    /// offset +0 of `tTriggerVolumeInstanceID`. Race gates conventionally
    /// reuse the author-given volume name; canonical-name munging happens
    /// upstream in the orchestrator pass.
    public required string Name { get; init; }

    /// World-space centre. Engine reads this through the engine-side
    /// trigger-volume catalogue, not the VLT row — but writers occasionally
    /// need it for sanity / fallback fixups.
    public required Vector3 Center { get; init; }

    /// World-space half-extents.
    public required Vector3 HalfExtents { get; init; }

    /// Optional yaw (degrees). Race gates are axis-aligned in most stock
    /// races; non-zero yaw is supported by tTriggerVolumeInstanceID but
    /// rarely used.
    public float YawDegrees { get; init; }
}
