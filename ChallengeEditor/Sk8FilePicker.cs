using System.Windows.Forms;

namespace ChallengeEditor;

/// File-picker dialog for File ▸ Import Sk8 Map. Mirrors
/// <see cref="SceneFilePicker"/>; uses WinForms (already a project reference
/// via <c>UseWindowsForms=true</c>). Returns null on cancel.
public static class Sk8FilePicker
{
    private const string FilterText = "Sk8 Map (*.sk8)|*.sk8|All Files (*.*)|*.*";
    private const string DefaultExt = "sk8";

    public static string? PickOpen(string title, string? initialPath = null)
    {
        using var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = FilterText,
            DefaultExt = DefaultExt,
            CheckFileExists = true,
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(initialPath) && File.Exists(initialPath))
            dlg.FileName = initialPath;
        return dlg.ShowDialog() == DialogResult.OK ? dlg.FileName : null;
    }
}
