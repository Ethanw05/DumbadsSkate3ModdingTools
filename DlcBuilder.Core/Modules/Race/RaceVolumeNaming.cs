using ArenaBuilder.Core;

namespace DlcBuilder.Modules.Race;

/// Canonical naming for per-race gate trigger volumes. Owned by `Modules/Race/`
/// because the names must agree across **two writers**:
///
///   1. <see cref="RaceMissionFolderWriter"/> — emits each gate as a
///      `pegasus::tTriggerInstance` in the per-race `cSim_Global` PSG. The
///      instance carries the volume's `Name` (written into the PSG name pool)
///      and `GuidLocal` (the runtime resolution key).
///
///   2. <see cref="DlcManifest.Vlt.RaceLocalDataVltBuilder"/> — emits each
///      gate's row in `challenge_race_gates/&lt;challengeKey&gt;_&lt;i&gt;` with a
///      `GateVolume` (16B `tTriggerVolumeInstanceID` = name-pool ptr + 8B
///      VolumeID). The 8B VolumeID MUST equal the PSG's `GuidLocal` and the
///      name string MUST equal the PSG's `Name` — otherwise
///      `cTriggerVolumeManager::Bind` can't match them at world load and the
///      gate never fires.
///
/// Naming follows the **DLC retail convention** from
/// `AttribDumpOut/dlc_race_dwgh_01/.../challenge_race_gates/*` cross-referenced
/// with the bin-pool string dump:
///
///   <c>&lt;WorldStreamName&gt;|&lt;challengeKey&gt;_racegate_&lt;NN&gt;|0x&lt;hexId&gt;</c>
///
/// Examples (race_dwgh_01 has 3 gates):
///   <c>DIST_DannyWayDLC|race_dwgh_01_racegate_01|0x2c701706003d0672</c>
///   <c>DIST_DannyWayDLC|race_dwgh_01_racegate_02|0x2c701706003d0673</c>
///   <c>DIST_DannyWayDLC|race_dwgh_01_racegate_FINISH|0x2c701706003d0674</c>
///
/// Three sub-conventions:
///   • Index is **1-based + zero-padded to 2 digits** (`_01`, `_02`, …).
///   • The **last gate is named `_FINISH`** instead of its numeric index.
///   • The trailing `0x&lt;hexId&gt;` is the same 64-bit value stored in
///     the VLT's tTriggerVolumeInstanceID.VolumeID field — name and ID must
///     agree.
///
/// Stock single-player races use `_spotvolume_<NN>` instead of `_racegate_<NN>`
/// (see `race_dwtn_01` in stock content) — that variant is left for a future
/// slice once we have a runtime confirmation that DLC races need the
/// `_racegate_` form for online dispatch.
public static class RaceVolumeNaming
{
    /// Bare per-gate name (no world prefix, no hex tail). Used as the input
    /// to the Lookup8 hash that produces the GuidLocal / VolumeID. Each
    /// gate's HullName ref + per-gate VLT row key also derive from this stem
    /// in retail (verified against the AttribDumpOut bin-string dump).
    public static string BareGateName(string challengeKey, int gateIndex, int totalGates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeKey);
        if (gateIndex < 0) throw new ArgumentOutOfRangeException(nameof(gateIndex));
        if (totalGates <= 0) throw new ArgumentOutOfRangeException(nameof(totalGates));
        bool isFinish = gateIndex == totalGates - 1;
        // 1-based numbering + zero-padded to match retail (`_01`, `_02`, …).
        string suffix = isFinish ? "FINISH" : (gateIndex + 1).ToString("D2");
        return $"{challengeKey}_racegate_{suffix}";
    }

    /// Full canonical engine-side name for the N-th gate in a race. Used as
    /// the trigger volume's `Name` field in the PSG and as the bin-pool
    /// string the VLT's `tTriggerVolumeInstanceID.VolumeName` points at. The
    /// embedded hex tail MUST match the VolumeID returned by
    /// <see cref="GateVolumeId"/> — engine resolves binds by matching the
    /// stored VolumeID against the PSG's `tTriggerInstance.m_uiGuidLocal`,
    /// and the hex tail in the name doubles as a debug fingerprint that
    /// must agree with both.
    public static string CanonicalGateName(
        string worldStreamName, string challengeKey, int gateIndex, int totalGates, string distKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldStreamName);
        ArgumentException.ThrowIfNullOrWhiteSpace(distKey);
        string bare = BareGateName(challengeKey, gateIndex, totalGates);
        ulong volumeId = ComputeGateVolumeId(bare, distKey);
        return $"{worldStreamName}|{bare}|0x{volumeId:x16}";
    }

    /// Engine-side `GuidLocal` for a per-race gate — what the VLT's
    /// `tTriggerVolumeInstanceID.VolumeID` field stores and what
    /// `cTriggerVolumeManager::Bind` matches against the PSG's
    /// `tTriggerInstance.m_uiGuidLocal`. Per-map scoping (the `_{distKey}`
    /// suffix in the hash input) prevents two races on different maps from
    /// binding to the same VolumeID by accident.
    ///
    /// TODO: stock retail VolumeIDs follow `0x2c7017xx00xxxxxx` — a
    /// `(world-stream-id, per-volume-index)` pair allocated at content-bake
    /// time. Our `Lookup8` derivation produces unrelated values; this is
    /// internally consistent (PSG ↔ VLT match each other) but won't match
    /// retail's engine-side allocator. Needs IDA verification.
    public static ulong GateVolumeId(string challengeKey, int gateIndex, int totalGates, string distKey)
    {
        string bare = BareGateName(challengeKey, gateIndex, totalGates);
        return ComputeGateVolumeId(bare, distKey);
    }

    /// Engine-side `Guid` (global, not per-map). PSG's
    /// `tTriggerInstance.m_uiGuid` + TOC's `TriggerInstanceSubref.Guid`.
    /// Follows the OTS pattern: `Lookup8(bareName)` with no distKey suffix.
    public static ulong GateGuid(string challengeKey, int gateIndex, int totalGates) =>
        Lookup8Hash.HashString(BareGateName(challengeKey, gateIndex, totalGates));

    private static ulong ComputeGateVolumeId(string bareName, string distKey) =>
        Lookup8Hash.HashString($"{bareName}_{distKey}");
}
