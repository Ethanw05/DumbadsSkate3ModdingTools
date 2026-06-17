using System.Numerics;
using ArenaBuilder.Core;
using DlcBuilder.Builders;
using DlcBuilder.Modules.LocatorPsg;
using DlcBuilder.Modules.MissionTemplates;
using DlcBuilder.Modules.OtsPsg;

namespace DlcBuilder.Modules.Race;

/// Writes the per-race mission folder under
/// `&lt;outputDirectory&gt;/content/missions/&lt;challengeKey&gt;/`:
///
///   • 4 Pres manifest stubs (`&lt;key&gt;_Pres.{pmm,psm,pss,pst}`)
///   • 4 Tex manifest stubs (`&lt;key&gt;_Tex.{pmm,psm,pss,pst}`)
///   • `cSim_Global/&lt;hash&gt;.psg` — gate-volume PSG body. Stream File Tool
///     packs this into `cSim_Global.psf` during the regular OtsPsfPacker
///     mission-folder sweep (the packer is generic — any folder with a
///     `cSim_Global` subfolder gets packed).
///
/// Deliberately skips the 4 Sim stubs (`&lt;key&gt;_Sim.{pmm,psm,pss,pst}`) that
/// the OTS writer copies — for races the `cSim_Global.psf` supersedes the
/// loose Sim stub set. Stock retail (`race_dwtn_01`) ships all 12, but the
/// 4 Sim stubs are redundant for runtime: the engine's sim-asset stream
/// loads `cSim_Global.psf` directly, not the loose Sim siblings.
///
/// Byte-level reference (stock):
/// `StockGameData/StockGameData/content/missions/race_dwtn_01/`
/// (note: stock ships all 12 stubs; we ship only 8 by your direction).
public static class RaceMissionFolderWriter
{
    public static void Write(RaceChallengeSpec spec, string outputDirectory, IList<string> writtenFiles,
        PlatformProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(writtenFiles);
        profile ??= PlatformProfile.Ps3;

        string missionDir = Path.Combine(outputDirectory, "content", "missions", spec.ChallengeKey);
        Directory.CreateDirectory(missionDir);

        // ── 1) Pres + Tex stubs (8 files). Skip Sim — cSim_Global.psf does its job. ──
        string[] suffixes =
        {
            "_Pres.pmm", "_Pres.psm", "_Pres.pss", "_Pres.pst",
            "_Tex.pmm",  "_Tex.psm",  "_Tex.pss",  "_Tex.pst",
        };
        foreach (string suffix in suffixes)
        {
            string dst = Path.Combine(missionDir, spec.ChallengeKey + suffix);
            if (MissionTemplateProvider.TryGetTemplateBytes(suffix, out byte[] templateBytes))
            {
                File.WriteAllBytes(dst, templateBytes);
                writtenFiles.Add(dst);
            }
            // Silently skip missing templates (mirrors OtsMissionFolderWriter
            // behaviour). Templates may not be embedded yet in dev environments
            // — the build still produces a valid folder structure for diffing.
        }

        // ── 2) cSim_Global/<hash>.psg — gate-volume PSG body ─────────────────
        // Each race gate becomes a `pegasus::tTriggerInstance` whose name + GUID
        // match the `challenge_race_gates.<n>.GateVolume.tTriggerVolumeInstanceID`
        // pair that the engine looks up via `cTriggerVolumeManager::Bind`. The
        // name convention follows stock `race_dwtn_01`:
        //   <race_key>_spotvolume_<NN> for numbered gates,
        //   <race_key>_spotvolume_FINISH for the last gate,
        //   <race_key>_start_clear_volume for the clear-volume at race start.
        //
        // Gate N−1 is named _FINISH by RaceVolumeNaming.BareGateName.
        string cSimDir = Path.Combine(missionDir, "cSim_Global");
        Directory.CreateDirectory(cSimDir);
        ulong psgHash = Lookup8Hash.HashString($"{spec.ChallengeKey}_cSim_Global");
        string psgPath = Path.Combine(cSimDir, $"{psgHash:X16}{profile.PsgExt}");

        // Synthesize OtsTriggerVolume per race gate. The OTS PSG builder
        // accepts arbitrary convex polygons; we feed the gate's AABB as a
        // 4-corner XZ rectangle and the AABB's Y range as the slab.
        int totalGates = spec.TotalGateCount;
        var otsTriggers = new List<OtsTriggerVolume>(totalGates);
        int gateIndex = 0;
        foreach (var gate in spec.AllGates)
        {
            otsTriggers.Add(BuildGateTriggerVolume(spec, gateIndex, totalGates, gate));
            gateIndex++;
        }

        // Register the spawn locator under the SAME name the VLT's
        // `challenge_local_data.<key>.StartLocation` (and each heat's
        // `challenge_race_heats.<key>_<i>.StartLocation`) looks up at
        // runtime — i.e. `<challengeKey>_startlocator`. RaceLocalDataVltBuilder
        // (line ~200) writes `StartLocation` as a tLocationID bin pointer to
        // exactly this string; if the PSG registers the locator under a
        // different name (`<key>_anchor`, etc.), `cLocationManager::FindLocation`
        // returns NULL and the player spawns at world origin.
        //
        // For per-heat overrides (`RaceHeatInput.StartLocatorId`),
        // RaceLocalDataVltBuilder writes `<key>_<heatIdx>_startlocator` — but
        // we don't register those here yet. Multi-heat races with per-heat
        // spawn overrides need each heat's locator registered in this PSG;
        // for single-heat races (every shipping retail race) one locator
        // suffices.
        // The race start locator + 6 sub-spawn slots. The sub-slots are
        // critical for the multiplayer / death-race variant — the engine
        // teleports the player + 5 AI / online racers into slots 1..6 at heat
        // start. Without them, only the single-player path (using the parent
        // transform alone) works. Stock `race_dwtn_01` ships exactly this
        // shape: 1 parent startlocator + 6 `spawnpoint_<NN>` sub-locations.
        string startName = $"{spec.ChallengeKey}_startlocator";
        Transform44 startTransform = BuildAnchorTransform(spec);
        var locators = new List<LocationDescDataBuilder.LocSpec>
        {
            new LocationDescDataBuilder.LocSpec(
                Name: startName,
                Description: spec.DisplayTitle,
                Transform: startTransform,
                Guid: Lookup8Hash.HashString(startName),
                SubLocations: BuildRaceStartSpawnSlots(startName, startTransform)),
        };

        // Per-heat start locators (when heats override the challenge's spawn).
        // Each per-heat locator also gets its own 6-slot starting grid — for
        // multi-heat races where each heat starts the racers at a different
        // position. Stock single-heat races leave this list empty.
        for (int hi = 0; hi < spec.Heats.Count; hi++)
        {
            if (spec.Heats[hi].StartPosition is not Vector3 hp) continue;
            float hyaw = spec.Heats[hi].StartYawDegrees ?? 0f;
            string heatName = $"{spec.ChallengeKey}_{hi}_startlocator";
            Transform44 heatTransform = Transform44.YawAt(hp.X, hp.Y, hp.Z, hyaw);
            locators.Add(new LocationDescDataBuilder.LocSpec(
                Name: heatName,
                Description: $"{spec.DisplayTitle} heat {hi}",
                Transform: heatTransform,
                Guid: Lookup8Hash.HashString(heatName),
                SubLocations: BuildRaceStartSpawnSlots(heatName, heatTransform)));
        }

        // Per-gate VI locators (and the finish-only VI locator attempt) were
        // both removed: each one surfaced an unwanted duplicate visual on
        // top of the trigger-volume archway without affecting race-end logic.

        // End-camera locator (`<key>_endcamera`). The VLT's
        // `OnlineEndCameraLocation` resolves this name via
        // cLocationManager::FindLocation. Without a matching PSG entry the
        // engine falls back to world origin (0,0,0) and spawns a phantom
        // visual indicator there. Place it at the finish gate's centre.
        {
            var lastGate = spec.AllGates.Last();
            Vector3 endPos = lastGate.Volume.Center;
            float endYaw = ComputeGateVisualIndicatorYaw(lastGate, spec.AnchorYawDegrees);
            string endCamName = $"{spec.ChallengeKey}_endcamera";
            locators.Add(new LocationDescDataBuilder.LocSpec(
                Name: endCamName,
                Description: $"{spec.DisplayTitle} end camera",
                Transform: Transform44.YawAt(endPos.X, endPos.Y, endPos.Z, endYaw),
                Guid: Lookup8Hash.HashString(endCamName),
                SubLocations: Array.Empty<LocationDescDataBuilder.SubLocSpec>()));
        }

        if (otsTriggers.Count == 0)
        {
            // Defensive: no gates means no PSG. Skip silently — the race
            // validator already errors on an empty gate list, so reaching
            // here means the build is producing a deliberately empty folder.
            return;
        }

        using (var fs = File.Create(psgPath))
            OtsPsgBytesBuilder.Build(spec.ChallengeKey, otsTriggers, locators, fs, profile.Arena);
        writtenFiles.Add(psgPath);
    }

