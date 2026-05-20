using DlcBuilder.Outputs;

namespace DlcBuilder.Modules.PsfPacker;

/// High-level PSF packing module. Walks the per-mission folders under a
/// missions root and packs every `cSim_Global` subfolder into the matching
/// sibling `cSim_Global.psf` via Stream File Tool.exe. On success, deletes the
/// source folder so the laid-out tree mirrors the shipping retail layout
/// (only `cSim_Global.psf`, no folder).
///
/// Returns a list of diagnostics describing what got packed, what failed, and
/// any tool-stderr lines worth surfacing in the build log. Never throws for
/// content issues — caller can collect across multiple modules and decide.
public static class PsfPacker
{
    /// Pack every per-mission `cSim_Global/` folder under `missionsRoot` into
    /// `cSim_Global.psf`. Returns a tuple of (packed count, diagnostics).
    public static (int packedCount, IReadOnlyList<Diagnostic> diagnostics) PackAllOtsChallenges(
        string missionsRoot,
        StreamToolPlatform platform = StreamToolPlatform.Ps3)
    {
        var diags = new List<Diagnostic>();

        if (!Directory.Exists(missionsRoot))
        {
            diags.Add(new(DiagnosticLevel.Error, "PsfPacker", $"missions root not found: {missionsRoot}"));
            return (0, diags);
        }

        string? toolPath = StreamToolRunner.Locate();
        if (toolPath == null)
        {
            diags.Add(new(DiagnosticLevel.Error, "PsfPacker",
                $"{StreamToolRunner.ToolName} not found. Drop it next to the running .exe and rebuild."));
            return (0, diags);
        }
        diags.Add(new(DiagnosticLevel.Info, "PsfPacker",
            $"Using {Path.GetFileName(toolPath)} from {Path.GetDirectoryName(toolPath)}"));

        int packed = 0;
        foreach (string missionDir in Directory.EnumerateDirectories(missionsRoot))
        {
            string cSimDir = Path.Combine(missionDir, "cSim_Global");
            if (!Directory.Exists(cSimDir)) continue;

            string missionName = Path.GetFileName(missionDir);
            string psfPath = Path.Combine(missionDir, "cSim_Global.psf");

            // Stream File Tool can exit with a non-zero code even when the
            // pack succeeds (it's a GUI tool repurposed as CLI), so we use the
            // existence of the .psf as the authoritative success criterion.
            var result = StreamToolRunner.Pack(toolPath, cSimDir, StreamToolType.Sim, platform);
            if (!File.Exists(psfPath))
            {
                diags.Add(new(DiagnosticLevel.Error, "PsfPacker",
                    $"Failed to produce {psfPath} for '{missionName}' (engine WILL crash on load)."));
                if (!string.IsNullOrWhiteSpace(result.StdErr))
                    diags.Add(new(DiagnosticLevel.Error, "PsfPacker", $"  stderr: {result.StdErr.Trim()}"));
                if (!string.IsNullOrWhiteSpace(result.StdOut))
                    diags.Add(new(DiagnosticLevel.Info, "PsfPacker", $"  stdout: {result.StdOut.Trim()}"));
                continue;
            }

            // Source folder packed successfully — remove it to mirror retail layout.
            try { Directory.Delete(cSimDir, recursive: true); }
            catch (Exception ex)
            {
                diags.Add(new(DiagnosticLevel.Warning, "PsfPacker",
                    $"Cleanup failed for {cSimDir}: {ex.Message}"));
            }
            packed++;
        }

        diags.Add(new(DiagnosticLevel.Info, "PsfPacker",
            $"Packed {packed} OTS challenge cSim_Global folder(s)."));
        return (packed, diags);
    }
}
