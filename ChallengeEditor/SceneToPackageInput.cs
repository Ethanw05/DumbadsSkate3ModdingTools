using System.Linq;
using DlcBuilder.Inputs;

namespace ChallengeEditor;

/// One-way conversion from the editor's runtime scene (with WPF-ish mutable
/// types and Veldrid GPU resources) into the immutable plain-data PackageInput
/// that DlcBuilder.Core consumes. Keeps the editor / library boundary clean: the
/// library never references EditorScene types and vice versa.
///
/// One DIST can contribute MANY MapInputs — every locator with `Owner ==
/// Freeskate` is a separate FE menu entry ("Rails", "Downtown", "Huge Jumps"
/// in one DIST = three MapInputs sharing the same DistFolderPath but each with
/// its own Name / Category / spawn). Slug uniqueness is enforced by deriving
/// a SlugSuffix from each locator's name; collisions get `_2`, `_3`, …
/// appended just like MinimalDlcBuilder did.
public static class SceneToPackageInput
{
    public static PackageInput Convert(EditorScene scene, string? packageNameOverride = null)
    {
        var maps = new List<MapInput>();

        foreach (var d in scene.Dists)
        {
            // Skip DISTs that have no folder on disk — those are authoring-only and
            // there's nothing for the builder to layer authored content onto.
            if (string.IsNullOrWhiteSpace(d.FolderPath)) continue;

            // Materialize the per-DIST authored content once. Every Location
            // (= Freeskate locator) emitted from this DIST shares the same
            // trigger volumes / locators / challenges — they're authored
            // against the DIST's geometry, not duplicated per location.
            var convertedChallenges = d.Challenges.Select(c =>
            {
                ChallengeKind kind = c.Type switch
                {
                    ChallengeType.Otl => ChallengeKind.Otl,
                    ChallengeType.Photo => ChallengeKind.Photo,
                    ChallengeType.Film => ChallengeKind.Film,
                    ChallengeType.Race => ChallengeKind.Race,
                    ChallengeType.Skate => ChallengeKind.Skate,
                    _ => ChallengeKind.Ots,
                };

                // Race authoring uses a single-heat / single-leg / N-gate
                // shape — flatten the editor's gate-volume list into the
                // builder's nested heat/leg/gate input. Empty list → empty
                // RaceHeats → validator errors with an actionable message
                // (no silent gate fabrication).
                IReadOnlyList<RaceHeatInput> raceHeats = Array.Empty<RaceHeatInput>();
                if (kind == ChallengeKind.Race && c.RaceGateVolumeIds.Count > 0)
                {
                    var gates = c.RaceGateVolumeIds
                        .Select(volId => new RaceGateInput { TriggerVolumeId = volId })
                        .ToList();
                    raceHeats = new[]
                    {
                        new RaceHeatInput
                        {
                            TimeLimitSeconds = c.RaceTimeLimitSeconds,
                            KilledItSeconds = c.RaceKilledItSeconds,
                            Legs = new[]
                            {
                                new RaceLegInput { Gates = gates },
                            },
                        },
                    };
                }

                return new ChallengeInput
                {
                    Id = c.Id,
                    Name = c.Name,
                    Kind = kind,
                    StartLocatorId = c.StartLocatorId,
                    ScoringVolumeId = c.ScoringVolumeId,
                    DiscoveryBoundaryId = c.DiscoveryBoundaryId,
                    ChallengeBoundaryId = c.ChallengeBoundaryId,
                    VisualSignupLocatorId = c.VisualSignupLocatorId,
                    InChallengeRibbonArrowLocatorIds = c.InChallengeRibbonArrowLocatorIds.ToList(),
                    ChevronLocatorIds = c.ChevronLocatorIds.ToList(),
                    OwnedPoints = c.OwnedPoints,
                    KilledItPoints = c.KilledItPoints,
                    OnlineBonusXp = c.OnlineBonusXp,
                    RaceHeats = raceHeats,
                    RaceGateSkipable = c.RaceGateSkipable,
                    IsDeathRace = c.IsDeathRace,
                    SkateSpotVolumeIds = c.SkateSpotVolumeIds.ToList(),
                    SkateTurnBasedStartVolumeId = c.SkateTurnBasedStartVolumeId,
                    SkateWaitLocatorId = c.SkateWaitLocatorId,
                    SkateVisualIndicatorLocatorIds = c.SkateVisualIndicatorLocatorIds.ToList(),
                    SkateTimeLimitSeconds = c.SkateTimeLimitSeconds,
                    SkateUseDwtn01Profile = c.SkateUseDwtn01Profile,
                    SkateOwnedItRewardCredits = c.SkateOwnedItRewardCredits,
                };
            }).ToList();

            var convertedTriggers = d.TriggerVolumes.Select(v => new TriggerVolumeInput
            {
                Id = v.Id,
                Name = v.Name,
                Center = v.Center,
                HalfExtents = v.HalfExtents,
                RotationDegrees = v.RotationDegrees,
            }).ToList();

            var convertedLocators = d.Locators.Select(l => new LocatorInput
            {
                Id = l.Id,
                Name = l.Name,
                Position = l.Position,
                RotationDegrees = l.RotationDegrees,
                Kind = l.Kind switch
                {
                    ChallengeEditor.LocatorKind.ChallengeStart  => DlcBuilder.Inputs.LocatorKind.ChallengeStart,
                    ChallengeEditor.LocatorKind.FreeskateAnchor => DlcBuilder.Inputs.LocatorKind.FreeskateAnchor,
                    ChallengeEditor.LocatorKind.Sub             => DlcBuilder.Inputs.LocatorKind.Sub,
                    _                                           => DlcBuilder.Inputs.LocatorKind.Spawn,
                },
            }).ToList();

            // Each Freeskate locator within this DIST becomes its own MapInput.
            // Slug uniqueness: derive from the locator's name, append `_2/_3/…`
            // on collision (matches MinimalDlcBuilder's UiLocation flow).
            var freeskateLocators = d.Locators.Where(l => l.Owner == OwnerKind.Freeskate).ToList();
            if (freeskateLocators.Count == 0)
            {
                // Fallback: a DIST with no Freeskate-flagged locators still
                // becomes a single MapInput so the build doesn't drop the
                // DIST silently. The validator will warn separately.
                maps.Add(new MapInput
                {
                    DistFolderPath = d.FolderPath!,
                    DisplayName = d.Name,
                    Challenges = convertedChallenges,
                    TriggerVolumes = convertedTriggers,
                    Locators = convertedLocators,
                });
                continue;
            }

            var usedSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < freeskateLocators.Count; i++)
            {
                Locator loc = freeskateLocators[i];
                string baseSuffix = string.IsNullOrWhiteSpace(loc.Name) ? $"loc_{i + 1}" : loc.Name.Trim();
                string candidate = ToSlug(baseSuffix);
                if (string.IsNullOrEmpty(candidate)) candidate = $"loc_{i + 1}";

                string unique = candidate;
                int n = 2;
                while (!usedSuffixes.Add(unique))
                {
                    unique = $"{candidate}_{n}";
                    n++;
                }

                // Challenges attach to exactly ONE freeskate location, not
                // every one. MinimalDlcBuilder/ModernMainForm.cs:582-608 has
                // a per-UiLocation `IncludeOts` checkbox so each location
                // independently decides whether to host an OTS — challenges
                // are intrinsic to a single location, never duplicated.
                // Earlier shape copied `convertedChallenges` to every
                // MapInput; with two freeskate locations + one authored
                // challenge that produced two MapInputs each carrying the
                // same challenge → duplicate row keys → validator hard fail.
                // Until the editor exposes a per-locator OTS toggle (or an
                // explicit owner-locator field on Challenge), park all
                // authored challenges on the FIRST freeskate location only.
                // Subsequent locations are pure freeskate menu entries.
                var challengesForThisMap = i == 0
                    ? convertedChallenges
                    : new List<ChallengeInput>();

                maps.Add(new MapInput
                {
                    DistFolderPath = d.FolderPath!,
                    DisplayName = string.IsNullOrWhiteSpace(loc.Name) ? d.Name : loc.Name,
                    Section = string.IsNullOrWhiteSpace(loc.Category) ? null : loc.Category.Trim(),
                    SlugSuffix = unique,
                    SpawnX = loc.Position.X,
                    SpawnY = loc.Position.Y,
                    SpawnZ = loc.Position.Z,
                    SpawnYaw = loc.RotationDegrees.Y,
                    Challenges = challengesForThisMap,
                    TriggerVolumes = convertedTriggers,
                    Locators = convertedLocators,
                });
            }
        }

        // Caller-supplied override (Export DLC prompt) wins; otherwise use the
        // scene's last-remembered name; otherwise fall back to "MyDLC".
        string name = !string.IsNullOrWhiteSpace(packageNameOverride)
            ? packageNameOverride!
            : (string.IsNullOrWhiteSpace(scene.PackageName) ? "MyDLC" : scene.PackageName);

        return new PackageInput
        {
            PackageName = name,
            Maps = maps,
        };
    }

    private static string ToSlug(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return new string(s.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
    }
}