    /// Build a retail-style `OtsTriggerVolume` for one race gate. Polygon is a
    /// 4-corner XZ rectangle derived from the gate's centre, half-extents, and
    /// authored yaw. Name follows DLC retail convention
    /// (`<WorldStream>|<key>_racegate_<NN>|0x<hexId>`, with `_FINISH` for the
    /// last gate) so the downstream
    /// `challenge_race_gates.<i>.GateVolume.tTriggerVolumeInstanceID`
    /// (16B = name-pool ptr + 8B VolumeID) resolves at runtime through
    /// `cTriggerVolumeManager::Bind` against the matching tTriggerInstance.
    private static OtsTriggerVolume BuildGateTriggerVolume(
        RaceChallengeSpec spec, int gateIndex, int totalGates, RaceGateSpec gate)
    {
        Vector3 c = gate.Volume.Center;
        Vector3 h = gate.Volume.HalfExtents;

        // Pad small half-extents — a strictly zero-extent rect collapses to a
        // line and fan-triangulation degenerates. Stock gates are at least
        // a few metres on each axis; we apply a 1 cm floor.
        float hx = MathF.Max(h.X, 0.01f);
        float hz = MathF.Max(h.Z, 0.01f);
        float hy = MathF.Max(h.Y, 0.01f);

        // Rotate the four local-frame corners into world XZ using the gate's
        // authored yaw. Skate uses row-vector convention (worldPos = localPos × M)
        // so with M = Transform44.YawAt(yaw):
        //   wx = cx + lx*cos(yaw) + lz*sin(yaw)
        //   wz = cz − lx*sin(yaw) + lz*cos(yaw)
        // At yaw=0 this collapses to the plain AABB. Gates with non-zero yaw
        // (ch_vol_03 at −47.84°, ch_vol_04 at −89.30°, ch_vol_05 at 91.08°)
        // need the rotation baked in so cTriggerVolumeManager fires when the
        // player passes through the actual gate opening, not an axis-aligned ghost.
        float yawRad = gate.Volume.YawDegrees * MathF.PI / 180f;
        float cosY   = MathF.Cos(yawRad);
        float sinY   = MathF.Sin(yawRad);
        var polygon = new (float X, float Z)[]
        {
            (c.X - hx*cosY - hz*sinY, c.Z + hx*sinY - hz*cosY),
            (c.X + hx*cosY - hz*sinY, c.Z - hx*sinY - hz*cosY),
            (c.X + hx*cosY + hz*sinY, c.Z - hx*sinY + hz*cosY),
            (c.X - hx*cosY + hz*sinY, c.Z + hx*sinY + hz*cosY),
        };

        // Naming + GUID formulas centralised in RaceVolumeNaming so the
        // matching `challenge_race_gates.<i>.GateVolume.tTriggerVolumeInstanceID`
        // in RaceLocalDataVltBuilder uses identical name + VolumeID — engine
        // binds the two via these fields.
        string volumeName = RaceVolumeNaming.CanonicalGateName(
            spec.Map.WorldStreamName, spec.ChallengeKey, gateIndex, totalGates, spec.Map.DistKey);
        ulong guid      = RaceVolumeNaming.GateGuid(spec.ChallengeKey, gateIndex, totalGates);
        ulong guidLocal = RaceVolumeNaming.GateVolumeId(
            spec.ChallengeKey, gateIndex, totalGates, spec.Map.DistKey);

        return new OtsTriggerVolume
        {
            Polygon = polygon,
            MinY = c.Y - hy,
            MaxY = c.Y + hy,
            Name = volumeName,
            Guid = guid,
            GuidLocal = guidLocal,
            TriggerType = 0,
            YawRadians = yawRad,
        };
    }

