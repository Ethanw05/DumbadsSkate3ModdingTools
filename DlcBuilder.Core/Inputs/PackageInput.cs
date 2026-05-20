namespace DlcBuilder.Inputs;

/// Top-level DLC package the builder consumes. One PackageInput → one DLC bundle
/// on disk. Front-ends (editor, CLI, batch tools) build this from their own
/// scene representation; the builder treats it as the only input contract so
/// neither side needs to know the other's internal shape.
public sealed record PackageInput
{
    /// User-visible package name. Drives the Slug, category keys, helper HALs
    /// and listing keys downstream. Required.
    public required string PackageName { get; init; }

    /// Optional override for the long-form category description shown in the
    /// in-game DLC listing. If null, a sensible default is derived from the
    /// package name.
    public string? CategoryDescription { get; init; }

    /// Each map input is one DIST that becomes a freeskate location. Order is
    /// preserved; first map's metadata is used as the package default when
    /// PackageName is left blank.
    public required IReadOnlyList<MapInput> Maps { get; init; } = Array.Empty<MapInput>();

    /// Optional global file-name prefix for output artifacts (default "dlc"
    /// matches MinimalDlcBuilder's existing behavior).
    public string Prefix { get; init; } = "dlc";
}
