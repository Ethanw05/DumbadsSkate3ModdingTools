using System.Numerics;
using DlcBuilder.Inputs;
using DlcBuilder.Modules.DlcManifest;
using DlcBuilder.Outputs;

namespace DlcBuilder.Modules.Race;

/// Resolves a public-API <see cref="ChallengeInput"/> (whose volume / locator
/// references are <see cref="Guid"/> handles) into a fully-baked
/// <see cref="RaceChallengeSpec"/> the race VLT writers can consume directly.
///
/// Parallels <see cref="OtsPsg.OtsPsgBuilder.FromChallengeInput"/> for the
/// OTS pipeline — same role, simpler output (no PSG body / no boundary
/// polygon / no sub-locator plan).
public static class RaceSpecBuilder
{
    /// Build a <see cref="RaceChallengeSpec"/> from authored input. Resolves
    /// every gate / split-time / start-locator <see cref="Guid"/> to the
    /// actual trigger volume / locator on the supplied <paramref name="map"/>.
    ///
    /// Validation has already run in <c>PackageInputValidator.ValidateRaceChallenge</c>
    /// before this is called, so each gate's <c>TriggerVolumeId</c> is
    /// guaranteed to resolve, the heat / leg / gate chain is non-empty, and
    /// time-limit / killed-it-time are sane. We still defensively fall back
    /// on resolution failures so a stale Guid produces a recognisable spec
    /// (zero-extent volume named <c>"&lt;missing&gt;"</c>) instead of throwing.
    /// Build a single `RaceChallengeSpec` from authored input. Stock retail
    /// ships two physical challenges per death race (`race_<key>` +
    /// `race_<key>_ol`) but the engine actually surfaces an `IsDeathRace`
    /// race in BOTH menus off a single row set, so emitting both is redundant
    /// and produces a duplicate FE entry. We emit one offline-keyed VLT row
    /// set and let the death-race family-row chain do the dual-menu surfacing.
    public static RaceChallengeSpec FromChallengeInput(
        ChallengeInput input,
        MapInput map,
        DlcSpec mapSpec,
        IList<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(mapSpec);
        ArgumentNullException.ThrowIfNull(diagnostics);

        // Challenge key — `race_<slug>` (stock retail naming convention).
        string slugged = ToSlug(input.Name);
        string challengeKey = string.IsNullOrEmpty(slugged)
            ? $"race_{mapSpec.Slug}"
            : (slugged.StartsWith("race_", StringComparison.Ordinal) ? slugged : "race_" + slugged);

        var volumesById = map.TriggerVolumes.ToDictionary(v => v.Id);
        var locatorsById = map.Locators.ToDictionary(l => l.Id);

        // Anchor — challenge-level start locator drives heat-start spawn when
        // a heat doesn't override. Falls back to (0,0,0) yaw 0 if the locator
        // is missing; the validator already flags this case with a Warning.
        Vector3 anchorPos = Vector3.Zero;
        float anchorYaw = 0f;
        if (input.StartLocatorId is Guid slId && locatorsById.TryGetValue(slId, out var sl))
        {
            anchorPos = sl.Position;
            anchorYaw = sl.RotationDegrees.Y;
        }

        var heatSpecs = new List<RaceHeatSpec>(input.RaceHeats.Count);
        foreach (var heat in input.RaceHeats)
        {
            Vector3? heatStartPos = null;
            float? heatStartYaw = null;
            if (heat.StartLocatorId is Guid hslId && locatorsById.TryGetValue(hslId, out var hsl))
            {
                heatStartPos = hsl.Position;
                heatStartYaw = hsl.RotationDegrees.Y;
            }

            var legSpecs = new List<RaceLegSpec>(heat.Legs.Count);
            foreach (var leg in heat.Legs)
            {
                var gateSpecs = new List<RaceGateSpec>(leg.Gates.Count);
                foreach (var gate in leg.Gates)
                {
                    gateSpecs.Add(new RaceGateSpec
                    {
                        Volume = ResolveVolume(gate.TriggerVolumeId, volumesById, challengeKey, diagnostics),
                        TimeBonusSeconds = gate.TimeBonusSeconds,
                    });
                }

                var splitTriggers = new List<TriggerVolumeRef>(leg.SplitTimeTriggerVolumeIds.Count);
                foreach (var sttId in leg.SplitTimeTriggerVolumeIds)
                    splitTriggers.Add(ResolveVolume(sttId, volumesById, challengeKey, diagnostics));

                legSpecs.Add(new RaceLegSpec
                {
                    Gates = gateSpecs,
                    SplitTimeTriggers = splitTriggers,
                });
            }

            heatSpecs.Add(new RaceHeatSpec
            {
                Legs = legSpecs,
                TimeLimitSeconds = heat.TimeLimitSeconds,
                KilledItSeconds = heat.KilledItSeconds,
                StartPosition = heatStartPos,
                StartYawDegrees = heatStartYaw,
            });
        }

        // Anchor fallback: if the challenge has no start-locator but at
        // least one gate, pin the anchor to the first gate's centre so a
        // mis-authored race still spawns near the playable area instead of
        // at world origin.
        if (anchorPos == Vector3.Zero && heatSpecs.Count > 0
            && heatSpecs[0].Legs.Count > 0 && heatSpecs[0].Legs[0].Gates.Count > 0)
        {
            anchorPos = heatSpecs[0].Legs[0].Gates[0].Volume.Center;
        }

        string displayTitle = string.IsNullOrWhiteSpace(input.Name) ? challengeKey : input.Name;

        return new RaceChallengeSpec
        {
            ChallengeKey = challengeKey,
            IsDeathRace = input.IsDeathRace,
            Map = mapSpec,
            DisplayTitle = displayTitle,
            // Per-instance Description left blank; the dlc_<key>_races family
            // row supplies ID_MISSION_DEATHRACE_CHALLENGE_DESCRIPTION via
            // inheritance.
            Description = string.Empty,
            RaceGateSkipable = input.RaceGateSkipable,
            AnchorPosition = anchorPos,
            AnchorYawDegrees = anchorYaw,
            Heats = heatSpecs,
        };
    }

    private static TriggerVolumeRef ResolveVolume(
        Guid id,
        Dictionary<Guid, TriggerVolumeInput> volumesById,
        string challengeKey,
        IList<Diagnostic> diagnostics)
    {
        if (volumesById.TryGetValue(id, out var tv))
        {
            return new TriggerVolumeRef
            {
                Name = tv.Name,
                Center = tv.Center,
                HalfExtents = tv.HalfExtents,
                YawDegrees = tv.RotationDegrees.Y,
            };
        }

        // Validator should have caught this; degrade gracefully so the
        // downstream writers see a recognisable sentinel instead of a NRE.
        diagnostics.Add(new Diagnostic(DiagnosticLevel.Error, "Race",
            $"[{challengeKey}] Gate / split-time trigger references volume {id} which doesn't exist on the map. " +
            $"Writing a zero-extent placeholder named '<missing>' — the gate will not fire at runtime."));
        return new TriggerVolumeRef
        {
            Name = "<missing>",
            Center = Vector3.Zero,
            HalfExtents = Vector3.Zero,
            YawDegrees = 0f,
        };
    }

    private static string ToSlug(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return new string(s.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
    }
}
