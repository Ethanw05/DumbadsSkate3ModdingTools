using DlcBuilder.Builders;
using DlcBuilder.Modules.Packing;
using DlcBuilder.Outputs;

namespace DlcBuilder.Modules.BigPackager;

public sealed record BigPackResult
{
    public required bool Succeeded { get; init; }
    /// Path to the produced `custom_&lt;slug&gt;.big.edat` (or null on failure).
    public string? FinalEdatPath { get; init; }
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
    public required ToolResult? ToolResult { get; init; }
}

/// Final step of a DLC build: invoke EA's `bigfile.exe` to pack the staged
/// `data/` tree into a single `.big`, then move/rename it to the expected
/// shipping path:
///
///   &lt;outputDir&gt;/&lt;dlcFolder&gt;/custom_&lt;slug&gt;.big.edat
///
/// where `dlcFolder` is the standard `IP9001-NPUB30664_00-DLC&lt;SLUG&gt;0000000` form
/// the engine expects. We don't construct the folder name here — caller passes
/// it (DlcSpec/PackageInput knows the slug + naming convention).
///
/// Tool CLI (matches MinimalDlcBuilder.ModernMainForm.cs):
///   bigfile.exe &lt;output.big&gt; data/* -r -fat
/// CWD must be the staging directory containing `data/` so the relative glob
/// resolves correctly. Output `.big` lands in the staging dir; we move it to
/// final after success.
public static class BigPackager
{
    public const string ToolName = "bigfile.exe";

    /// Locate `bigfile.exe`. Search:
    ///   1. Same directory as the running .exe.
    ///   2. &lt;baseDir&gt;/DONOTREMOVE/.
    public static string? Locate()
    {
        return EmbeddedToolExtractor.LocateOrExtract(ToolName);
    }

    /// Pack `&lt;stagingDir&gt;/data/*` into a `.big`, then move to
    /// `&lt;outputDir&gt;/&lt;dlcFolderName&gt;/custom_&lt;slug&gt;.big.edat`.
    /// `stagingDir` must contain a `data/` subdir already laid out by previous
    /// build steps.
    public static BigPackResult Pack(
        string stagingDir,
        string outputDir,
        string dlcFolderName,
        string packageSlug,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(dlcFolderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageSlug);

        var diags = new List<Diagnostic>();

        if (!Directory.Exists(stagingDir))
        {
            diags.Add(new(DiagnosticLevel.Error, "BigPackager", $"staging dir not found: {stagingDir}"));
            return new BigPackResult { Succeeded = false, Diagnostics = diags, ToolResult = null };
        }
        if (!Directory.Exists(Path.Combine(stagingDir, "data")))
        {
            diags.Add(new(DiagnosticLevel.Error, "BigPackager",
                $"staging dir has no `data/` subdirectory: {stagingDir}"));
            return new BigPackResult { Succeeded = false, Diagnostics = diags, ToolResult = null };
        }

        string? toolPath = Locate();
        if (toolPath == null)
        {
            diags.Add(new(DiagnosticLevel.Error, "BigPackager",
                $"{ToolName} not found. Drop it next to the running .exe and rebuild."));
            return new BigPackResult { Succeeded = false, Diagnostics = diags, ToolResult = null };
        }
        diags.Add(new(DiagnosticLevel.Info, "BigPackager",
            $"Using {Path.GetFileName(toolPath)} from {Path.GetDirectoryName(toolPath)}"));

        // bigfile.exe requires its output path to be relative to CWD; we stage
        // the .big in stagingDir and move it after.
        string bigName = $"dlc_{packageSlug}_minimal.big";
        string bigPath = Path.Combine(stagingDir, bigName);

        var result = ToolRunner.Run(
            toolPath,
            new[] { bigName, "data/*", "-r", "-fat" },
            workingDirectory: stagingDir,
            timeout: timeout);

        diags.Add(new(DiagnosticLevel.Info, "BigPackager",
            $"Command: {ToolName} {bigName} data/* -r -fat  (CWD={stagingDir})"));

        // bigfile.exe is generally well-behaved on exit codes (unlike Stream
        // File Tool), so we trust ExitCode==0 here. But still verify the file
        // exists as a safety net.
        if (!result.Succeeded)
        {
            diags.Add(new(DiagnosticLevel.Error, "BigPackager",
                $"bigfile.exe failed (exit {result.ExitCode}{(result.TimedOut ? ", timed out" : "")})."));
            if (!string.IsNullOrWhiteSpace(result.StdErr))
                diags.Add(new(DiagnosticLevel.Error, "BigPackager", $"  stderr: {result.StdErr.Trim()}"));
            return new BigPackResult { Succeeded = false, Diagnostics = diags, ToolResult = result };
        }
        if (!File.Exists(bigPath))
        {
            diags.Add(new(DiagnosticLevel.Error, "BigPackager", $"Packed BIG file missing after pack: {bigPath}"));
            return new BigPackResult { Succeeded = false, Diagnostics = diags, ToolResult = result };
        }

        // Stage 2: move/rename to the shipping location.
        string finalFolder = Path.Combine(outputDir, dlcFolderName);
        Directory.CreateDirectory(finalFolder);
        string finalName = BigFilePacker.PackageSlugToFinalBigEdatFileName(packageSlug.ToLowerInvariant());
        string finalPath = Path.Combine(finalFolder, finalName);
        try
        {
            File.Copy(bigPath, finalPath, overwrite: true);
            try { File.Delete(bigPath); } catch { /* best effort */ }
        }
        catch (Exception ex)
        {
            diags.Add(new(DiagnosticLevel.Error, "BigPackager",
                $"Failed to copy {bigPath} → {finalPath}: {ex.Message}"));
            return new BigPackResult { Succeeded = false, Diagnostics = diags, ToolResult = result };
        }

        diags.Add(new(DiagnosticLevel.Info, "BigPackager", $"Final: {finalPath}"));
        return new BigPackResult
        {
            Succeeded = true,
            FinalEdatPath = finalPath,
            Diagnostics = diags,
            ToolResult = result,
        };
    }
}