    /// Anchor transform for the placeholder locator entry. Centred on the
    /// race's resolved start position with the race's yaw applied as a
    /// Y-axis rotation. Uses the canonical `Transform44.YawAt` helper.
    private static Transform44 BuildAnchorTransform(RaceChallengeSpec spec) =>
        Transform44.YawAt(
            spec.AnchorPosition.X,
            spec.AnchorPosition.Y,
            spec.AnchorPosition.Z,
            spec.AnchorYawDegrees);

    /// Yaw for a `_vi_<NN>` Gate Visual Indicator locator.
    ///
    /// The engine's `Sk8::Challenge::cVisualIndicatorGroup::AddLocationIndicator`
    /// copies the locator's FULL 4×4 transform (rotation + position) straight
    /// into the indicator (verified via IDA — see the caller). So the in-game
    /// archway is oriented by exactly this locator's yaw.
    ///
    /// Archway model: posts span local +X, player runs through local +Z.
    /// At yaw=0 the posts span world-X and the run-through faces world-Z.
    ///
    /// When hx > hz the gate's wide axis is already X — the archway posts
    /// naturally align with the gate, so VI yaw = gate yaw (no offset).
    /// When hz > hx the wide axis is Z — rotate the archway 90° so the
    /// posts span the Z extent instead.
    ///
    /// Verified against stock base-game PSG dumps (race_dwtn_01).
    internal static float ComputeGateVisualIndicatorYaw(RaceGateSpec gate, float fallbackYaw)
    {
        float hx = gate.Volume.HalfExtents.X;
        float hz = gate.Volume.HalfExtents.Z;
        float gateYaw = gate.Volume.YawDegrees;

        if (hx > hz)
            return gateYaw;
        if (hz > hx)
            return gateYaw + 90f;
        return gateYaw != 0f ? gateYaw : fallbackYaw;
    }

