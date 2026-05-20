namespace DlcBuilder.Inputs;

/// One map (= one DIST folder) inside a DLC package. Source PSGs, materials,
/// collisions, etc. all live under DistFolderPath; the authored content
/// (challenges/triggers/locators/spawns) is layered on top from the editor's
/// scene state.
public sealed record MapInput
{
    /// Absolute path to the DIST folder on disk. Must contain the .psf set the
    /// builder reads (cPres_*, cSim_*, etc.). Required.
    public required string DistFolderPath { get; init; }

    /// User-visible name. Defaults to the DIST folder's leaf name when omitted
    /// by the front-end.
    public required string DisplayName { get; init; }

    /// Optional per-map description override; falls back to a default.
    public string? DescriptionOverride { get; init; }

    /// Optional in-game section/category override (e.g. "Park", "Street"). Null
    /// = use the package-level default.
    public string? Section { get; init; }

    /// Optional slug suffix appended to the DIST-derived slug. Required to
    /// disambiguate when MULTIPLE map inputs share the same `DistFolderPath` —
    /// e.g. a single DIST hosting several FE menu entries ("Rails",
    /// "Downtown", "Huge Jumps"). Null/empty = no suffix; only safe when this
    /// DIST appears exactly once in the package.
    public string? SlugSuffix { get; init; }

    /// Optional spawn override for this map's freeskate location. Null = use
    /// (0,0,0). Per-location authoring (multiple locations within one DIST)
    /// expects each entry to set its own spawn so the player drops at the
    /// authored spot.
    public float? SpawnX { get; init; }
    public float? SpawnY { get; init; }
    public float? SpawnZ { get; init; }
    public float? SpawnYaw { get; init; }

    /// Optional FE menu thumbnail source (PNG/JPG/DDS). Null = no per-map
    /// thumbnail (engine falls back to its default placeholder).
    public string? FeImageSourcePath { get; init; }

    /// OTS challenges authored against this map.
    public IReadOnlyList<ChallengeInput> Challenges { get; init; } = Array.Empty<ChallengeInput>();

    /// Trigger volumes authored against this map. Used by challenges (scoring
    /// volumes / boundaries) and by general gameplay triggers.
    public IReadOnlyList<TriggerVolumeInput> TriggerVolumes { get; init; } = Array.Empty<TriggerVolumeInput>();

    /// Locators authored against this map (spawns, challenge starts, freeskate
    /// anchors, sub-locators).
    public IReadOnlyList<LocatorInput> Locators { get; init; } = Array.Empty<LocatorInput>();
}
