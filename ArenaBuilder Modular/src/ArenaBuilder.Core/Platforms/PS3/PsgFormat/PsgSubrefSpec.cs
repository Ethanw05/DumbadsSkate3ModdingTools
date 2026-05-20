namespace ArenaBuilder.Core.Psg;

/// <summary>
/// Spec for the Arena Subreferences section.
/// Null when the PSG has no subrefs (e.g. texture).
/// </summary>
public sealed record PsgSubrefSpec(IReadOnlyList<PsgSubrefRecord> Records);
