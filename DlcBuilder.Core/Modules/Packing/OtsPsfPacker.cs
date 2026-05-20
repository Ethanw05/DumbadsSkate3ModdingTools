using System.Diagnostics;

namespace DlcBuilder.Modules.Packing;

/// Wraps EA's `Stream File Tool.exe` to pack each per-OTS-challenge
/// `cSim_Global` folder into `cSim_Global.psf` next to the manifest stubs.
///
/// The engine streams `cSim_Global.psf` (NOT the unpacked folder) when the
/// challenge's mission folder loads. Without packing the asset stream system
/// gets a NULL pointer for the trigger volume PSG and crashes inside the
/// arena fixup chain.
///
/// CLI: `Stream File Tool.exe pack --folder=&lt;missionFolder&gt; --type=sim --platform=p`
/// where `&lt;missionFolder&gt;` is the parent containing the cSim_Global subfolder.
///
/// The tool can exit non-zero even on success (it's a GUI app repurposed as a
/// CLI), so we use the existence of `cSim_Global.psf` as the success signal
/// rather than the exit code.
public static class OtsPsfPacker
{
    public const string StreamToolExeName = "Stream File Tool.exe";

    public sealed record PackResult(int Packed, int Failed, IReadOnlyList<string> Diagnostics);

    /// For every mission folder under `missionsRoot` that contains a
    /// `cSim_Global` subfolder, run Stream File Tool to pack the folder into
    /// `cSim_Global.psf`. On success the source folder is removed (matches
    /// shipping retail layout: only the .psf, no subfolder).
    public static PackResult PackAll(string missionsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(missionsRoot);
        var diags = new List<string>();
        if (!Directory.Exists(missionsRoot))
        {
            diags.Add($"missions root not found: {missionsRoot}");
            return new PackResult(0, 0, diags);
        }

        string? toolPath = FindStreamTool();
        if (toolPath is null)
        {
            diags.Add($"{StreamToolExeName} not found. Drop it next to the host .exe and rebuild.");
            return new PackResult(0, 0, diags);
        }
        diags.Add($"using {Path.GetFileName(toolPath)} from {Path.GetDirectoryName(toolPath)}");

        int packed = 0, failed = 0;
        foreach (string missionDir in Directory.EnumerateDirectories(missionsRoot))
        {
            string cSimDir = Path.Combine(missionDir, "cSim_Global");
            if (!Directory.Exists(cSimDir)) continue;

            string missionName = Path.GetFileName(missionDir);
            string psfPath = Path.Combine(missionDir, "cSim_Global.psf");

            RunPack(toolPath, missionDir, diags);

            if (!File.Exists(psfPath))
            {
                diags.Add($"FAILED to produce {psfPath} for '{missionName}'. Engine WILL crash on load.");
                failed++;
                continue;
            }

            // Source folder contributed its content; remove to match retail layout.
            try { Directory.Delete(cSimDir, recursive: true); }
            catch (Exception ex) { diags.Add($"warning: cleanup failed for {cSimDir}: {ex.Message}"); }

            packed++;
        }

        return new PackResult(packed, failed, diags);
    }

    private static string? FindStreamTool()
    {
        return DlcBuilder.Modules.PsfPacker.StreamToolRunner.Locate();
    }

    private static bool RunPack(string exePath, string missionFolder, List<string> diags)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? missionFolder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("pack");
        psi.ArgumentList.Add($"--folder={missionFolder}");
        psi.ArgumentList.Add("--type=sim");
        psi.ArgumentList.Add("--platform=p");

        try
        {
            using Process? p = Process.Start(psi);
            if (p is null) { diags.Add($"failed to start {Path.GetFileName(exePath)}."); return false; }

            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            const int timeoutMs = 120_000;
            if (!p.WaitForExit(timeoutMs))
            {
                p.Kill(entireProcessTree: false);
                diags.Add($"{Path.GetFileName(exePath)} timed out (>{timeoutMs / 1000}s) and was killed.");
                return false;
            }

            if (p.ExitCode != 0)
            {
                diags.Add($"tool exited code {p.ExitCode} (0x{(uint)p.ExitCode:X8}).");
                if (!string.IsNullOrWhiteSpace(stderr)) diags.Add($"stderr: {stderr.Trim()}");
                if (!string.IsNullOrWhiteSpace(stdout)) diags.Add($"stdout: {stdout.Trim()}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            diags.Add($"{Path.GetFileName(exePath)} error: {ex.Message}");
            return false;
        }
    }
}
