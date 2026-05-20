namespace DlcBuilder.Outputs;

public enum BuildStatus
{
    Succeeded,
    /// Build ran to completion but with non-fatal warnings (see Diagnostics).
    SucceededWithWarnings,
    /// At least one fatal error stopped the build before all artifacts were written.
    Failed,
}

public enum DiagnosticLevel { Info, Warning, Error }

/// One piece of feedback from the builder. Modules append these as they run; the
/// final Build() result aggregates them. Sources are arbitrary tags (e.g.
/// "LocXml", "PsfPacker") that identify which module produced the message.
public sealed record Diagnostic(DiagnosticLevel Level, string Source, string Message);

/// Result of a single Build() call. Always returned (never null) so callers can
/// surface the diagnostic list even on failure.
public sealed record BuildResult
{
    public required BuildStatus Status { get; init; }
    public required string OutputDirectory { get; init; }
    public required IReadOnlyList<string> WrittenFiles { get; init; }
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
    public TimeSpan Elapsed { get; init; }

    public bool HasErrors => Diagnostics.Any(d => d.Level == DiagnosticLevel.Error);
    public bool HasWarnings => Diagnostics.Any(d => d.Level == DiagnosticLevel.Warning);
}