    /// Build the 6-slot starting grid that hangs off the race start locator.
    /// Mirrors the stock retail shape: 6 `spawnpoint_<NN>` sub-locations
    /// arranged in a 2-row × 3-column grid in the parent's local frame, with
    /// row 1 at the start line (pole + flanks) and row 2 two metres behind.
    ///
    /// The engine indexes these slots by position 0..5 at heat start to place
    /// player + 5 AI / online racers. The single-player path uses slot 0
    /// (pole position = parent transform). Without this list, only one racer
    /// can spawn and the death-race variant can't start.
    ///
    /// Yaw is extracted from the parent's transform matrix the same way
    /// <see cref="OtsPsg.OtsLocatorPlanner"/> does (Rows[8/10] → atan2). The
    /// local-frame offsets are rotated into world space so the grid always
    /// faces the parent's forward direction regardless of the race's
    /// orientation in the level.
    private static IReadOnlyList<LocationDescDataBuilder.SubLocSpec> BuildRaceStartSpawnSlots(
        string parentName, Transform44 parentTransform)
    {
        // Match the OTS extractor's convention so per-axis sign agrees.
        float yawRad = MathF.Atan2(parentTransform.Rows[8], parentTransform.Rows[10]);
        float yawDeg = yawRad * (180f / MathF.PI);
        float cosY = MathF.Cos(yawRad);
        float sinY = MathF.Sin(yawRad);

        // Local-frame offsets (metres). +X = right, +Z = forward (Skate Y-up).
        // Slots staged BEHIND the parent — the parent IS the start line.
        // Slot 01 (pole position) sits at the parent itself so single-player
        // / lone-racer paths reuse the same transform.
        (float Lx, float Lz)[] offsets =
        {
            (0f, 0f),       // 01 pole
            (1.5f, 0f),     // 02 right of pole
            (-1.5f, 0f),    // 03 left of pole
            (0f, -2f),      // 04 row 2 centre
            (1.5f, -2f),    // 05 row 2 right
            (-1.5f, -2f),   // 06 row 2 left
        };

        var slots = new List<LocationDescDataBuilder.SubLocSpec>(offsets.Length);
        for (int i = 0; i < offsets.Length; i++)
        {
            var (lx, lz) = offsets[i];
            // Rotate local (lx, lz) into world XZ around parent's yaw.
            float wx = lx * cosY - lz * sinY;
            float wz = lx * sinY + lz * cosY;
            var t = Transform44.YawAt(
                parentTransform.Tx + wx,
                parentTransform.Ty,
                parentTransform.Tz + wz,
                yawDeg);
            slots.Add(new LocationDescDataBuilder.SubLocSpec(
                Name: $"{parentName}::spawnpoint_{i + 1:D2}",
                Transform: t));
        }
        return slots;
    }
}
