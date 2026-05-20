using DlcBuilder.Builders;
using DlcBuilder.Modules.Packing;

namespace DlcBuilder.Modules.PsfPacker;

public enum StreamToolType
{
    Sim,    // --type=sim   (cSim_Global → cSim_Global.psf)
    Pres,   // --type=pres  (cPres_Global / cPres_*_*_high → matching .psf)
    Tex,    // --type=tex   (cTex_*_*_high → matching .psf)
}

public enum StreamToolPlatform
{
    Ps3,    // --platform=p
    Xbox,   // --platform=x
    Wii,    // --platform=w
}

/// Locate and invoke EA's "Stream File Tool.exe" to pack one stream folder
/// into the matching `.psf`. Stateless — caller supplies which folder, what
/// type/platform, and where the tool lives.
///
/// CLI mirrors the ArenaBuilder modular invocation:
///   Stream File Tool.exe pack --folder=&lt;folder&gt; --type=&lt;t&gt; --platform=&lt;p&gt;
///
/// The tool is GUI-oriented and can exit with a non-zero code even when the
/// pack succeeded, so the caller should treat `psfFileExists-on-disk` as the
/// authoritative success criterion rather than just `result.Succeeded`.
public static class StreamToolRunner
{
    public const string ToolName = "Stream File Tool.exe";

    /// Search candidate locations (in order) for the tool:
    ///   1. Same dir as the running .exe.
    ///   2. &lt;baseDir&gt;/DONOTREMOVE/ (matches ArenaBuilder layout).
    /// Returns null if not found.
    public static string? Locate()
    {
        return EmbeddedToolExtractor.LocateOrExtract(ToolName);
    }

    /// Pack one folder. The output `.psf` is dropped next to the source folder
    /// (e.g. `cSim_Global/` → sibling `cSim_Global.psf`).
    /// Caller can check that file's existence to decide success.
    public static ToolResult Pack(
        string toolPath,
        string folderToPack,
        StreamToolType type,
        StreamToolPlatform platform,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderToPack);
        if (!File.Exists(toolPath))
            throw new FileNotFoundException("Stream File Tool not found.", toolPath);
        if (!Directory.Exists(folderToPack))
            throw new DirectoryNotFoundException($"Folder to pack not found: {folderToPack}");

        return ToolRunner.Run(
            toolPath,
            new[]
            {
                "pack",
                $"--folder={folderToPack}",
                $"--type={TypeFlag(type)}",
                $"--platform={PlatformFlag(platform)}",
            },
            workingDirectory: Path.GetDirectoryName(toolPath),
            timeout: timeout);
    }

    private static string TypeFlag(StreamToolType t) => t switch
    {
        StreamToolType.Sim => "sim",
        StreamToolType.Pres => "pres",
        StreamToolType.Tex => "tex",
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    private static string PlatformFlag(StreamToolPlatform p) => p switch
    {
        StreamToolPlatform.Ps3 => "p",
        StreamToolPlatform.Xbox => "x",
        StreamToolPlatform.Wii => "w",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };
}
