using System.Reflection;

namespace DlcBuilder.Modules.MissionTemplates;

internal static class MissionTemplateProvider
{
    private const string DiskRelativeDir = "lib\\mission_template";

    internal static bool TryGetTemplateBytes(string suffix, out byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);

        // First preference: embedded resources so single-file publish can be truly portable.
        if (TryGetEmbeddedBytes(suffix, out bytes))
            return true;

        // Backward-compatible fallback for local dev layouts.
        string diskPath = Path.Combine(AppContext.BaseDirectory, DiskRelativeDir, "mission_template" + suffix);
        if (File.Exists(diskPath))
        {
            bytes = File.ReadAllBytes(diskPath);
            return true;
        }

        bytes = Array.Empty<byte>();
        return false;
    }

    private static bool TryGetEmbeddedBytes(string suffix, out byte[] bytes)
    {
        Assembly asm = typeof(MissionTemplateProvider).Assembly;
        string expectedTail = $".lib.mission_template.mission_template{suffix.Replace('\\', '.').Replace('/', '.')}";

        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.EndsWith(expectedTail, StringComparison.OrdinalIgnoreCase))
                continue;

            using Stream? stream = asm.GetManifestResourceStream(name);
            if (stream == null)
                continue;

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            bytes = ms.ToArray();
            return true;
        }

        bytes = Array.Empty<byte>();
        return false;
    }
}
