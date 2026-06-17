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

    // skate3recomp Content-tree constants (see reference_x360_dlc_structure_for_builder).
    private const string Sk3TitleId = "454108E6";
    private const string MarketplaceContentType = "00000002";
    private const string SharedXuid = "0000000000000000";

    /// Packs `&lt;stagingRoot&gt;/data/*` into a package. PS3 (default) → a renamed
    /// `custom_&lt;slug&gt;.big.edat` under `&lt;outputRoot&gt;/&lt;dlcFolder&gt;/`. Xbox 360 →
    /// a RAW unencrypted `&lt;slug&gt;_00000000.big` placed in the recomp Content tree
    /// `&lt;outputRoot&gt;/Content/0000000000000000/454108E6/00000002/&lt;ContentID&gt;/`.
    /// `stagingRoot` is the directory CONTAINING the `data/` subfolder.
    public static PackResult Pack(string stagingRoot, string outputRoot, string packageSlug,
        PlatformProfile? profile = null, string? displayName = null)
    {
        profile ??= PlatformProfile.Ps3;
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

        // Move/rename to the final shipping location. PS3: `.big.edat` under a
        // USRDIR-style folder. Xbox 360: a RAW `.big` (no edat) inside the
        // recomp Content tree, where the enumerator lists the folder and the
        // game's recipe/bigfile loader reads the unencrypted archive directly.
        string finalFolder;
        string finalName;
        string? xboxContentId = null;
        if (profile.PackEdatSuffix)
        {
            finalFolder = Path.Combine(outputRoot, PackageSlugToDlcFolderName(slug));
            finalName = PackageSlugToFinalBigEdatFileName(slug);
        }
        else
        {
            // ContentID folder = uppercased slug (the recomp's enumerator falls
            // back to the folder name when no .header is present). Layout is
            // `<root>/0000000000000000/454108E6/00000002/<ContentID>/` — the
            // portable content root is the output root itself (NO `Content/`
            // segment; verified against the recomp's ResolvePackageRoot +
            // the on-disk Portable tree).
            string contentId = new string(slug.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
            if (contentId.Length == 0) contentId = "CUSTOMDLC";
            xboxContentId = contentId;
            string titleRoot = ResolveXboxTitleRoot(outputRoot);
            finalFolder = Path.Combine(titleRoot, MarketplaceContentType, contentId);
            finalName = $"{slug}_00000000.big";
        }
        Directory.CreateDirectory(finalFolder);
        string finalPath = Path.Combine(finalFolder, finalName);
        File.Copy(bigPath, finalPath, overwrite: true);

        // Xbox 360: also write the content `.header` (a raw XCONTENT_AGGREGATE_DATA
        // blob) the recomp's content enumerator needs to register + mount the
        // folder. Without it the folder isn't enumerated like the stock DLCs, so
        // its `.big` never gets globbed/loaded. See content_manager.h.
        if (xboxContentId is not null)
        {
            try
            {
                string headerPath = WriteXboxContentHeader(ResolveXboxTitleRoot(outputRoot), xboxContentId,
                    string.IsNullOrWhiteSpace(displayName) ? xboxContentId : displayName!);
                diags.Add($"wrote content header {headerPath}");
            }
            catch (Exception ex)
            {
                diags.Add($"warning: failed to write content .header: {ex.Message}");
            }
        }
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

    /// Writes the recomp content `.header` for an Xbox DLC: a raw big-endian
    /// `XCONTENT_AGGREGATE_DATA` blob (0x148 bytes) the content enumerator reads
    /// (memcpy) to register the folder. Layout (rexglue content_manager.h):
    ///   0x00 be u32 device_id (1 = HDD)        0x04 be u32 content_type (2 = marketplace)
    ///   0x08 char16[128] display_name (BE)     0x108 char[42] file_name (= ContentID/folder)
    ///   0x132 u8[2] pad                         0x134 be u64 xuid (0 = shared)
    ///   0x13C be u32 title_id (454108E6)        → padded to 0x148.
    /// Path: &lt;titleRoot&gt;/Headers/00000002/&lt;ContentID&gt;.header, where titleRoot ends in `454108E6`.
    private static string WriteXboxContentHeader(string titleRoot, string contentId, string displayName)
    {
        const int Size = 0x148;
        var buf = new byte[Size];

        void Be32(int off, uint v) { buf[off] = (byte)(v >> 24); buf[off + 1] = (byte)(v >> 16); buf[off + 2] = (byte)(v >> 8); buf[off + 3] = (byte)v; }

        Be32(0x00, 1u);                          // device_id = HDD
        Be32(0x04, 2u);                          // content_type = marketplace
        // display_name: UTF-16 big-endian, max 127 chars, null-terminated.
        string disp = displayName.Length > 127 ? displayName[..127] : displayName;
        for (int i = 0; i < disp.Length; i++)
        {
            char c = disp[i];
            buf[0x08 + i * 2] = (byte)(c >> 8);
            buf[0x08 + i * 2 + 1] = (byte)c;
        }
        // file_name (ASCII, max 41 chars, 42-byte field) — MUST equal the folder
        // name so the enumerator/loader resolves the content path.
        string fn = contentId.Length > 41 ? contentId[..41] : contentId;
        for (int i = 0; i < fn.Length; i++) buf[0x108 + i] = (byte)fn[i];
        // xuid (0x134) = 0 shared; title_id (0x13C) = Skate 3.
        Be32(0x13C, 0x454108E6u);

        string headerDir = Path.Combine(titleRoot, "Headers", MarketplaceContentType);
        Directory.CreateDirectory(headerDir);
        string headerPath = Path.Combine(headerDir, contentId + ".header");
        File.WriteAllBytes(headerPath, buf);
        return headerPath;
    }

    /// Resolves the `454108E6` title folder from whatever the user picked, so both the
    /// content `.big` and the `.header` anchor to the same place. Accepts:
    ///   • the title folder itself     (…\0000000000000000\454108E6)        → used as-is
    ///   • the shared-XUID folder      (…\0000000000000000)                  → +454108E6
    ///   • the Portable content root   (…\Portable, contains 0000000000000000) → +0000000000000000\454108E6
    /// Final layout is always &lt;titleRoot&gt;\00000002\&lt;ContentID&gt;\ and &lt;titleRoot&gt;\Headers\00000002\&lt;ContentID&gt;.header.
    private static string ResolveXboxTitleRoot(string outputRoot)
    {
        string trimmed = outputRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string leaf = Path.GetFileName(trimmed);
        if (string.Equals(leaf, Sk3TitleId, StringComparison.OrdinalIgnoreCase))
            return trimmed;                                       // …\454108E6
        if (string.Equals(leaf, SharedXuid, StringComparison.OrdinalIgnoreCase))
            return Path.Combine(trimmed, Sk3TitleId);             // …\0000000000000000
        return Path.Combine(trimmed, SharedXuid, Sk3TitleId);     // Portable root
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
