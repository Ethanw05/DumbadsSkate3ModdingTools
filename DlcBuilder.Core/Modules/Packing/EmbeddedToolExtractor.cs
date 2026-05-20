using System.Reflection;

namespace DlcBuilder.Modules.Packing;

/// Extracts packer executables embedded in DlcBuilder.Core for single-file hosts.
internal static class EmbeddedToolExtractor
{
    private static readonly object Sync = new();

    public static string? LocateOrExtract(string toolFileName)
    {
        string baseDir = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        string[] candidates =
        {
            Path.Combine(baseDir, toolFileName),
            Path.Combine(baseDir, "DONOTREMOVE", toolFileName),
        };
        foreach (string c in candidates)
            if (File.Exists(c)) return c;

        return ExtractFromEmbeddedResource(toolFileName);
    }

    private static string? ExtractFromEmbeddedResource(string toolFileName)
    {
        lock (Sync)
        {
            Assembly asm = typeof(EmbeddedToolExtractor).Assembly;
            string? resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("." + toolFileName, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(resName))
                return null;

            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChallengeEditor",
                "embedded-tools");
            Directory.CreateDirectory(root);
            string outPath = Path.Combine(root, toolFileName);

            using Stream? src = asm.GetManifestResourceStream(resName);
            if (src == null) return null;

            using (var dst = File.Create(outPath))
                src.CopyTo(dst);

            return outPath;
        }
    }
}

