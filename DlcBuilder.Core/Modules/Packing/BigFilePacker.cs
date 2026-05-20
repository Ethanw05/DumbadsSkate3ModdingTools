using System.Diagnostics;
using System.Text;

namespace DlcBuilder.Modules.Packing;

/// Wraps `bigfile.exe` to pack a `data/` staging tree into a single
/// `&lt;dlcFolder&gt;/custom_&lt;slug&gt;.big.edat` file ready to drop into an RPCS3
/// USRDIR (or copy onto a real PS3 via FTP).
///
/// Pipeline (matches MinimalDlcBuilder's `ModernMainForm` Step 3 + 4):
///   1. Run `bigfile.exe &lt;bigName&gt; data/* -r -fat` from the staging root.
///      The tool walks `data/`, hashes every path, and writes one BIG with
///      a leading FAT chunk. Verbose `Added file ...` and
///      `bigfile: failed to open ...` lines are filtered out.
///   2. Move the output `&lt;bigName&gt;` to `&lt;dlcFolder&gt;/custom_&lt;slug&gt;.big.edat`
///      where `dlcFolder` is a 16-char USRDIR-style folder name derived from
///      the package slug (`PackageNameToDlcFolderName`).
///   3. Delete the temporary `&lt;bigName&gt;` produced in step 1.
///
/// Note: the `.edat` extension is just a rename — no actual EDAT encryption.
/// RPCS3 (and the engine via the BLUS30464 path) accepts the unencrypted BIG
/// as long as the extension matches.
public static class BigFilePacker
{
    public const string BigFileExeName = "bigfile.exe";

    public sealed record PackResult(
        bool Success,
        string? OutputBigEdatPath,
        IReadOnlyList<string> Diagnostics);

    /// Packs `&lt;stagingRoot&gt;/data/*` into a `.big.edat` under
    /// `&lt;outputRoot&gt;/&lt;dlcFolder&gt;/`. `stagingRoot` is the directory CONTAINING
    /// the `data/` subfolder (NOT the data folder itself).
    public static PackResult Pack(string stagingRoot, string outputRoot, string packageSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageSlug);

        var diags = new List<string>();

        string dataDir = Path.Combine(stagingRoot, "data");
        if (!Directory.Exists(dataDir))
        {
            diags.Add($"staging 'data/' subfolder not found at {dataDir}.");
            return new PackResult(false, null, diags);
        }

        string? bigExe = FindBigFileExe();
        if (bigExe is null)
        {
            diags.Add($"{BigFileExeName} not found. Drop it next to the host .exe and rebuild.");
            return new PackResult(false, null, diags);
        }
        diags.Add($"using {Path.GetFileName(bigExe)} from {Path.GetDirectoryName(bigExe)}");

        string slug = packageSlug.ToLowerInvariant();
        string bigName = $"dlc_{slug}_minimal.big";
        string bigPath = Path.Combine(stagingRoot, bigName);
        string args = $"\"{bigName}\" data/* -r -fat";

        diags.Add($"cwd:     {stagingRoot}");
        diags.Add($"command: {Path.GetFileName(bigExe)} {args}");

        var output = new List<string>();
        bool ok = RunProcess(bigExe, args, stagingRoot, output);
        int suppressed = 0;
        foreach (string line in output)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (IsNoisyBigfileLine(line)) { suppressed++; continue; }
            diags.Add(line);
        }
        if (suppressed > 0) diags.Add($"bigfile: suppressed {suppressed} verbose line(s).");

        if (!ok)
        {
            diags.Add("bigfile.exe failed.");
            return new PackResult(false, null, diags);
        }
        if (!File.Exists(bigPath))
        {
            diags.Add($"packed BIG file missing at expected path: {bigPath}");
            return new PackResult(false, null, diags);
        }

        // Move + rename to .big.edat under the USRDIR-style folder name.
        string dlcFolderName = PackageSlugToDlcFolderName(slug);
        string finalFolder = Path.Combine(outputRoot, dlcFolderName);
        Directory.CreateDirectory(finalFolder);
        string finalPath = Path.Combine(finalFolder, PackageSlugToFinalBigEdatFileName(slug));
        File.Copy(bigPath, finalPath, overwrite: true);
        try { File.Delete(bigPath); }
        catch (Exception ex) { diags.Add($"warning: temp BIG cleanup failed: {ex.Message}"); }

        diags.Add($"wrote {finalPath} ({new FileInfo(finalPath).Length:N0} B)");
        return new PackResult(true, finalPath, diags);
    }

    /// Matches MinimalDlcBuilder's convention: 7-char alphanumeric slug
    /// uppercased + 9 zeros = 16 chars total. Engine cross-references this
    /// folder name against `dlc_mapping.product_id` at boot, so it MUST equal
    /// the product_id baked into the manifest VLT.
    public static string PackageSlugToDlcFolderName(string slug) =>
        new string(slug.ToUpperInvariant().Where(char.IsLetterOrDigit).Take(7).ToArray())
            .PadRight(7, '0') + "000000000";

    /// Final BIG filename under the DLC folder. Uses the same slug as
    /// <see cref="DlcBuilder.Modules.DlcManifest.DlcSpec.ToSlug"/> / manifest
    /// <c>PackageSlug</c> (lower-case letters, digits, underscores).
    public static string PackageSlugToFinalBigEdatFileName(string packageSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageSlug);
        string core = packageSlug.ToLowerInvariant();
        const int maxCore = 200;
        if (core.Length > maxCore)
            core = core[..maxCore].TrimEnd('_');
        return $"custom_{core}.big.edat";
    }

    private static string? FindBigFileExe()
    {
        return EmbeddedToolExtractor.LocateOrExtract(BigFileExeName);
    }

    private static bool RunProcess(string exe, string args, string cwd, List<string> output)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process p = new() { StartInfo = psi };
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        foreach (string line in sb.ToString().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            output.Add(line);
        return p.ExitCode == 0;
    }

    /// bigfile.exe spams `Added file ...` per file and `bigfile: failed to open ...`
    /// for any path it can't read (frequently spurious). Filter both so the build
    /// log stays readable.
    private static bool IsNoisyBigfileLine(string line)
    {
        string t = line.TrimStart();
        return t.StartsWith("Added file ", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("bigfile: failed to open ", StringComparison.OrdinalIgnoreCase);
    }
}
