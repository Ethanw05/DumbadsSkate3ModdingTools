using System.Numerics;
using DlcBuilder.Builders;
using DlcBuilder.Inputs;
using DlcBuilder.Modules.DlcManifest;
using DlcBuilder.Outputs;

namespace DlcBuilder.Modules.Skate;

/// Resolves a public-API <see cref="ChallengeInput"/> (Guid handles) into a
/// fully-baked <see cref="SkateChallengeSpec"/> the Skate VLT writers can
/// consume directly. Own pipeline — does NOT call OtsPsgBuilder.
public static class SkateSpecBuilder
{
    public static SkateChallengeSpec FromChallengeInput(
        ChallengeInput input,
        MapInput map,
        DlcSpec mapSpec,
        IList<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(mapSpec);
        ArgumentNullException.ThrowIfNull(diagnostics);

        string slugged = ToSlug(input.Name);
        string challengeKey = string.IsNullOrEmpty(slugged)
            ? $"skate_{mapSpec.Slug}"
            : (slugged.StartsWith("skate_", StringComparison.Ordinal) ? slugged : "skate_" + slugged);

        var volumesById = map.TriggerVolumes.ToDictionary(v => v.Id);
        var locatorsById = map.Locators.ToDictionary(l => l.Id);

        // SpotVolumes (1 or 2). Authored order = runtime order.
        var spotVolumes = new List<SkateTriggerVolume>(input.SkateSpotVolumeIds.Count);
        for (int i = 0; i < input.SkateSpotVolumeIds.Count; i++)
        {
            string canon = $"{challengeKey}_spotvolume_{i + 1:D2}";
            spotVolumes.Add(ResolveVolume(input.SkateSpotVolumeIds[i], volumesById, canon, challengeKey, diagnostics));
        }

        // ChallengeBoundary.
        SkateTriggerVolume challengeBoundary;
        if (input.ChallengeBoundaryId is Guid cbId && volumesById.ContainsKey(cbId))
            challengeBoundary = ResolveVolume(cbId, volumesById, $"{challengeKey}_challengeboundary_01", challengeKey, diagnostics);
        else
        {
            diagnostics.Add(new Diagnostic(DiagnosticLevel.Warning, "Skate",
                $"[{challengeKey}] No ChallengeBoundaryId; falling back to first SpotVolume."));
            challengeBoundary = spotVolumes.Count > 0
                ? spotVolumes[0] with { Name = $"{challengeKey}_challengeboundary_01" }
                : MissingVolume($"{challengeKey}_challengeboundary_01", challengeKey, "ChallengeBoundary", diagnostics);
        }

        // TurnBasedStartVolume.
        SkateTriggerVolume startVolume = input.SkateTurnBasedStartVolumeId is Guid svId
            ? ResolveVolume(svId, volumesById, $"{challengeKey}_turnbasedstartvolume", challengeKey, diagnostics)
            : MissingVolume($"{challengeKey}_turnbasedstartvolume", challengeKey, "TurnBasedStartVolume", diagnostics);

        // Start locator.
        Vector3 startPos = Vector3.Zero;
        float startYaw = 0f;
        if (input.StartLocatorId is Guid slId && locatorsById.TryGetValue(slId, out var sl))
        {
            startPos = sl.Position;
            startYaw = sl.RotationDegrees.Y;
        }
        else
        {
            diagnostics.Add(new Diagnostic(DiagnosticLevel.Warning, "Skate",
                $"[{challengeKey}] No StartLocatorId; using origin."));
        }

        // Wait locator.
        Vector3 waitPos = startPos;
        float waitYaw = startYaw;
        if (input.SkateWaitLocatorId is Guid wlId && locatorsById.TryGetValue(wlId, out var wl))
        {
            waitPos = wl.Position;
            waitYaw = wl.RotationDegrees.Y;
        }

        // Visual indicators.
        var vis = new List<(Vector3, float)>(input.SkateVisualIndicatorLocatorIds.Count);
        foreach (var lid in input.SkateVisualIndicatorLocatorIds)
        {
            if (locatorsById.TryGetValue(lid, out var loc))
                vis.Add((loc.Position, loc.RotationDegrees.Y));
            else
            {
                diagnostics.Add(new Diagnostic(DiagnosticLevel.Error, "Skate",
                    $"[{challengeKey}] VisualIndicator locator {lid} not found on map."));
                vis.Add((Vector3.Zero, 0f));
            }
        }

        string displayTitle = string.IsNullOrWhiteSpace(input.Name) ? challengeKey : input.Name;

        return new SkateChallengeSpec
        {
            ChallengeKey = challengeKey,
            Map = mapSpec,
            DisplayTitle = displayTitle,
            Description = string.Empty,
            SpotVolumes = spotVolumes,
            ChallengeBoundary = challengeBoundary,
            TurnBasedStartVolume = startVolume,
            StartLocatorPosition = startPos,
            StartLocatorYawDegrees = startYaw,
            WaitLocatorPosition = waitPos,
            WaitLocatorYawDegrees = waitYaw,
            VisualIndicators = vis,
            TimeLimitSeconds = input.SkateTimeLimitSeconds,
            UseDwtn01Profile = input.SkateUseDwtn01Profile,
            OwnedItRewardCredits = input.SkateOwnedItRewardCredits,
        };
    }

    private static SkateTriggerVolume ResolveVolume(
        Guid id,
        Dictionary<Guid, TriggerVolumeInput> volumesById,
        string canonicalName,
        string challengeKey,
        IList<Diagnostic> diagnostics)
    {
        if (volumesById.TryGetValue(id, out var tv))
        {
            // GuidLocal = Lookup8 of the canonical name. The PSG-side writer
            // must register the same volume under the same canonical name so
            // tTriggerInstance.m_uiGuidLocal matches.
            ulong guid = Lookup8Hashing.Hash(canonicalName);
            return new SkateTriggerVolume
            {
                Name = canonicalName,
                Center = tv.Center,
                HalfExtents = tv.HalfExtents,
                YawDegrees = tv.RotationDegrees.Y,
                Guid = guid,
            };
        }
        diagnostics.Add(new Diagnostic(DiagnosticLevel.Error, "Skate",
            $"[{challengeKey}] Volume {id} ({canonicalName}) not found on map."));
        return MissingVolume(canonicalName, challengeKey, canonicalName, diagnostics);
    }

    private static SkateTriggerVolume MissingVolume(string canonicalName, string challengeKey, string slotName, IList<Diagnostic> diagnostics)
    {
        diagnostics.Add(new Diagnostic(DiagnosticLevel.Error, "Skate",
            $"[{challengeKey}] {slotName} missing — emitting zero-extent placeholder."));
        return new SkateTriggerVolume
        {
            Name = canonicalName,
            Center = Vector3.Zero,
            HalfExtents = Vector3.Zero,
            YawDegrees = 0f,
            Guid = Lookup8Hashing.Hash(canonicalName),
        };
    }

    private static string ToSlug(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return new string(s.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
    }
}
