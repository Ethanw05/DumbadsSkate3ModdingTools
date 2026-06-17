using System.Text.Json;

namespace ArenaBuilder.WinForms;

/// <summary>
/// Disk-backed persistence for the last-used GLB input folder and DIST/stream output folder, so the
/// map maker remembers them across sessions. Stored as JSON in
/// <c>%AppData%\ArenaBuilder\folderpaths.json</c>. All I/O is best-effort: a missing or corrupt file
/// yields empty defaults, and a failed write never throws into the UI.
/// </summary>
internal sealed class FolderPathSettings
{
    public string? GlbFolder { get; set; }
    public string? DistOutputFolder { get; set; }

    private static string FilePath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ArenaBuilder");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "folderpaths.json");
        }
    }

    public static FolderPathSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<FolderPathSettings>(File.ReadAllText(FilePath))
                       ?? new FolderPathSettings();
        }
        catch
        {
            // Corrupt/unreadable settings — fall back to empty defaults rather than crashing startup.
        }
        return new FolderPathSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // Best-effort: never let a settings write break the UI.
        }
    }
}
