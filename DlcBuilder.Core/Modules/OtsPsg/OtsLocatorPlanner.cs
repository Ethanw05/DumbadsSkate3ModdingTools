using ArenaBuilder.Core;
using DlcBuilder.Builders;
using DlcBuilder.Modules.LocatorPsg;

namespace DlcBuilder.Modules.OtsPsg;

/// Translates an `OtsChallengeSpec` into the per-mission cSim_Global PSG's
/// top-level locator set.
///
/// Each named sub-locator on the spec (optional chev_*, vis_1, startlocator,
/// waitlocator) becomes an independent `LocSpec` — `RegArena` only iterates
/// `m_LocationDescs` so names referenced through `tLocationID` (e.g.
/// `<key>_startlocator` from challenge_local_data.StartLocation) MUST live
/// at the top level or `cLocationManager::GetLocationInfo` returns NULL and
/// the engine writes an identity matrix into the spawn slot (player at
/// world origin facing yaw 0).
///
/// Verified against retail DW
/// content/missions/ots_dwmc_01/cSim_Global/5822CECF4EF38F6C.psg
/// (numLocs=6, numSub=12). DW packs 6 spawn-slot sub-locations under
/// `startlocator` and `waitlocator`; we mirror that for online lobby
/// compatibility even though single-player OTS only consumes the parent
/// transform.
public static class OtsLocatorPlanner
{
    /// Small XZ offsets (metres) for the 6 spawn slots that hang off
    /// startlocator / waitlocator. Same pattern as the freeskate locator's
    /// online lobby spawns.
    private static readonly (float Dx, float Dz)[] SpawnSlotOffsets =
    {
        (0f, 0f),    // slot 1 = parent position (single-player default)
        (1.5f, 0f),
        (-1.5f, 0f),
        (0f, 1.5f),
        (0f, -1.5f),
        (1.5f, 1.5f),
    };

    public static List<LocationDescDataBuilder.LocSpec> PlanMissionPsgLocators(
        OtsChallengeSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var result = new List<LocationDescDataBuilder.LocSpec>(spec.SubLocators.Count);

        foreach (var sub in spec.SubLocators)
        {
            // Locators with sub-spawn slots (engine indexes them by [0..5]).
            // Names hang off the parent name with `::` separator — same
            // convention freeskate locator spawns use, and the convention
            // RegArena's name walker preserves (sub-locations aren't
            // registered by name so they don't collide with the parent).
            var subSpawns = NeedsSpawnSlots(sub.Name)
                ? BuildSpawnSlots(spec.ChallengeKey, sub.Name, sub.Transform)
                : (IReadOnlyList<LocationDescDataBuilder.SubLocSpec>)Array.Empty<LocationDescDataBuilder.SubLocSpec>();

            result.Add(new LocationDescDataBuilder.LocSpec(
                Name: sub.Name,
                Description: "challenge",
                Transform: sub.Transform,
                Guid: Lookup8Hash.HashString(sub.Name),
                SubLocations: subSpawns));
        }

        return result;
    }

    private static bool NeedsSpawnSlots(string locatorName)
    {
        // Match DW shape: only startlocator and waitlocator carry the
        // 6 lobby-style spawn slots. chev_*, vis_* are bare top-level entries.
        return locatorName.EndsWith("_startlocator", StringComparison.Ordinal)
            || locatorName.EndsWith("_waitlocator", StringComparison.Ordinal);
    }

    private static List<LocationDescDataBuilder.SubLocSpec> BuildSpawnSlots(
        string challengeKey, string parentName, Transform44 parentTransform)
    {
        // DW sub-name pattern: `<parent>::<key>_start_<n>` for startlocator,
        // `<parent>::<key>_wait_<n>` for waitlocator. The token between key
        // and ordinal is the parent's role with the locator/word stripped.
        string role = parentName.EndsWith("_startlocator", StringComparison.Ordinal) ? "start" : "wait";

        var slots = new List<LocationDescDataBuilder.SubLocSpec>(SpawnSlotOffsets.Length);
        for (int i = 0; i < SpawnSlotOffsets.Length; i++)
        {
            var (dx, dz) = SpawnSlotOffsets[i];
            float yaw = MathF.Atan2(parentTransform.Rows[8], parentTransform.Rows[10]); // forward.x / forward.z
            float yawDeg = yaw * (180f / MathF.PI);
            float rx = dx * MathF.Cos(yaw) - dz * MathF.Sin(yaw);
            float rz = dx * MathF.Sin(yaw) + dz * MathF.Cos(yaw);
            var t = Transform44.YawAt(
                parentTransform.Tx + rx,
                parentTransform.Ty,
                parentTransform.Tz + rz,
                yawDeg);
            slots.Add(new LocationDescDataBuilder.SubLocSpec(
                Name: $"{parentName}::{challengeKey}_{role}_{i + 1}",
                Transform: t));
        }
        return slots;
    }
}
